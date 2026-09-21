import type { APIRequestContext } from '@playwright/test';

/**
 * キーボード操作を確かめるため、回答部品を一通り載せたアンケートを作って公開する。
 *
 * 必須にするのは、通しでキー操作する文字、ラジオ、チェック、確認だけ。
 * それ以外は Tab で順番どおりに到達できることを確かめる。
 */
export async function prepareKeyboardSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: 'キーボード操作の見本', pleasanterSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId, publicId } = (await created.json()) as {
    surveyId: string;
    publicId: string;
  };

  const choices = [
    { value: 'first', label: { ja: '第一候補' } },
    { value: 'second', label: { ja: '第二候補' } },
  ];
  const definition = {
    surveyId,
    version: 1,
    title: { ja: 'キーボード操作の見本' },
    confirmationMessage: { ja: 'キーボードだけで送信できました。' },
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        title: { ja: '入力欄の並び' },
        questions: [
          question('q-name', 'Text', 'お名前', true),
          question('q-comment', 'Paragraph', 'ご意見'),
          { ...question('q-radio', 'Radio', '連絡方法', true), choices },
          { ...question('q-checkbox', 'Checkbox', '関心のある項目', true), choices },
          { ...question('q-dropdown', 'Dropdown', '都道府県'), choices },
          {
            ...question('q-scale', 'Scale', '満足度'),
            settings: { scaleMinimum: 1, scaleMaximum: 3 },
          },
          {
            ...question('q-rating', 'Rating', 'おすすめ度'),
            settings: { scaleMinimum: 1, scaleMaximum: 3 },
          },
          question('q-date', 'Date', '希望日'),
          question('q-file', 'File', '添付ファイル'),
          {
            ...question('q-grid', 'Grid', '項目別の評価'),
            choices,
            settings: {
              rows: [
                { rowId: 'price', label: { ja: '価格' } },
                { rowId: 'quality', label: { ja: '品質' } },
              ],
            },
          },
          {
            ...question('q-checkbox-grid', 'CheckboxGrid', '項目別の関心'),
            choices,
            settings: {
              rows: [
                { rowId: 'design', label: { ja: 'デザイン' } },
                { rowId: 'support', label: { ja: 'サポート' } },
              ],
            },
          },
          { ...question('q-ranking', 'Ranking', '優先順位'), choices },
          question('q-time', 'Time', '希望時刻'),
          question('q-confirm', 'Confirm', '内容を確認しました', true),
        ],
      },
    ],
  };

  const saved = await request.put(`/api/admin/surveys/${surveyId}`, {
    data: { revision: 0, definition, mapping: { assignments: [] } },
  });
  if (!saved.ok()) {
    throw new Error(`下書きを保存できなかった: ${saved.status()} ${await saved.text()}`);
  }

  const testPublished = await request.post(
    `/api/admin/surveys/${surveyId}/test-publish`,
    { data: {} },
  );
  if (!testPublished.ok()) {
    throw new Error(
      `テスト公開できなかった: ${testPublished.status()} ${await testPublished.text()}`,
    );
  }

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}

function question(questionId: string, type: string, title: string, isRequired = false) {
  return {
    questionId,
    type,
    title: { ja: title },
    isRequired,
    choices: [],
    settings: {},
  };
}
