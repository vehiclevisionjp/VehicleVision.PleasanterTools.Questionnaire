import { expect, test } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';

/**
 * マッピングの編集画面が「ソース → 変換 → ターゲット」の表になっていることを
 * 実機で確かめる（Issue #86）。
 *
 * **確かめたいのは 3 つ。**
 *
 * 1. **1 つの割り当てが 1 行に収まる**こと。縦積みのままだと、
 *    どの入力がどの列へ行くのかを目で追えない
 * 2. **入力が複数のときは 1 つの升の中で番号が付く**こと。
 *    行を分けて結合すると、変換とターゲットがどの入力群に掛かるのか読めなくなる
 * 3. **不備はその割り当ての直下に出る**こと。表の外へ集めると、
 *    どの行の話なのかが失われる
 *
 * **まっさらな検証環境が前提**（管理者がまだ 1 人も居ない）。
 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

let authFile = '';
let surveyId = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL }) => {
  authFile = await ensureAdminStorageState(browser, baseURL ?? '');
});

test.describe('マッピングの編集画面', () => {
  test('見本のアンケートを用意する', async ({ browser, baseURL }) => {
    const context = await browser.newContext({ baseURL, storageState: authFile });

    try {
      const created = await context.request.post('/api/admin/surveys', {
        data: { title: 'マッピングの見本', pleasanterSiteId: demoSiteId },
      });
      expect(created.ok(), await created.text()).toBe(true);
      ({ surveyId } = (await created.json()) as { surveyId: string });

      const question = (questionId: string, title: string) => ({
        questionId,
        type: 'Text',
        title: { ja: title },
        isRequired: false,
        choices: [],
        settings: {},
      });

      const definition = {
        surveyId,
        version: 1,
        title: { ja: 'マッピングの見本' },
        description: { ja: '割り当ての表を確かめるためのものです。' },
        confirmationMessage: { ja: 'ありがとうございました。' },
        pages: [
          {
            pageId: 'page-1',
            title: { ja: 'お名前' },
            questions: [
              question('q-sei', '姓'),
              question('q-mei', '名'),
              question('q-memo', '備考'),
            ],
          },
        ],
      };

      /**
       * **3 通りをわざと混ぜる。**
       *
       * - `1:0:1` … 入力 1 つ・変換なし（いちばん普通の形）
       * - `2:1:1` … 入力 2 つ・変換あり（番号が付く形）
       * - `2:0:1` … 入力 2 つ・変換なし（**不備**。直下に出るはず）
       */
      const mapping = {
        assignments: [
          {
            targetColumn: 'ClassA',
            sources: [{ questionId: 'q-memo', port: 'Value' }],
            converter: null,
          },
          {
            targetColumn: 'ClassB',
            sources: [
              { questionId: 'q-sei', port: 'Value' },
              { questionId: 'q-mei', port: 'Value' },
            ],
            converter: { operation: 'join', config: { separator: '　' } },
          },
          {
            targetColumn: 'ClassC',
            sources: [
              { questionId: 'q-sei', port: 'Value' },
              { questionId: 'q-mei', port: 'Value' },
            ],
            converter: null,
          },
        ],
      };

      const saved = await context.request.put(`/api/admin/surveys/${surveyId}`, {
        data: { revision: 0, definition, mapping },
      });
      expect(saved.ok(), await saved.text()).toBe(true);
    } finally {
      await context.close();
    }
  });

  test.describe('画面から見る', () => {
    test.use({ storageState: 'artifacts/mapping-auth.json' });

    test.beforeAll(async ({ browser, baseURL }) => {
      const context = await browser.newContext({ baseURL, storageState: authFile });
      await context.storageState({ path: 'artifacts/mapping-auth.json' });
      await context.close();
    });

    test('ソース・変換・ターゲットの 3 列になっている', async ({ page }) => {
      test.skip(surveyId === '', '先の試験でアンケートを作れていない');

      await page.goto(`/admin/surveys/${surveyId}`);
      const mapping = page.getByRole('table', {
        name: '割り当ての一覧。1 行が Pleasanter の 1 列への割り当てです。',
      });
      await expect(mapping).toBeVisible();

      // **順番も見る。** 並びが入れ替わると「左から右へ流れる」読み方が壊れる
      const headers = mapping.locator('thead th');
      await expect(headers.nth(0)).toHaveText('ソース');
      await expect(headers.nth(1)).toHaveText('変換');
      await expect(headers.nth(2)).toHaveText('ターゲット（Pleasanter の列）');
    });

    test('1 つの割り当てが 1 行に収まる', async ({ page }) => {
      test.skip(surveyId === '', '先の試験でアンケートを作れていない');

      await page.goto(`/admin/surveys/${surveyId}`);

      // **不備の行は数に入れない。** 割り当てそのものは 3 つ
      const rows = page.locator('table tbody tr:not(.notes)');
      await expect.poll(() => rows.count()).toBe(3);

      await expect(page.getByRole('textbox', { name: '書き込み先の列' }).nth(0))
        .toHaveValue('ClassA');
      await expect(page.getByRole('textbox', { name: '書き込み先の列' }).nth(1))
        .toHaveValue('ClassB');
    });

    test('入力が複数のときは 1 つの升に積んで番号が付く', async ({ page }) => {
      test.skip(surveyId === '', '先の試験でアンケートを作れていない');

      await page.goto(`/admin/surveys/${surveyId}`);
      const rows = page.locator('table tbody tr:not(.notes)');
      await expect.poll(() => rows.count()).toBe(3);

      // 1 行目は入力 1 つ。**番号は出さない**（1 つしか無いのに順番を語らない）
      const single = rows.nth(0).locator('td.source ol.sources');
      await expect(single.locator('li')).toHaveCount(1);
      await expect(single).not.toHaveClass(/numbered/);

      // 2 行目は入力 2 つ。**同じ升の中に積む**
      const multiple = rows.nth(1).locator('td.source ol.sources');
      await expect(multiple.locator('li')).toHaveCount(2);
      await expect(multiple).toHaveClass(/numbered/);

      // **番号は CSS の counter で描く。** 文言に混ぜると翻訳が汚れる
      const marker = await multiple.locator('li').first().evaluate((li) =>
        globalThis.getComputedStyle(li, '::before').content,
      );
      expect(marker).not.toBe('none');

      // **変換とターゲットは 1 つずつ。** 入力が 2 つでも升は増えない
      await expect(rows.nth(1).locator('td.converter select')).toHaveCount(1);
      await expect(rows.nth(1).locator('td.target input')).toHaveCount(1);
    });

    test('不備はその割り当ての直下に出る', async ({ page }) => {
      test.skip(surveyId === '', '先の試験でアンケートを作れていない');

      await page.goto(`/admin/surveys/${surveyId}`);
      const rows = page.locator('table tbody tr');

      // 3 つの割り当て ＋ 3 つ目の不備で 4 行
      await expect.poll(() => rows.count()).toBe(4);

      const notes = page.locator('table tbody tr.notes');
      await expect(notes).toHaveCount(1);
      await expect(notes).toContainText('変換が無いときは入力をちょうど 1 つにしてください。');

      // **直前の行が、その不備の持ち主**であること
      const owner = notes.locator('xpath=preceding-sibling::tr[1]');
      await expect(owner.locator('td.target input')).toHaveValue('ClassC');
    });

    test('列を追加すると行が増える', async ({ page }) => {
      test.skip(surveyId === '', '先の試験でアンケートを作れていない');

      await page.goto(`/admin/surveys/${surveyId}`);
      const rows = page.locator('table tbody tr:not(.notes)');
      await expect.poll(() => rows.count()).toBe(3);

      // **`exact` を付ける。** 「添付の列を追加」にも当たってしまう
      await page.getByRole('button', { name: '列を追加', exact: true }).click();

      await expect.poll(() => rows.count()).toBe(4);
      await expect(rows.nth(3).locator('td.source select').first()).toBeVisible();
    });
  });
});
