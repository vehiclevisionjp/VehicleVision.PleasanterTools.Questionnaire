import { expect, test } from '@playwright/test';
import type { APIRequestContext } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';

/**
 * アンケート一覧のページ送りと絞り込みを実機で確かめる（Issue #79）。
 *
 * **単体試験だけでは足りない。** 上限を入れたことで起きうる失敗は、
 * 「1 ページ目しか出ない」「絞り込んだのに 3 ページ目のまま 0 件に見える」のように、
 * **画面まで通して初めて見える**もの。
 *
 * **まっさらな検証環境が前提**（アンケートが 1 件も無い状態）。
 * 溜まった行があると件数の数え方が変わる。
 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

/** 1 ページの件数。画面側の `pageSize` と同じ。 */
const pageSize = 50;

/** ページ送りを確かめるために作る件数。**1 ページに収まらない数にする。** */
const pagedCount = 55;

/** 絞り込みで拾う目印。**ページ送りの見本とは題名が重ならないようにする。** */
const markedTitles = ['絞り込みの目印 A', '絞り込みの目印 B'];

/** 公開して「公開中」で絞り込むためのアンケート。**これ 1 本だけ公開する。** */
const publishedTitle = '公開済みの目印';

/**
 * 検証環境にあるアンケートの総数。
 *
 * **数えて求める。** 他の spec が作った分まで数に入れないと、
 * まとめて走らせたときだけ 1 件ずれて落ちる（実際に踏んだ）。
 */
let totalCount = 0;

let authFile = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL }) => {
  authFile = await ensureAdminStorageState(browser, baseURL ?? '');
});

