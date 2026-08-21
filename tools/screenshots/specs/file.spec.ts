import { expect, test } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareFileSurvey } from '../lib/file';

/**
 * **ファイル添付**の見え方と断り方を確かめる（Issue #120）。
 *
 * **確かめたいのは 3 つ。**
 *
 * 1. **上限を選ぶ前に伝える。** 選び終えてから弾かれると、選び直しになる
 * 2. **選んだものが名前で見える。** 何を付けたのか分からないまま送らせない
 * 3. **上限に触れたら、その設問の場所で断る**（件数・大きさ・必須）
 *
 * **単体試験では確かめられない。** 判定は `validation.ts` の試験で見ているが、
 * **`<input type="file">` に入れた結果として画面がどう変わるか**は通してみないと分からない。
 */
const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

/** 上限（1024 バイト）に収まる見本。 */
const smallFile = {
  name: 'small.txt',
  mimeType: 'text/plain',
  buffer: Buffer.from('a'.repeat(100)),
};

/** 上限を超える見本。**1 バイト超えるだけで断られること。** */
const largeFile = {
  name: 'large.txt',
  mimeType: 'text/plain',
  buffer: Buffer.from('a'.repeat(1025)),
};

test.describe.configure({ mode: 'serial' });

test.describe('ファイル添付', () => {
  /** 回答画面を開くための公開 ID。**下ごしらえで作る。** */
  let publicId = '';

  test.beforeAll(async ({ browser }) => {
    // **初期設定から通すと 30 秒では足りない。** 管理者の登録・2 要素・復旧コードを
    // 順に踏むため、**まっさらな環境で単独に走らせたときだけ**時間がかかる
    test.setTimeout(120_000);

    const authFile = await ensureAdminStorageState(browser, baseUrl);
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      publicId = (await prepareFileSurvey(context.request, demoSiteId)).publicId;
    } finally {
      await context.close();
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを公開できていない');
    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: 'ファイル添付の見本' })).toBeVisible();
  });

  test('上限は選ぶ前に伝える', async ({ page }) => {
    await expect(page.getByText('2 件まで')).toBeVisible();
    // **数は 3 桁ごとに区切って出る**（`Intl.NumberFormat`）
    await expect(page.getByText('1 件 1,024 バイトまで')).toBeVisible();
  });

  test('必須の添付を空のまま送れない', async ({ page }) => {
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'ファイルを選んでください' }),
    ).toBeVisible();
    await expect(page.getByText('ありがとうございました。')).toHaveCount(0);
  });

  test('選んだファイルは名前で見える', async ({ page }) => {
    await page.getByLabel('写真').setInputFiles([smallFile]);

    await expect(page.locator('.files li')).toHaveText(['small.txt']);
  });

  test('大きすぎるファイルはその場で断る', async ({ page }) => {
    await page.getByLabel('写真').setInputFiles([largeFile]);
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'large.txt: ファイルが大きすぎます' }),
    ).toBeVisible();
  });

  test('件数の上限を超えたら断る', async ({ page }) => {
    await page.getByLabel('写真').setInputFiles([
      { ...smallFile, name: 'a.txt' },
      { ...smallFile, name: 'b.txt' },
      { ...smallFile, name: 'c.txt' },
    ]);
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'ファイルは 2 件までです' }),
    ).toBeVisible();
  });

  test('選び直すと前の断りが消える', async ({ page }) => {
    await page.getByLabel('写真').setInputFiles([largeFile]);
    await page.getByRole('button', { name: '送信する' }).click();
    await expect(
      page.getByRole('alert').filter({ hasText: 'large.txt: ファイルが大きすぎます' }),
    ).toBeVisible();

    await page.getByLabel('写真').setInputFiles([smallFile]);
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'ファイルが大きすぎます' }),
    ).toHaveCount(0);
  });
});
