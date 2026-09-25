import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import type { Server } from 'node:net';
import { ensureAdminStorageState } from '../lib/admin';
import { startForward, stopForward } from '../lib/tcp-forward';
import { findTofu, japaneseSamples } from '../lib/tofu';
import { readEnrollmentSecret, totp } from '../lib/totp';

/**
 * Pleasanter のログインで管理画面へ入る機能（Issue #464）を、ブラウザで確かめる（Issue #470）。
 *
 * - 設定画面から有効にする
 * - 外部設定で決まった項目のロック表示
 * - ログイン画面の「Pleasanter でログイン」→ 別窓で Pleasanter にログイン → 管理画面へ入る往復
 *
 * **Pleasanter の利用者は `tools/pleasanter-testenv/seed/03_sso_users.sql` が入れておく**
 * （e2e.yml の写しの一式の job）。
 *
 * **Pleasanter と本アプリを同じホスト名で開く。** 容器の中に `localhost:8080`（Pleasanter）と
 * `localhost:8081`（本アプリ）の素通しを立てる（`lib/tcp-forward.ts`）。
 * 本アプリの設定の「ログイン画面」も `http://localhost:8080/users/login` にする。
 *
 * ⚠️ **設定は画面から有効にし、終わったら無効へ戻す。** 写しの一式の本アプリは外部設定を持たない。
 * 有効のまま残すと、後に走る spec のログイン画面に釦が増える。
 * ⚠️ **まっさらな検証環境が前提。** 往復で本アプリに作られる管理者（その場で登録）は、
 * 2 回目には 2 要素を登録済みになり、途中の画面が変わる。
 */

const shots = 'shots';

/** 容器の網の中の Pleasanter。**本アプリのサーバから見た URL にも使う。** */
const pleasanterHost = process.env['SSO_PLEASANTER_HOST'] ?? 'pleasanter';
const pleasanterPort = Number(process.env['SSO_PLEASANTER_PORT'] ?? '8080');
const appHost = process.env['SSO_APP_HOST'] ?? 'questionnaire-app';
const appPort = Number(process.env['SSO_APP_PORT'] ?? '8080');

/** ブラウザから見た Pleasanter と本アプリ（同じホスト名 `localhost`）。 */
const browserPleasanterPort = 8080;
const browserAppPort = 8081;
const browserPleasanterUrl = `http://localhost:${browserPleasanterPort}`;
const browserAppUrl = `http://localhost:${browserAppPort}`;

const ssoUser = { loginId: 'sso-e2e-plain', password: 'SsoE2e#Plain1' };

const settingsPath = '/api/admin/pleasanter-sso/settings';

/**
 * **素通しを立てない。** 容器の外（手元）で走らせ、`localhost:8080` と `localhost:8081` に
 * Pleasanter と本アプリが既に居るときだけ `1` にする。写しの一式の容器では使わない
 */
const skipForward = process.env['SSO_SKIP_FORWARD'] === '1';

let forwards: Server[] = [];
let storageState = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL, playwright }) => {
  test.setTimeout(240_000);

  if (!skipForward) {
    forwards = await Promise.all([
      startForward(browserPleasanterPort, pleasanterHost, pleasanterPort),
      startForward(browserAppPort, appHost, appPort),
    ]);
  }

  // **Pleasanter の画面が出るまで待つ。** 起動の直後は待受だけ開いて 500 を返すことがある
  const request = await playwright.request.newContext();
  try {
    await expect
      .poll(
        async () => {
          try {
            return (await request.get(`${browserPleasanterUrl}/users/login`)).status();
          } catch {
            return 0;
          }
        },
        { timeout: 180_000, intervals: [2_000] },
      )
      .toBe(200);
  } finally {
    await request.dispose();
  }

  storageState = await ensureAdminStorageState(browser, baseURL ?? '');
});

test.afterAll(async ({ browser, baseURL }) => {
  test.setTimeout(60_000);

  // **無効へ戻す。** 後に走る spec のログイン画面を変えない
  if (storageState !== '') {
    const context = await browser.newContext({ baseURL, storageState });
    try {
      await saveSettings(context.request, { enabled: false });
    } finally {
      await context.close();
    }
  }

  await Promise.all(forwards.map((server) => stopForward(server)));
});

test('設定画面から Pleasanter のログインを有効にする', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL, storageState });
  try {
    const page = await context.newPage();
    await page.goto('/admin/pleasanter-sso-settings');
    await expect(page.getByRole('heading', { name: 'Pleasanter ログイン設定' })).toBeVisible();

    // **外部設定が無いので、どの項目もロックされていない**
    await expect(page.getByText('設定で固定されています')).toHaveCount(0);

    // ---- 有効にする
    await page.getByLabel('Pleasanter のログインを有効にする').check();
    await page.getByLabel('Pleasanter の内部 URL').fill(`http://${pleasanterHost}:${pleasanterPort}/`);
    await page.getByLabel('Pleasanter のログイン画面').fill(`${browserPleasanterUrl}/users/login`);
    // **往復で使う利用者は本アプリに居ないので、その場で登録させる**（役割は既定の編集者）
    await page.getByLabel('未登録の利用者').selectOption('Register');
    await page.getByLabel('その場で登録するときの役割').selectOption('Editor');

    await page.getByRole('button', { name: '保存する' }).click();
    await expect(page.getByText('Pleasanter ログイン設定を保存しました。')).toBeVisible();

    await shoot(page, 'admin-19-pleasanter-sso-settings');

    // **保存した値が読み直しても残っている**
    const saved = (await (await context.request.get(settingsPath)).json()) as Record<string, unknown>;
    expect(saved['enabled']).toBe(true);
    expect(saved['unknownUser']).toBe('Register');
  } finally {
    await context.close();
  }
});

