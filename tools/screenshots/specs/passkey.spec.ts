import { expect, test, type CDPSession, type Page } from '@playwright/test';
import { demoAdmin } from '../lib/setup';
import { readEnrollmentSecret, totp } from '../lib/totp';

/**
 * パスキーを、実際のブラウザの画面から通す（Issue #1085。親 Core#213）。
 *
 * **仮想認証器（CDP の WebAuthn ドメイン）を使う。** 実機の端末・認証器ではない
 * （実機の確認は運用手順書の 7 章。Core#217）。ブラウザの WebAuthn の経路・画面・サーバの検証を、
 * 端から端まで自動で通すのが目的。
 *
 * - 登録（パスワードで再認証）・一覧・名前の変更
 * - ログイン画面の「パスキー」（ユーザー名なし。ディスコバラブル資格情報）。**2 要素は求められない**
 * - ログイン中の端末の一覧の `loginMethod = Passkey`
 * - 削除（パスワードで再認証）。削除したパスキーではもう入れない
 *
 * **まっさらな検証環境が前提**（管理者を最初に 1 人作る）。
 * **パスキーを有効にして起動した本アプリが対象**（`QUESTIONNAIRE_ADMIN_PASSKEY*`）。
 * 取説用のスクリーンショットの一式（`npm run shot`）には混ぜない（`@passkey` を除外している）。
 */
test.describe.configure({ mode: 'serial' });

const firstName = '仕事用 PC';
const renamedName = '自宅用 PC';

let page: Page;
let cdp: CDPSession;
let authenticatorId = '';

/** マイアカウントを開き、パスキーの一覧が読み込まれるまで待つ。 */
async function openMyAccount(): Promise<void> {
  await page.getByRole('button', { name: '自分の設定' }).click();
  await expect(page.getByRole('heading', { name: 'パスキー', exact: true })).toBeVisible();
  // 描く前に数えると空に見えるので、読み込みが終わるまで待つ
  await expect(page.getByText('読み込んでいます…')).toHaveCount(0);
}

/** 再認証の欄へパスワードを入れて続ける。 */
async function reauthenticateWithPassword(): Promise<void> {
  const form = page.locator('form.reauth');
  await form.getByLabel('パスワード').fill(demoAdmin.password);
  await form.getByRole('button', { name: '確認して続ける', exact: true }).click();
}

test.beforeAll(async ({ browser }) => {
  test.setTimeout(120_000);

  const context = await browser.newContext();
  page = await context.newPage();

  // ---- 仮想認証器 -----------------------------------------------------------
  // **ページを開く前に有効にする。** 認証器は、この CDP セッションが続く間、ナビゲーションをまたいで残る。
  cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  const added = await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: {
      protocol: 'ctap2',
      transport: 'internal',
      // **ディスコバラブル資格情報（resident key）と本人確認（UV）に対応する認証器。**
      // サーバは UV を必須にしている（運用手順書 1 章）
      hasResidentKey: true,
      hasUserVerification: true,
      isUserVerified: true,
      // 「タッチ」を自動で済ませる。人の操作を待たない
      automaticPresenceSimulation: true,
    },
  });
  authenticatorId = added.authenticatorId;

  // ---- 最初の管理者（初期設定 → 2 要素の登録）----------------------------------
  await page.goto('/admin');
  await page.getByRole('heading', { name: '最初の管理者を登録する' }).waitFor();
  await page.getByLabel('ログイン ID').fill(demoAdmin.loginId);
  await page.getByLabel('パスワード', { exact: true }).fill(demoAdmin.password);
  await page.getByLabel('パスワード（確認）').fill(demoAdmin.password);
  await page.getByRole('button', { name: '登録する' }).click();

  await page.getByRole('heading', { name: '2 要素認証を登録する' }).waitFor();
  const secret = await readEnrollmentSecret(page);
  await page.getByLabel('認証アプリに表示された 6 桁のコード').fill(totp(secret));
  await page.getByRole('button', { name: '登録する' }).click();

  await page.getByRole('heading', { name: '復旧コードを控えてください' }).waitFor();
  await page.getByLabel('控えました').check();
  await page.getByRole('button', { name: '管理画面へ進む' }).click();
  await expect(page.getByRole('button', { name: '自分の設定' })).toBeVisible({ timeout: 30_000 });
});

