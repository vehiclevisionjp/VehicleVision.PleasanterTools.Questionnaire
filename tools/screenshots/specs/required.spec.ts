import { expect, test } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareRequiredSurvey } from '../lib/required';

/**
 * **必須の断り方とページの行き来**を確かめる（Issue #120）。
 *
 * **確かめたいのは 3 つ。**
 *
 * 1. **空の必須では進ませない。** 断りの文言が、その設問の場所に出ること。
 *    まとめて上に出すだけでは、どれを直せばよいか分からない
 * 2. **「戻る」で入れたものが消えない。** 消えると、長いアンケートほど答えるのを諦める
 * 3. **書式の誤り（メール）も同じ断り方で出る**
 *
 * **単体試験では確かめられない。** 判定そのものは `validation.ts` の試験で見ているが、
 * **押した結果として画面が留まり、文言がその場に出る**かは通してみないと分からない。
 */
const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

test.describe.configure({ mode: 'serial' });

test.describe('必須の断り方とページの行き来', () => {
  /** 回答画面を開くための公開 ID。**下ごしらえで作る。** */
  let publicId = '';

  test.beforeAll(async ({ browser }) => {
    // **初期設定から通すと 30 秒では足りない。** 管理者の登録・2 要素・復旧コードを
    // 順に踏むため、**まっさらな環境で単独に走らせたときだけ**時間がかかる
    test.setTimeout(120_000);

    const authFile = await ensureAdminStorageState(browser, baseUrl);
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      publicId = (await prepareRequiredSurvey(context.request, demoSiteId)).publicId;
    } finally {
      await context.close();
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを公開できていない');
    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '必須とページ送りの見本' })).toBeVisible();
  });

  test('空の必須では次のページへ進めない', async ({ page }) => {
    await page.getByRole('button', { name: '次へ' }).click();

    // **断りはその設問の場所に出る**
    await expect(page.getByRole('alert').filter({ hasText: '回答してください' })).toBeVisible();
    await expect(page.getByLabel('お名前')).toHaveAttribute('aria-invalid', 'true');

    // **ページは動いていない**
    await expect(page.getByRole('heading', { name: 'お客様について' })).toBeVisible();
    await expect(page.getByText('1 / 2 ページ')).toBeVisible();
  });

  test('答えると断りが消えて次のページへ進む', async ({ page }) => {
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByRole('alert').filter({ hasText: '回答してください' })).toBeVisible();

    await page.getByLabel('お名前').fill('検証 太郎');
    await page.getByRole('button', { name: '次へ' }).click();

    await expect(page.getByRole('heading', { name: 'ご連絡先' })).toBeVisible();
    await expect(page.getByText('2 / 2 ページ')).toBeVisible();

    // **最後のページでは「送信する」に変わる**
    await expect(page.getByRole('button', { name: '送信する' })).toBeVisible();
    await expect(page.getByRole('button', { name: '次へ' })).toHaveCount(0);
  });

  test('戻ると入れたものが残っている', async ({ page }) => {
    await page.getByLabel('お名前').fill('検証 太郎');
    await page.getByLabel('ひとこと').fill('よろしくお願いします。');
    await page.getByRole('button', { name: '次へ' }).click();

    await expect(page.getByRole('heading', { name: 'ご連絡先' })).toBeVisible();
    await page.getByLabel('連絡先メールアドレス').fill('taro@example.com');

    await page.getByRole('button', { name: '戻る' }).click();

    await expect(page.getByLabel('お名前')).toHaveValue('検証 太郎');
    await expect(page.getByLabel('ひとこと')).toHaveValue('よろしくお願いします。');

    // **もう一度進めても、先のページの入力が消えていない**
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByLabel('連絡先メールアドレス')).toHaveValue('taro@example.com');
  });

  test('最初のページには「戻る」が出ない', async ({ page }) => {
    await expect(page.getByRole('button', { name: '戻る' })).toHaveCount(0);
  });

  test('書式の誤りも同じ断り方で出る', async ({ page }) => {
    await page.getByLabel('お名前').fill('検証 太郎');
    await page.getByRole('button', { name: '次へ' }).click();

    await page.getByLabel('連絡先メールアドレス').fill('これはメールではない');
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'メールアドレスの形式で入力してください' }),
    ).toBeVisible();

    // **送れていないので、お礼の画面へは行っていない**
    await expect(page.getByText('ありがとうございました。')).toHaveCount(0);
  });
});
