import type { APIRequestContext } from '@playwright/test';

/**
 * 写しを撮るための下ごしらえ。
 *
 * **まっさらな検証環境が前提。** 管理者がまだ 1 人も居ない状態から始める。
 * 初期設定と 2 要素の登録は取説にも載せたい画面なので、
 * **実際の初回の流れをそのまま辿る。**
 */
export const demoAdmin = {
  loginId: 'kanri-tantou',
  // **検証用と分かる値にする。** 実在しそうな資格情報を写しに入れない
  password: 'demo-password-for-manual',
};

/** 取説に載せる見本のアンケートを作って公開する。 */
export async function prepareSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: '社内アンケート（見本）', pleasanterSiteId },
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
    title: { ja: '社内アンケート（見本）' },
    description: { ja: '取扱説明書に載せるための見本です。回答は保存されません。' },
    confirmationMessage: { ja: 'ご回答ありがとうございました。' },
    showProgress: true,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        title: { ja: '働きやすさについて' },
        questions: [
          {
            questionId: 'q-satisfaction',
            type: 'Radio',
            title: { ja: '現在の職場環境に満足していますか' },
            isRequired: true,
            choices: [
              { value: 'very', label: { ja: 'とても満足' } },
              { value: 'ok', label: { ja: 'おおむね満足' } },
              { value: 'poor', label: { ja: 'あまり満足していない' } },
            ],
            settings: {},
          },
          {
            questionId: 'q-scale',
            type: 'Scale',
            title: { ja: '同僚へ勧めたいと思いますか' },
            isRequired: false,
            choices: [],
            settings: {
              scaleMinimum: 1,
              scaleMaximum: 5,
              scaleMinimumLabel: { ja: '思わない' },
              scaleMaximumLabel: { ja: '強く思う' },
            },
          },
          {
            questionId: 'q-comment',
            type: 'Paragraph',
            title: { ja: 'ご意見があればお書きください' },
            isRequired: false,
            choices: [],
            settings: { maxLength: 500 },
          },
        ],
      },
    ],
  };

  const mapping = {
    assignments: [
      {
        targetColumn: 'ClassA',
        sources: [{ questionId: 'q-satisfaction', port: 'Value' }],
        converter: null,
      },
      {
        targetColumn: 'NumA',
        sources: [{ questionId: 'q-scale', port: 'Value' }],
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
