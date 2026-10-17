import { expect, test, type Locator, type Page } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareFeatureSurvey } from '../lib/features';
import { findTofu, japaneseSamples } from '../lib/tofu';

const authFile = '.auth.json';
const shots = 'shots';
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL }) => {
  await ensureAdminStorageState(browser, baseURL ?? '');
});

test.describe('v0.12.0 の取説用の写し', { tag: '@standalone' }, () => {
  test.use({ storageState: authFile });

  let surveyId = '';
  let publicId = '';

  test.beforeAll(async ({ browser, baseURL }) => {
    const context = await browser.newContext({ baseURL, storageState: authFile });
    try {
      ({ surveyId, publicId } = await prepareFeatureSurvey(context.request, demoSiteId));
    } finally {
      await context.close();
    }
  });

  test('設問の移動で壊れる候補を撮る', async ({ page }) => {
    await openEditor(page, surveyId);

    await page
      .locator('article.question', { hasText: 'サービスを利用していますか' })
      .getByRole('button', { name: '移動' })
      .click();

    const dialog = page.getByRole('dialog', { name: '設問を移動' });
    await expect(dialog.getByText('この位置では定義が壊れます。')).toBeVisible();
    await shoot(page, dialog, 'admin-21-question-reorder');
  });

  test('ページの移動で壊れる候補を撮る', async ({ page }) => {
    await openEditor(page, surveyId);

    await page.locator('section.page > .page-head').getByRole('button', { name: '移動' }).click();

    const dialog = page.getByRole('dialog', { name: 'ページを移動' });
    await expect(dialog.getByText('この位置では定義が壊れます。')).toBeVisible();
    await shoot(page, dialog, 'admin-22-page-reorder');
  });

  test('ページ下部の釦の設定を撮る', async ({ page }) => {
    await openEditor(page, surveyId);

    await page.getByText('ページ下部の釦', { exact: true }).click();
    await expect(page.getByRole('heading', { name: '任意リンク' })).toBeVisible();
    await expect(page.getByLabel('リンク先 URL（http / https / mailto）')).toHaveValue(
      'https://www.example.com/guide',
    );
    await shoot(page, page.locator('section.page'), 'admin-23-page-buttons');
  });

  test('未保存の設問カードを撮る', async ({ page }) => {
    await openEditor(page, surveyId);

    const question = page.locator('article.question').nth(1);
    await question.getByPlaceholder('設問文').fill('利用しているサービス名（変更）');
    await expect(question.getByLabel('未保存')).toBeVisible();
    await shoot(page, question, 'admin-24-unsaved-question');
  });

  test('備考と無効な次へ釦を回答画面で撮る', async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '新機能の見本' })).toBeVisible();
    await expect(page.getByText('継続して利用している方を選んでください。')).toBeVisible();
    await expect(page.getByText('ポータルの通知一覧で確認できます。')).toBeVisible();
    await expect(page.getByRole('button', { name: '次へ' })).toBeDisabled();
    await expect(page.getByText('条件を満たしていないため押せません')).toBeVisible();
    await shoot(page, page.locator('main'), 'answer-11-choice-remarks-disabled-next');
  });

  test('無効な送信釦と理由を回答画面で撮る', async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);
    await page.getByRole('radio', { name: '利用している' }).check();
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByRole('heading', { name: '確認' })).toBeVisible();
    await expect(page.getByRole('button', { name: '送信する' })).toBeDisabled();
    await expect(page.getByText('条件を満たしていないため押せません')).toBeVisible();
    await shoot(page, page.locator('main'), 'answer-12-disabled-submit');
  });
});

async function openEditor(page: Page, surveyId: string): Promise<void> {
  test.skip(surveyId === '', '下ごしらえでアンケートを作れていない');
  await page.goto(`/admin/surveys/${surveyId}`);
  await expect(page.getByRole('tablist', { name: 'ページ' })).toBeVisible();
}

async function shoot(page: Page, target: Locator, name: string): Promise<void> {
  const report = await findTofu(page, japaneseSamples);
  expect(
    report.tofu,
    `${name} を撮る前に日本語が描けていない。豆腐: ${report.tofu.join('')} / 書体: ${report.fontFamily}`,
  ).toEqual([]);

  await target.screenshot({ path: `${shots}/${name}.png` });
}
