import { expect, test } from '@playwright/test';
import { demoAdmin, prepareSurvey } from '../lib/setup';
import { totp } from '../lib/totp';

/**
 * 目視の代わりに、直した画面だけを撮る。
 *
 * **取説用の一式とは別。** あちらは初期設定から順に辿るため、
 * 1 か所でも崩れると最後まで届かない。**ここは「いま見たい画面」だけに絞る。**
 *
 * ⚠️ **広い画面で撮る。** 既定の 1280 だと桁が窮屈で、
 * **直したい所が本当に直っているのか分からない。**
 */
test.use({ viewport: { width: 1920, height: 1080 } });
test.setTimeout(180000);

test('直した画面を撮る', async ({ page, context }) => {
  // ---- 初期設定から 2 要素まで ----------------------------------------------
  await page.goto('/admin');
  await page.getByRole('heading', { name: '最初の管理者を登録する' }).waitFor();
  await page.getByLabel('ログイン ID').fill(demoAdmin.loginId);
  await page.getByLabel('パスワード', { exact: true }).fill(demoAdmin.password);
  await page.getByLabel('パスワード（確認）').fill(demoAdmin.password);
  await page.getByRole('button', { name: '登録する' }).click();

  await page.getByRole('heading', { name: '2 要素認証を登録する' }).waitFor();
  const secret = (await page.locator('.secret code').innerText()).replace(/\s/g, '');
  await page.getByLabel('認証アプリに表示された 6 桁のコード').fill(totp(secret));
  await page.getByRole('button', { name: '登録する' }).click();

  await page.getByRole('heading', { name: '復旧コードを控えてください' }).waitFor();
  await page.getByLabel('控えました').check();
  await page.getByRole('button', { name: '管理画面へ進む' }).click();
  // **空の一覧には表が出ない。** 常にある釦で待つ
  await expect(page.getByRole('button', { name: '新規作成' })).toBeVisible({ timeout: 30000 });

  // ---- 見本を 2 本 ---------------------------------------------------------
  // **釦が出そろった行と、下書きのままの行を並べたい**
  const { surveyId } = await prepareSurvey(context.request, 1);
  await context.request.post('/api/admin/surveys', {
    data: { title: '下書きのままのアンケート（見本）', pleasanterSiteId: 2 },
  });

  await page.goto('/admin');
  await expect(page.getByRole('table')).toBeVisible();
  await page.screenshot({ path: 'shots/review-01-survey-list.png', fullPage: true });

  await page.goto(`/admin/surveys/${surveyId}`);
  await page.getByRole('table').first().waitFor({ timeout: 30000 });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: 'shots/review-02-survey-editor.png', fullPage: true });
  await page.screenshot({
    path: 'shots/review-03-editor-top.png',
    clip: { x: 0, y: 0, width: 1920, height: 900 },
  });
});
