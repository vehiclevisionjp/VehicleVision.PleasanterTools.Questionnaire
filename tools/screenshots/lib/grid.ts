import type { APIRequestContext } from '@playwright/test';

/**
 * グリッドとランキングを確かめるための見本アンケートを作って公開する（Issue #74）。
 *
 * **形はこう。**
 *
 * ```text
 * ページ1  q-grid「それぞれについて教えてください」（必須・行ごとに 1 つ）
 *            行: 価格 / 品質      列: 良い / ふつう / 悪い
 *          q-rank「大事な順に選んでください」
 *            項目: 速さ / 安さ / 品質
 * ```
 *
 * **必須のグリッドは行が全部埋まるまで送れない。**
 * 行の一部だけ答えて送れると、どこまで答えたのか誰にも分からなくなる。
 */
export async function prepareGridSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: 'グリッドの見本', pleasanterSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId, publicId } = (await created.json()) as {
    surveyId: string;
    publicId: string;
  };

  const definition = {
    surveyId,
    version: 1,
    title: { ja: 'グリッドの見本' },
    description: { ja: 'グリッドとランキングが動くことを確かめるためのものです。' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        title: { ja: 'ご評価' },
        questions: [
          {
            questionId: 'q-grid',
            type: 'Grid',
            title: { ja: 'それぞれについて教えてください' },
            isRequired: true,
            choices: [
              { value: 'good', label: { ja: '良い' } },
              { value: 'fair', label: { ja: 'ふつう' } },
              { value: 'bad', label: { ja: '悪い' } },
            ],
            settings: {
              rows: [
                { rowId: 'price', label: { ja: '価格' } },
                { rowId: 'quality', label: { ja: '品質' } },
              ],
            },
          },
          {
            questionId: 'q-rank',
            type: 'Ranking',
            title: { ja: '大事な順に選んでください' },
            isRequired: false,
            choices: [
              { value: 'speed', label: { ja: '速さ' } },
              { value: 'cost', label: { ja: '安さ' } },
              { value: 'quality', label: { ja: '品質' } },
            ],
            settings: {},
          },
        ],
      },
    ],
  };

  // **行ごとに 1 列。** ランキングは順位を数値の列へ
  const mapping = {
    assignments: [
      {
        targetColumn: 'ClassA',
        sources: [{ questionId: 'q-grid', port: 'Value', rowId: 'price' }],
        converter: null,
      },
      {
        targetColumn: 'ClassB',
        sources: [{ questionId: 'q-grid', port: 'Value', rowId: 'quality' }],
        converter: null,
      },
    ],
  };

  const saved = await request.put(`/api/admin/surveys/${surveyId}`, {
    data: { revision: 0, definition, mapping },
  });
  if (!saved.ok()) {
    throw new Error(`下書きを保存できなかった: ${saved.status()} ${await saved.text()}`);
  }

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}
