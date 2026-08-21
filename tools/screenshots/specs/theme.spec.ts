import { expect, test, type Page } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { createSurvey, prepareThemeSurvey, saveTheme } from '../lib/theme';

/**
 * **配色が回答画面に本当に効くことを確かめる**（Issue #109 / #124）。
 *
 * **確かめたいのは 4 つ。**
 *
 * 1. **名前の付いた配色を選ぶと、実際に色が変わる。**
 *    サーバは「どれを選んだか」しか持たないので、
 *    **色の中身は画面側の写し**（`lib/theme.ts`）。効いているかは通さないと分からない
 * 2. **個別の指定が名前の付いた配色より優先される**
 * 3. **変数を入れただけでなく、描かれた色まで届いている**
 * 4. **CSS へ流し込めない値はサーバが受け取らない。**
 *    画面側で捨てる前に、そもそも保存させない
 *
 * **単体試験では確かめられない。** `applyTheme` の試験は `setProperty` の呼び方まで。
 * **ブラウザが本当にその値を採ったか**は、計算後の値を読まないと分からない。
 */
const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

/** `THEME_PRESET_COLORS.Forest` の値（`Frontend/src/lib/theme.ts`）。 */
const forest = {
  accent: '#15803d',
  background: '#f6faf6',
  text: '#14261a',
};

/** 個別に被せる色。**Forest の accent とは別の値にする。** */
const overriddenAccent = '#b91c1c';

/** `:root` に入った変数を、ブラウザが計算した形で読む。 */
async function cssVariable(page: Page, name: string): Promise<string> {
  return page.evaluate(
    (variable) => getComputedStyle(document.documentElement).getPropertyValue(variable).trim(),
    name,
  );
}

test.describe.configure({ mode: 'serial' });

test.describe('配色', () => {
  /** ログイン済みの控え。**色を断られることの確認でも使う。** */
  let authFile = '';

  /** 名前の付いた配色だけを指定したもの。 */
  let presetId = '';

  /** 名前の付いた配色に個別の指定を被せたもの。 */
  let customId = '';

  test.beforeAll(async ({ browser }) => {
    // **初期設定から通すと 30 秒では足りない。** 管理者の登録・2 要素・復旧コードを
    // 順に踏むため、**まっさらな環境で単独に走らせたときだけ**時間がかかる
    test.setTimeout(120_000);

    authFile = await ensureAdminStorageState(browser, baseUrl);
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      presetId = (
        await prepareThemeSurvey(context.request, demoSiteId, { preset: 'Forest' }, '配色の見本')
      ).publicId;

      customId = (
        await prepareThemeSurvey(
          context.request,
          demoSiteId,
          { preset: 'Forest', accentColor: overriddenAccent },
          '配色の見本（個別指定あり）',
        )
      ).publicId;
    } finally {
      await context.close();
    }
  });

  test('名前の付いた配色を選ぶと色が変わる', async ({ page }) => {
    test.skip(presetId === '', '下ごしらえでアンケートを公開できていない');

    await page.goto(`/f/${presetId}`);
    await expect(page.getByRole('heading', { name: '配色の見本' })).toBeVisible();

    expect(await cssVariable(page, '--accent')).toBe(forest.accent);
    expect(await cssVariable(page, '--bg')).toBe(forest.background);
    expect(await cssVariable(page, '--text')).toBe(forest.text);
  });

  test('個別の指定が名前の付いた配色より優先される', async ({ page }) => {
    test.skip(customId === '', '下ごしらえでアンケートを公開できていない');

    await page.goto(`/f/${customId}`);
    await expect(page.getByRole('heading', { name: '配色の見本（個別指定あり）' })).toBeVisible();

    // **被せた色が勝つ**
    expect(await cssVariable(page, '--accent')).toBe(overriddenAccent);

    // **被せていない色は名前の付いた配色のまま**
    expect(await cssVariable(page, '--bg')).toBe(forest.background);
  });

  test('選んだ色が実際の見た目に出る', async ({ page }) => {
    test.skip(presetId === '', '下ごしらえでアンケートを公開できていない');

    await page.goto(`/f/${presetId}`);

    // **変数を入れただけでは足りない。** 実際に描かれた色まで届いていること
    const color = await page
      .getByRole('button', { name: '送信する' })
      .evaluate((element) => getComputedStyle(element).backgroundColor);

    // `#15803d` は rgb(21, 128, 61)
    expect(color).toBe('rgb(21, 128, 61)');
  });

  // **画面側で捨てる前に、そもそも保存させない**（Issue #109）。
  // 受け付ける形は `#rgb` / `#rrggbb` だけ
  test('色として通らない値は保存の時点で断られる', async ({ browser }) => {
    test.skip(authFile === '', '下ごしらえでログインできていない');

    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      const { surveyId } = await createSurvey(context.request, demoSiteId, '配色の見本（壊れた値）');

      // **CSS を壊しにいく値。** 通ればスタイル表ごと書き換えられる
      const saved = await saveTheme(context.request, surveyId, '配色の見本（壊れた値）', {
        accentColor: 'red; } body { display: none } /*',
      });

      expect(saved.status()).toBe(400);
      const problem = (await saved.json()) as { message: string; fields: string[] };
      expect(problem.fields).toContain('AccentColor');
    } finally {
      await context.close();
    }
  });
});
