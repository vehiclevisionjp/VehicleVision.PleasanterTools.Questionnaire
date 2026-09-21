import { expect, test, type Locator, type Page } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareFlowchartDraft } from '../lib/branching';
import { prepareKeyboardSurvey } from '../lib/keyboard';

const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');
const authFile = '.auth.json';

let publicId = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser }) => {
  test.setTimeout(120_000);
  const storageState = await ensureAdminStorageState(browser, baseUrl);
  const context = await browser.newContext({ baseURL: baseUrl, storageState });
  try {
    publicId = (await prepareKeyboardSurvey(context.request, demoSiteId)).publicId;
  } finally {
    await context.close();
  }
});

test.describe('キーボードだけで操作する', () => {
  test.beforeEach(async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを公開できていない');
    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: 'キーボード操作の見本' })).toBeVisible();
  });

  test('Tab の順番どおりに全設問へ到達し、焦点の輪郭が見える', async ({ page }) => {
    const order = answerTabOrder(page);

    for (const [index, target] of order.entries()) {
      await tabToNext(page, target, order[index - 1]);
      await expectVisibleFocus(target);
    }
  });

  test('ラジオとチェックをキーで選び、送信まで完了する', async ({ page }) => {
    await tabTo(page, page.getByLabel('お名前'));
    await page.keyboard.type('検証 太郎');

    await page.keyboard.press('Tab');
    await expect(page.getByLabel('ご意見')).toBeFocused();
    await page.keyboard.type('キーボードで回答します。');

    await page.keyboard.press('Tab');
    const radio = page.getByRole('radio', { name: '第一候補' }).first();
    await expect(radio).toBeFocused();
    await page.keyboard.press('ArrowRight');
    await expect(page.getByRole('radio', { name: '第二候補' }).first()).toBeChecked();

    await page.keyboard.press('Tab');
    const checkbox = page.getByRole('checkbox', { name: '第一候補' }).first();
    await expect(checkbox).toBeFocused();
    await page.keyboard.press('Space');
    await expect(checkbox).toBeChecked();

    await tabTo(page, page.getByRole('checkbox', { name: '内容を確認しました' }));
    await page.keyboard.press('Space');
    await expect(page.getByRole('checkbox', { name: '内容を確認しました' })).toBeChecked();

    await tabTo(page, page.getByRole('button', { name: '送信する' }));
    await page.waitForTimeout(4000);
    await page.keyboard.press('Enter');

    await expect(page.getByRole('heading', { name: '回答を受け付けました' })).toBeVisible();
    await expect(page.getByText('キーボードだけで送信できました。')).toBeVisible();
  });

  test('検証エラーを読み上げへ伝える', async ({ page }) => {
    const submit = page.getByRole('button', { name: '送信する' });
    await tabTo(page, submit);
    await page.keyboard.press('Enter');

    const firstError = page.getByRole('alert').first();
    await expect(firstError).toBeVisible();
    await expect(firstError).toHaveText('回答してください');
    await expect(page.getByLabel('お名前')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel('お名前')).toHaveAttribute(
      'aria-describedby',
      'error-q-name',
    );
  });
});

test.describe('管理画面もキーボードで辿る', () => {
  test('ログイン欄を見た目の順番で辿れる', async ({ browser }) => {
    const context = await browser.newContext({ baseURL: baseUrl });
    try {
      const page = await context.newPage();
      await page.goto('/admin');
      await expect(page.getByRole('heading', { name: '管理画面にログイン' })).toBeVisible();

      for (const target of [
        page.getByLabel('ログイン ID'),
        page.getByLabel('パスワード', { exact: true }),
        page.getByRole('button', { name: '次へ' }),
      ]) {
        await page.keyboard.press('Tab');
        await expect(target).toBeFocused();
        await expectVisibleFocus(target);
      }
    } finally {
      await context.close();
    }
  });

  test('一覧の絞り込み欄を見た目の順番で辿れる', async ({ browser }) => {
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });
    try {
      const page = await context.newPage();
      await page.goto('/admin');
      await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();

      await tabTo(page, page.getByLabel('題名'));
      await expectVisibleFocus(page.getByLabel('題名'));
      await page.keyboard.press('Tab');
      await expect(page.getByLabel('状態')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.getByRole('checkbox', { name: 'アーカイブ済みも表示' })).toBeFocused();
    } finally {
      await context.close();
    }
  });
});