test('外部設定で決まった項目はロックして出す', async ({ browser, baseURL }) => {
  // ⚠️ **写しの一式の本アプリは外部設定を持たない**（持たせると画面から有効にできない）。
  // そこで**サーバの応答の fixedFields だけを「全部固定」に書き換えて**、画面の出し方を確かめる。
  // サーバが外部設定を優先して保存を拒むことは、端から端まで通す試験
  // （PleasanterSsoEndToEndTests。外部設定で有効にした本アプリ）が確かめている
  const context = await browser.newContext({ baseURL, storageState });
  try {
    const page = await context.newPage();
    await page.route(`**${settingsPath}`, async (route) => {
      if (route.request().method() !== 'GET') {
        await route.continue();
        return;
      }

      const response = await route.fetch();
      const body = (await response.json()) as { fixedFields: Record<string, boolean> };
      for (const key of Object.keys(body.fixedFields)) {
        body.fixedFields[key] = true;
      }
      await route.fulfill({ response, json: body });
    });

    await page.goto('/admin/pleasanter-sso-settings');
    await expect(page.getByRole('heading', { name: 'Pleasanter ログイン設定' })).toBeVisible();

    // **すべての項目に印が付き、どの欄も触れない**（項目の数は決め打ちにしない）
    const form = page.locator('form');
    const controls = await form.locator('input, select').all();
    expect(controls.length).toBeGreaterThan(0);
    await expect(page.getByText('設定で固定されています')).toHaveCount(controls.length);
    for (const control of controls) {
      await expect(control).toBeDisabled();
    }
  } finally {
    await context.close();
  }
});

test('ログイン画面の釦から別窓で Pleasanter にログインし、管理画面へ入る', async ({ browser }) => {
  test.setTimeout(120_000);

  // **本アプリも localhost で開く。** Pleasanter の cookie が届くのは同じホスト名だけ
  const context = await browser.newContext({ baseURL: browserAppUrl });
  try {
    const page = await context.newPage();
    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: '管理画面にログイン' })).toBeVisible();

    const button = page.getByRole('button', { name: 'Pleasanter でログイン' });
    await expect(button).toBeVisible();
    await shoot(page, 'admin-20-pleasanter-login');

    // ---- 釦を押すと、Pleasanter のログイン画面が別窓で開く
    const popupPromise = page.waitForEvent('popup');
    await button.click();
    const popup = await popupPromise;
    await popup.waitForURL(`${browserPleasanterUrl}/users/login**`);

    // **待っている間の案内が出る**
    await expect(page.getByText('開いたウィンドウで Pleasanter にログインしてください。', { exact: false }))
      .toBeVisible();

    // ---- 別窓で Pleasanter にログインする（Pleasanter の画面そのもの）
    await popup.locator('#Users_LoginId').fill(ssoUser.loginId);
    await popup.locator('#Users_Password').fill(ssoUser.password);
    await popup.locator('#Login').click();

    // ---- 本アプリが気付いて先へ進み、別窓を閉じる。
    // 検証環境は 2 要素が必須なので、その場で登録された管理者は 2 要素の登録へ進む
    await expect(page.getByRole('heading', { name: '2 要素認証を登録する' })).toBeVisible({
      timeout: 30_000,
    });
    await expect.poll(() => popup.isClosed(), { timeout: 10_000 }).toBe(true);

    const secret = await readEnrollmentSecret(page);
    await page.getByLabel('認証アプリに表示された 6 桁のコード').fill(totp(secret));
    await page.getByRole('button', { name: '登録する' }).click();

    await page.getByRole('heading', { name: '復旧コードを控えてください' }).waitFor();
    await page.getByLabel('控えました').check();
    await page.getByRole('button', { name: '管理画面へ進む' }).click();

    await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();

    // **Pleasanter のログインで入ったセッションになっている**
    const session = (await (await page.request.get('/api/admin/session')).json()) as Record<string, unknown>;
    expect(session['authenticated']).toBe(true);
    expect(session['loginId']).toBe(ssoUser.loginId);
    expect(session['viaPleasanterSso']).toBe(true);
  } finally {
    await context.close();
  }
});

/** 設定の一部だけを変えて保存する（ほかの項目は今の値のまま）。 */
async function saveSettings(
  request: APIRequestContext,
  changes: Record<string, unknown>,
): Promise<void> {
  const current = (await (await request.get(settingsPath)).json()) as Record<string, unknown>;
  delete current['fixedFields'];
  const response = await request.put(settingsPath, { data: { ...current, ...changes } });
  expect(response.ok(), `Pleasanter ログイン設定を保存できなかった: ${response.status()}`).toBe(true);
}

/** 撮った写しに豆腐が無いことを、撮るたびに確かめる。 */
async function shoot(page: Page, name: string): Promise<void> {
  const report = await findTofu(page, japaneseSamples);
  expect(
    report.tofu,
    `${name} を撮る前に日本語が描けていない。豆腐: ${report.tofu.join('')} / 書体: ${report.fontFamily}`,
  ).toEqual([]);

  await page.screenshot({ path: `${shots}/${name}.png`, fullPage: true });
}
