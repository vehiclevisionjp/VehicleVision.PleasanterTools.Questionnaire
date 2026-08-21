import type { APIRequestContext } from '@playwright/test';

/**
 * **必須の断り方とページの行き来**を確かめる見本を作って公開する（Issue #120）。
 *
 * **形はこう。**
 *
 * ```text
 * ページ1  q-name「お名前」（必須）
 *          q-memo「ひとこと」（任意）
 * ページ2  q-mail「連絡先」（必須・メール）
 * ```
 *
 * **必須を空のまま「次へ」を押しても進めない**こと、
 * **「戻る」で入れたものが残っている**ことを見る。
 * 進み具合の表示も出したいので `showProgress` を立てる。
 */
export async function prepareRequiredSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: '必須とページ送りの見本', pleasanterSiteId },
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
    title: { ja: '必須とページ送りの見本' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    // **進み具合を出す。** ページを跨いだときに数字が動くことを見る
    showProgress: true,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        title: { ja: 'お客様について' },
        questions: [
          {
            questionId: 'q-name',
            type: 'Text',
            title: { ja: 'お名前' },
            isRequired: true,
            choices: [],
            settings: { maxLength: 100 },
          },
          {
            questionId: 'q-memo',
            type: 'Paragraph',
            title: { ja: 'ひとこと' },
            isRequired: false,
            choices: [],
            settings: { maxLength: 500 },
          },
        ],
      },
      {
        pageId: 'page-2',
        title: { ja: 'ご連絡先' },
        questions: [
          {
            questionId: 'q-mail',
            type: 'Text',
            title: { ja: '連絡先メールアドレス' },
            isRequired: true,
            choices: [],
            // **書式の誤りも同じ断り方で出ることを見る**
            settings: { format: 'Email', maxLength: 200 },
          },
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

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}
