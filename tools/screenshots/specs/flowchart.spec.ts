import { expect, test } from '@playwright/test';
import { prepareFlowchartDraft } from '../lib/branching';
import { ensureAdminStorageState } from '../lib/admin';
import { expectNoAdminYet } from '../lib/fresh';
import { findTofu, japaneseSamples } from '../lib/tofu';

const authFile = '.auth.json';
const shots = 'shots';
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL });
  try {
    const page = await context.newPage();
    await page.goto('/admin');
    await expectNoAdminYet(page);
  } finally {
    await context.close();
  }

  await ensureAdminStorageState(browser, baseURL ?? '');
});

test.describe('分岐の全体図の写し', { tag: '@standalone' }, () => {
  test('不備のない分岐の全体図を撮る', async ({ browser, baseURL }) => {
    const context = await signedInContext(browser, baseURL ?? '');
    try {
      const page = await context.newPage();
      const survey = await prepareFlowchartDraft(page.request, demoSiteId);

      await page.goto(`/admin/surveys/${survey.surveyId}`);
      await page.getByRole('button', { name: '分岐の全体図' }).click();

      const dialog = page.getByRole('dialog', { name: '分岐の全体図' });
      await expect(dialog.getByText('選択肢による行き先', { exact: true }).first()).toBeVisible();
      await expect(dialog.getByText('ページ末尾の行き先', { exact: true }).first()).toBeVisible();
      await expect(dialog.getByText('分岐に不備はありません。')).toBeVisible();
      await expect(dialog.getByRole('heading', { name: '凡例' })).toBeVisible();
      await expect(dialog.getByRole('heading', { name: 'ページと行き先' })).toBeVisible();

      await shoot(page, 'admin-15-flowchart');
    } finally {
      await context.close();
    }
  });

  test('不備のある分岐の全体図を撮る', async ({ browser, baseURL }) => {
    const context = await signedInContext(browser, baseURL ?? '');
    try {
      const page = await context.newPage();
      const survey = await prepareFlowchartDraft(page.request, demoSiteId, true);

      await page.goto(`/admin/surveys/${survey.surveyId}`);
      await page.getByRole('button', { name: '分岐の全体図' }).click();

      const dialog = page.getByRole('dialog', { name: '分岐の全体図' });
      await expect(dialog.getByText('どこからも来られない', { exact: true }).first()).toBeVisible();
      await expect(
        dialog.getByText('! 存在しないページ（missing-page）', { exact: true }),
      ).toBeVisible();
      await expect(dialog.getByRole('heading', { name: '凡例' })).toBeVisible();
      await expect(dialog.getByRole('heading', { name: 'ページと行き先' })).toBeVisible();

      await shoot(page, 'admin-16-flowchart-problems');
    } finally {
      await context.close();
    }
  });
});

async function signedInContext(
  browser: import('@playwright/test').Browser,
  baseURL: string,
): Promise<import('@playwright/test').BrowserContext> {
  return browser.newContext({
    baseURL,
    storageState: authFile,
    viewport: { width: 1920, height: 1800 },
  });
}

async function shoot(page: import('@playwright/test').Page, name: string): Promise<void> {
  const report = await findTofu(page, japaneseSamples);
  expect(
    report.tofu,
    `${name} を撮る前に日本語が描けていない。豆腐: ${report.tofu.join('')} / 書体: ${report.fontFamily}`,
  ).toEqual([]);

  await page.screenshot({ path: `${shots}/${name}.png`, fullPage: true });
}