/** アンケートを 1 本作る。**中身は要らない**ので下書きのまま置く。 */
async function createSurvey(request: APIRequestContext, title: string): Promise<string> {
  const created = await request.post('/api/admin/surveys', {
    data: { title, pleasanterSiteId: demoSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId } = (await created.json()) as { surveyId: string };
  return surveyId;
}

/** 表の行数。**見出しの行は数えない。** */
function rowCount(page: import('@playwright/test').Page) {
  return page.locator('table tbody tr').count();
}

/** いま検証環境にあるアンケートの総数を数える。**上限があるので繰り返し読む。** */
async function countAll(request: APIRequestContext): Promise<number> {
  let offset = 0;
  let counted = 0;

  for (;;) {
    const response = await request.get(`/api/admin/surveys?offset=${offset}&limit=200`);
    if (!response.ok()) {
      throw new Error(`一覧を読めなかった: ${response.status()} ${await response.text()}`);
    }

    const page = (await response.json()) as { items: unknown[]; hasMore: boolean };
    counted += page.items.length;

    if (!page.hasMore) {
      return counted;
    }

    offset += 200;
  }
}

test.describe('アンケート一覧のページ送りと絞り込み', () => {
  test(`見本を ${pagedCount + markedTitles.length + 1} 件そろえる`, async ({ browser, baseURL }) => {
    const context = await browser.newContext({ baseURL, storageState: authFile });

    try {
      // **並べて作らない。** 並び順は UpdatedAt なので、
      // 同時に作ると 1 ページ目の顔ぶれが走らせるたびに変わる
      for (let i = 1; i <= pagedCount; i += 1) {
        await createSurvey(context.request, `ページ送りの見本 ${String(i).padStart(2, '0')}`);
      }

      for (const title of markedTitles) {
        await createSurvey(context.request, title);
      }

      // **1 本だけ公開する。** 状態で絞り込めることを見るため
      const surveyId = await createSurvey(context.request, publishedTitle);

      const definition = {
        surveyId,
        version: 1,
        title: { ja: publishedTitle },
        description: { ja: '状態で絞り込めることを確かめるためのものです。' },
        confirmationMessage: { ja: 'ありがとうございました。' },
        pages: [
          {
            pageId: 'page-1',
            title: { ja: '一問だけ' },
            questions: [
              {
                questionId: 'q-1',
                type: 'Text',
                title: { ja: 'お名前' },
                isRequired: false,
                choices: [],
                settings: {},
              },
            ],
          },
        ],
      };

      const saved = await context.request.put(`/api/admin/surveys/${surveyId}`, {
        data: { revision: 0, definition, mapping: { assignments: [] } },
      });
      expect(saved.ok(), await saved.text()).toBe(true);

      const published = await context.request.post(
        `/api/admin/surveys/${surveyId}/publish`,
        { data: {} },
      );
      expect(published.ok(), await published.text()).toBe(true);
    } finally {
      await context.close();
    }
  });

  test.describe('管理画面から見る', () => {
    test.use({ storageState: 'artifacts/survey-list-auth.json' });

    test.beforeAll(async ({ browser, baseURL }) => {
      // **控えの場所は beforeAll より先に決まらない**ので、いま使うものへ写す
      const context = await browser.newContext({ baseURL, storageState: authFile });
      await context.storageState({ path: 'artifacts/survey-list-auth.json' });

      // **数えるのはここ。** 他の spec が作った分も込みの、いまの総数
      totalCount = await countAll(context.request);
      expect(totalCount).toBeGreaterThan(pageSize);

      await context.close();
    });

    test('1 ページ目には上限までしか出ない', async ({ page }) => {
      await page.goto('/admin');
      await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();

      // **全部描かれてしまうと、この試験自体が意味を失う**ので件数で見る
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      await expect(page.getByText(`1〜${pageSize} 件目`)).toBeVisible();
      await expect(page.getByRole('button', { name: '前へ' })).toBeDisabled();
      await expect(page.getByRole('button', { name: '次へ' })).toBeEnabled();
    });

    test('次へ進むと残りが出る', async ({ page }) => {
      await page.goto('/admin');
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      await page.getByRole('button', { name: '次へ' }).click();

      // **2 ページ目に何件出るか**。総数が 2 ページに収まらないなら上限まで
      const remaining = Math.min(totalCount - pageSize, pageSize);
      await expect.poll(() => rowCount(page)).toBe(remaining);
      await expect(page.getByText(`${pageSize + 1}〜${pageSize + remaining} 件目`)).toBeVisible();

      // **最後のページでは次へ進めない。** 進めると 0 件の画面が出てしまう
      if (totalCount <= pageSize * 2) {
        await expect(page.getByRole('button', { name: '次へ' })).toBeDisabled();
      }

      await expect(page.getByRole('button', { name: '前へ' })).toBeEnabled();
    });

    test('題名で絞り込める', async ({ page }) => {
      await page.goto('/admin');
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      await page.getByLabel('題名で検索').fill('絞り込みの目印');
      await page.getByRole('button', { name: '絞り込む' }).click();

      await expect.poll(() => rowCount(page)).toBe(markedTitles.length);
      for (const title of markedTitles) {
        await expect(page.getByRole('cell', { name: title })).toBeVisible();
      }
    });

    test('絞り込むとページは先頭へ戻る', async ({ page }) => {
      await page.goto('/admin');
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      // **2 ページ目のまま絞り込むのが、いちばん危ない筋道。**
      // 条件に合う行があっても「1 件も無い」と見えてしまう
      await page.getByRole('button', { name: '次へ' }).click();
      await expect(page.getByRole('button', { name: '前へ' })).toBeEnabled();

      await page.getByLabel('題名で検索').fill('絞り込みの目印');
      await page.getByRole('button', { name: '絞り込む' }).click();

      await expect.poll(() => rowCount(page)).toBe(markedTitles.length);
      await expect(page.getByText(`1〜${markedTitles.length} 件目`)).toBeVisible();
      await expect(page.getByRole('button', { name: '前へ' })).toBeDisabled();
    });

    test('状態で絞り込める', async ({ page }) => {
      await page.goto('/admin');
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      await page.getByLabel('状態').selectOption({ label: '公開中' });
      await page.getByRole('button', { name: '絞り込む' }).click();

      // **件数では見ない。** 他の spec が公開したものが混ざり得る。
      // **見たいのは「公開中でないものが出ていない」こと**
      await expect(page.getByRole('cell', { name: publishedTitle })).toBeVisible();
      const statuses = page.locator('table tbody tr td:nth-child(2)');
      const labels = await statuses.allInnerTexts();
      expect(labels.length).toBeGreaterThan(0);
      for (const label of labels) {
        expect(label).toContain('公開中');
      }
    });

    test('条件に合わないときは、1 件も無いときと言い分ける', async ({ page }) => {
      await page.goto('/admin');
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      await page.getByLabel('題名で検索').fill('どこにも無い題名');
      await page.getByRole('button', { name: '絞り込む' }).click();

      // **「まだアンケートがありません」と出してはいけない。**
      // 消えたのかと探し始めることになる
      await expect(page.getByText('条件に合うアンケートがありません。')).toBeVisible();
      await expect(page.getByText('まだアンケートがありません。')).toHaveCount(0);
    });

    test('条件をクリアと元へ戻る', async ({ page }) => {
      await page.goto('/admin');
      await expect.poll(() => rowCount(page)).toBe(pageSize);

      await page.getByLabel('題名で検索').fill('絞り込みの目印');
      await page.getByRole('button', { name: '絞り込む' }).click();
      await expect.poll(() => rowCount(page)).toBe(markedTitles.length);

      await page.getByRole('button', { name: '条件をクリア' }).click();

      await expect.poll(() => rowCount(page)).toBe(pageSize);
      await expect(page.getByLabel('題名で検索')).toHaveValue('');
    });
  });
});
