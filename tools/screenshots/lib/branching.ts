import type { APIRequestContext } from '@playwright/test';

/**
 * 分岐を確かめるための見本アンケートを作って公開する。
 *
 * **形はこう。**
 *
 * ```text
 * ページ1  q-use「利用していますか」
 *            「はい」→ 次のページ（ページ2）
 *            「いいえ」→ ページ3 へ飛ぶ
 *          q-service「サービス名」（q-use が「はい」のときだけ出す）
 * ページ2  q-reason「良かった点」（必須）
 * ページ3  q-free「ご意見」
 * ```
 *
 * **「いいえ」を選ぶと、必須の q-reason を答えずに送信まで行ける。**
 * これが通らないと、飛ばしたページの必須で止まっていることになる。
 */
export async function prepareBranchingSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
  displayMode: 'Paged' | 'OneQuestionPerPage' = 'Paged',
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: '分岐の見本', pleasanterSiteId },
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
    title: { ja: '分岐の見本' },
    description: { ja: '分岐が動くことを確かめるためのものです。' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode,
    showProgress: true,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        title: { ja: 'ご利用について' },
        questions: [
          {
            questionId: 'q-use',
            type: 'Radio',
            title: { ja: '利用していますか' },
            isRequired: true,
            choices: [
              { value: 'yes', label: { ja: 'はい' }, isOther: false },
              // **「いいえ」を選んだらページ2 を飛ばす**
              {
                value: 'no',
                label: { ja: 'いいえ' },
                isOther: false,
                next: { kind: 'Page', pageId: 'page-3' },
              },
            ],
            settings: {},
          },
          {
            questionId: 'q-service',
            type: 'Text',
            title: { ja: 'サービス名' },
            isRequired: false,
            choices: [],
            settings: {},
            // **同じページの中で出し分ける**
            visibleWhen: {
              match: 'All',
              rules: [{ questionId: 'q-use', operator: 'Equals', value: 'yes' }],
            },
          },
        ],
      },
      {
        pageId: 'page-2',
        title: { ja: '良かった点' },
        questions: [
          {
            questionId: 'q-reason',
            type: 'Paragraph',
            title: { ja: '良かった点をお書きください' },
            // **必須。** 飛ばした側では求められないこと
            isRequired: true,
            choices: [],
            settings: { maxLength: 500 },
          },
        ],
      },
      {
        pageId: 'page-3',
        title: { ja: 'ご意見' },
        questions: [
          {
            questionId: 'q-free',
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
        sources: [{ questionId: 'q-use', port: 'Value' }],
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
