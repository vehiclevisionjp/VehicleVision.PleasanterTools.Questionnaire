import { expect, test } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareNoteSurvey } from '../lib/note';

/**
 * 説明文ブロックの書式が**書いたとおりに出る**ことを確かめる（Issue #108 / #120）。
 *
 * **確かめたいのは 2 つ。**
 *
 * 1. **付けた書式が実際の要素になっている。** 太字が `<strong>`、箇条書きが `<ul>`。
 *    平文のまま出ていては、書式を足した意味が無い
 * 2. **素通しの経路が無い。** HTML を書いても要素にならず、`https:` 以外はリンクにならない
 *
 * **ここでしか確かめられない。** サーバ側の試験は構造まで、画面側の単体試験は関数まで。
 * **記法 → 構造 → DOM が端から端まで繋がっていることを見るのはここだけ。**
 */
const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

test.describe.configure({ mode: 'serial' });

test.describe('説明文ブロックの書式', () => {
  /** 回答画面を開くための公開 ID。**下ごしらえで作る。** */
  let publicId = '';

  test.beforeAll(async ({ browser }) => {
    // **初期設定から通すと 30 秒では足りない。** 管理者の登録・2 要素・復旧コードを
    // 順に踏むため、**まっさらな環境で単独に走らせたときだけ**時間がかかる
    test.setTimeout(120_000);

    const authFile = await ensureAdminStorageState(browser, baseUrl);
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      publicId = (await prepareNoteSurvey(context.request, demoSiteId)).publicId;
    } finally {
      await context.close();
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを公開できていない');
    await page.goto(`/f/${publicId}`);
  });

  test('見出しと段落が要素として出る', async ({ page }) => {
    const note = page.locator('.note');

    // **記号がそのまま残っていないこと。** 残っていれば記法が読まれていない
    await expect(note).not.toContainText('## ご案内');
    await expect(note.getByRole('heading', { name: 'ご案内' })).toBeVisible();
    await expect(note.locator('p').first()).toContainText('太字');
  });

  test('太字と斜体が装飾として出る', async ({ page }) => {
    const note = page.locator('.note');

    await expect(note).not.toContainText('**太字**');
    await expect(note.locator('strong')).toHaveText('太字');
    await expect(note.locator('em')).toHaveText('斜体');
  });

  test('箇条書きが並びとして出る', async ({ page }) => {
    const note = page.locator('.note');

    await expect(note.locator('ul li')).toHaveCount(2);
    await expect(note.locator('ol li')).toHaveCount(2);
    await expect(note.locator('ul li').first()).toHaveText('1 つめの項目');
  });

  test('リンクは新しいタブで開き、開いた先へ元の画面を触らせない', async ({ page }) => {
    const link = page.locator('.note a', { hasText: '会社の案内' });

    await expect(link).toHaveAttribute('href', 'https://www.example.com/guide');
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', /noopener/);
  });

  // **素通しの経路を作らない**（Issue #108）。書いても要素にはならない
  test('HTML を書いても要素にならない', async ({ page }) => {
    const note = page.locator('.note');

    // 文字としては残る。**消さずに平文で残すのが決め事**
    await expect(note).toContainText('太字のつもりの HTML');

    // 要素としては生えていない
    await expect(note.locator('b')).toHaveCount(0);
    await expect(note.locator('script')).toHaveCount(0);
  });

  // **`https:` 以外はリンクにしない。** 平文の行き先へ回答者を送らない
  test('https 以外のリンクはリンクにならない', async ({ page }) => {
    const note = page.locator('.note');

    await expect(note).toContainText('危ない行き先');
    await expect(note.locator('a[href^="http://"]')).toHaveCount(0);
  });

  // **説明文ブロックは設問ではない。** 入力欄も必須の印も持たない
  test('説明文ブロックに入力欄は出ない', async ({ page }) => {
    await expect(page.locator('.note input')).toHaveCount(0);
    await expect(page.locator('.note textarea')).toHaveCount(0);

    // **後ろの設問は普通に出る。** 説明文が描画を止めていないこと
    await expect(page.getByLabel('お名前')).toBeVisible();
  });
});
