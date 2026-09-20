import { expect, test, type Locator, type Page } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareThemeSurvey } from '../lib/theme';
import { findTofu, japaneseSamples } from '../lib/tofu';

/**
 * 読みやすさ設定を実ブラウザで切り替え、見た目と CSS 変数を確かめる（Issue #340）。
 *
 * **作成者の Forest テーマを付けた同じアンケートを使う。**
 * 高コントラストとダークが作成者の色より優先されたことを、写しと値の両方で確認する。
 */
const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');
const shots = 'shots';

const forest = {
  accent: '#15803d',
  background: '#f6faf6',
  text: '#14261a',
};

const highContrast = {
  accent: '#0047b3',
  background: '#ffffff',
  text: '#000000',
};

const dark = {
  accent: '#8ab4f8',
  background: '#121212',
  text: '#f5f5f5',
};

async function cssVariable(page: Page, name: string): Promise<string> {
  return page.evaluate(
    (variable) => getComputedStyle(document.documentElement).getPropertyValue(variable).trim(),
    name,
  );
}

async function expectColors(
  page: Page,
  colors: { accent: string; background: string; text: string },
): Promise<void> {
  expect(await cssVariable(page, '--accent')).toBe(colors.accent);
  expect(await cssVariable(page, '--bg')).toBe(colors.background);
  expect(await cssVariable(page, '--text')).toBe(colors.text);
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const sizes = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
    overflowing: Array.from(document.querySelectorAll<HTMLElement>('body *'))
      .filter((element) => {
        const bounds = element.getBoundingClientRect();
        return bounds.right > document.documentElement.clientWidth + 1;
      })
      .slice(0, 10)
      .map((element) => ({
        element: `${element.tagName.toLowerCase()}.${element.className}`,
        right: Math.round(element.getBoundingClientRect().right),
        text: element.innerText.slice(0, 40),
      })),
  }));
  expect(
    sizes.documentWidth,
    `横へ溢れた要素: ${JSON.stringify(sizes.overflowing)}`,
  ).toBeLessThanOrEqual(sizes.viewportWidth);
}

async function shoot(page: Page, target: Locator, name: string): Promise<void> {
  const report = await findTofu(page, japaneseSamples);
  expect(
    report.tofu,
    `${name} を撮る前に日本語が描けていない。豆腐: ${report.tofu.join('')} / 書体: ${report.fontFamily}`,
  ).toEqual([]);

  await expect(target).toBeVisible();
  await target.screenshot({ path: `${shots}/${name}.png` });
}

test.describe.configure({ mode: 'serial' });

test.describe('読みやすさ設定の写し', () => {
  let authFile = '';
  let publicId = '';

  test.beforeAll(async ({ browser }) => {
    test.setTimeout(180_000);

    authFile = await ensureAdminStorageState(browser, baseUrl);
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      publicId = (
        await prepareThemeSurvey(
          context.request,
          demoSiteId,
          { preset: 'Forest' },
          '読みやすさ設定の見本',
        )
      ).publicId;
    } finally {
      await context.close();
    }
  });

  async function openAnswer(page: Page): Promise<Locator> {
    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '読みやすさ設定の見本' })).toBeVisible();
    return page.locator('main');
  }

  test('回答画面：作成者のテーマと標準文字', async ({ page }) => {
    const answer = await openAnswer(page);
    await expectColors(page, forest);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)).toBe(
      '16px',
    );
    await shoot(page, answer, 'answer-06-readability-default');
  });

  test('回答画面：特大文字', async ({ page }) => {
    const answer = await openAnswer(page);
    await page.locator('#answer-readability-font-size').selectOption('extraLarge');
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)).toBe(
      '20px',
    );
    await expectNoHorizontalOverflow(page);
    await shoot(page, answer, 'answer-07-readability-large');
  });

  test('回答画面：高コントラスト', async ({ page }) => {
    const answer = await openAnswer(page);
    await page.locator('#answer-readability-color-mode').selectOption('highContrast');
    await expectColors(page, highContrast);
    expect(await cssVariable(page, '--accent')).not.toBe(forest.accent);
    await shoot(page, answer, 'answer-08-readability-contrast');
  });

  test('回答画面：ダーク', async ({ page }) => {
    const answer = await openAnswer(page);
    await page.locator('#answer-readability-color-mode').selectOption('dark');
    await expectColors(page, dark);
    expect(await cssVariable(page, '--bg')).not.toBe(forest.background);
    await shoot(page, answer, 'answer-09-readability-dark');
  });

  async function openAdmin(
    browser: import('@playwright/test').Browser,
  ): Promise<{ context: import('@playwright/test').BrowserContext; page: Page; target: Locator }> {
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });
    const page = await context.newPage();
    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();
    return { context, page, target: page.locator('.shell') };
  }

  test('管理画面：ダーク', async ({ browser }) => {
    const { context, page, target } = await openAdmin(browser);

    try {
      await page.locator('#admin-readability-color-mode').selectOption('dark');
      await expectColors(page, dark);
      await shoot(page, target, 'admin-17-readability-dark');
    } finally {
      await context.close();
    }
  });

  test('管理画面：特大文字', async ({ browser }) => {
    const { context, page, target } = await openAdmin(browser);

    try {
      await page.locator('#admin-readability-font-size').selectOption('extraLarge');
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)).toBe(
        '20px',
      );
      await expectNoHorizontalOverflow(page);
      await shoot(page, target, 'admin-18-readability-large');
    } finally {
      await context.close();
    }
  });
});