test.describe('重ねて開く画面', () => {
  test.use({ storageState: authFile });

  test('プレビューと分岐の全体図を Esc で閉じられる', async ({ page }) => {
    const survey = await prepareFlowchartDraft(page.request, demoSiteId);
    await page.goto(`/admin/surveys/${survey.surveyId}`);

    await page.getByRole('button', { name: 'プレビュー' }).click();
    await expect(page.getByRole('dialog', { name: 'プレビュー' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: 'プレビュー' })).toBeHidden();

    await page.getByRole('button', { name: '分岐の全体図' }).click();
    await expect(page.getByRole('dialog', { name: '分岐の全体図' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: '分岐の全体図' })).toBeHidden();
  });
});

function answerTabOrder(page: Page): Locator[] {
  return [
    page.getByLabel('言語'),
    page.getByLabel('文字'),
    page.getByLabel('配色'),
    page.getByLabel('お名前'),
    page.getByLabel('ご意見'),
    page.getByRole('radio', { name: '第一候補' }).first(),
    page.getByRole('checkbox', { name: '第一候補' }).first(),
    page.getByRole('checkbox', { name: '第二候補' }).first(),
    page.getByLabel('都道府県'),
    page.getByRole('radio', { name: '1' }),
    page.getByRole('button', { name: '1 / 3' }),
    page.getByRole('button', { name: '2 / 3' }),
    page.getByRole('button', { name: '3 / 3' }),
    page.getByLabel('希望日'),
    page.getByLabel('添付ファイル'),
    page.getByRole('radio', { name: '価格: 第一候補' }),
    page.getByRole('radio', { name: '品質: 第一候補' }),
    page.getByRole('checkbox', { name: 'デザイン: 第一候補' }),
    page.getByRole('checkbox', { name: 'デザイン: 第二候補' }),
    page.getByRole('checkbox', { name: 'サポート: 第一候補' }),
    page.getByRole('checkbox', { name: 'サポート: 第二候補' }),
    page.getByRole('button', { name: /未選択.*第一候補/ }),
    page.getByRole('button', { name: /未選択.*第二候補/ }),
    page.getByLabel('希望時刻'),
    page.getByRole('checkbox', { name: '内容を確認しました' }),
    page.getByRole('button', { name: '送信する' }),
  ];
}

async function tabTo(page: Page, target: Locator): Promise<void> {
  for (let count = 0; count < 100; count += 1) {
    if (await target.evaluate((element) => element === document.activeElement)) {
      return;
    }
    await page.keyboard.press('Tab');
  }
  throw new Error('Tab を 100 回押しても目的の入力へ到達できなかった');
}

async function tabToNext(page: Page, target: Locator, previous?: Locator): Promise<void> {
  for (let count = 0; count < 10; count += 1) {
    await page.keyboard.press('Tab');
    if (await target.evaluate((element) => element === document.activeElement)) {
      return;
    }

    const remainsInPrevious =
      previous !== undefined
      && await previous.evaluate((element) => element === document.activeElement);
    if (!remainsInPrevious) {
      const active = await page.locator(':focus').evaluate((element) => element.outerHTML);
      throw new Error(`Tab の順番にない入力へ移った: ${active}`);
    }
  }
  throw new Error('同じ入力の内部で Tab を 10 回押しても次の入力へ移らなかった');
}

async function expectVisibleFocus(target: Locator): Promise<void> {
  const focusStyle = await target.evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      outlineStyle: style.outlineStyle,
      outlineWidth: style.outlineWidth,
      boxShadow: style.boxShadow,
    };
  });
  expect(
    (focusStyle.outlineStyle !== 'none' && focusStyle.outlineWidth !== '0px')
      || focusStyle.boxShadow !== 'none',
    `焦点の輪郭が見えない: ${JSON.stringify(focusStyle)}`,
  ).toBe(true);
}