test.afterAll(async () => {
  await page.context().close();
});

test('安全なコンテキストで、サーバがパスキーを有効と返す', { tag: '@passkey' }, async () => {
  // **ここが通らないと、以降は原因が分からない形で落ちる。** 先に前提を見る
  expect(await page.evaluate(() => window.isSecureContext)).toBe(true);

  const response = await page.request.get('/api/admin/session');
  expect(response.ok()).toBe(true);
  expect(((await response.json()) as { passkeyEnabled?: boolean }).passkeyEnabled).toBe(true);
});

test('パスワードで再認証して登録し、一覧に出る', { tag: '@passkey' }, async () => {
  await openMyAccount();
  await expect(page.getByText('登録したパスキーはありません。')).toBeVisible();

  await page.getByLabel('このパスキーの名前').fill(firstName);
  await page.getByRole('button', { name: 'パスキーを登録する' }).click();
  await reauthenticateWithPassword();

  await expect(page.getByText('パスキーを登録しました。')).toBeVisible();
  await expect(page.locator('li', { hasText: firstName })).toBeVisible();

  // **認証器に実際に資格情報ができている**（ディスコバラブルで、本人確認つき）
  const { credentials } = await cdp.send('WebAuthn.getCredentials', { authenticatorId });
  expect(credentials).toHaveLength(1);
  expect(credentials[0]?.isResidentCredential).toBe(true);
});

test('名前を変更できる', { tag: '@passkey' }, async () => {
  await page.locator('li', { hasText: firstName }).getByRole('button', { name: '名前を変更' }).click();
  await page.getByLabel(`${firstName} の新しい名前`).fill(renamedName);
  await page.getByRole('button', { name: '保存', exact: true }).click();

  await expect(page.getByText('名前を変更しました。')).toBeVisible();
  await expect(page.locator('li', { hasText: renamedName })).toBeVisible();
  await expect(page.locator('li', { hasText: firstName })).toHaveCount(0);
});

test('ログイン画面のパスキーで、ユーザー名なし・2 要素なしで入れる', { tag: '@passkey' }, async () => {
  await page.getByRole('button', { name: 'ログアウト' }).click();
  await page.getByRole('heading', { name: '管理画面にログイン' }).waitFor();

  await page.getByRole('tab', { name: 'パスキー' }).click();
  await page.getByRole('button', { name: 'パスキーでログイン' }).click();

  // **TOTP の画面を挟まずに入れる**（パスキーは 2 要素を満たす。運用手順書 1 章）
  await expect(page.getByRole('button', { name: '自分の設定' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('heading', { name: '認証コードを入力' })).toHaveCount(0);
});

test('ログイン中の端末の一覧に、パスキーで入ったことが出る', { tag: '@passkey' }, async () => {
  // **API の値を見る。** 画面の表示名は別の確認（画面側は #1088 で扱う）
  const response = await page.request.get('/api/admin/me/sessions');
  expect(response.ok()).toBe(true);

  const { sessions } = (await response.json()) as {
    sessions: { current: boolean; loginMethod: string | null }[];
  };
  expect(sessions.find((session) => session.current)?.loginMethod).toBe('Passkey');
});

test('パスワードで再認証して削除でき、削除したパスキーでは入れない', { tag: '@passkey' }, async () => {
  await openMyAccount();
  await page.locator('li', { hasText: renamedName }).getByRole('button', { name: '削除', exact: true }).click();
  await page.getByRole('button', { name: '削除する', exact: true }).click();
  await reauthenticateWithPassword();

  await expect(page.getByText('パスキーを削除しました。')).toBeVisible();
  await expect(page.getByText('登録したパスキーはありません。')).toBeVisible();

  // 認証器には資格情報が残っている。**サーバが拒むこと**を確かめる
  await page.getByRole('button', { name: 'ログアウト' }).click();
  await page.getByRole('heading', { name: '管理画面にログイン' }).waitFor();
  await page.getByRole('tab', { name: 'パスキー' }).click();
  await page.getByRole('button', { name: 'パスキーでログイン' }).click();

  await expect(page.getByRole('alert')).toContainText('パスキーでログインできませんでした');
  await expect(page.getByRole('button', { name: '自分の設定' })).toHaveCount(0);
});
