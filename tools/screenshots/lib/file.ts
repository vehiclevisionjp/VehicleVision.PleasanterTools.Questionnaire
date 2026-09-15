import type { APIRequestContext } from '@playwright/test';

/**
 * **ファイル添付**を確かめる見本を作って公開する（Issue #120）。
 *
 * **形はこう。**
 *
 * ```text
 * ページ1  q-photo「写真」（必須・2 件まで・1 件 1024 バイトまで）
 * ```
 *
 * **上限を小さく取る。** 大きな見本を作らずに「大きすぎる」を出せる。
 * 上限そのものが正しいかはサーバ側の試験の役目で、
 * **ここで見るのは、上限に触れたときの断り方と選んだものの見え方。**
 */
export async function prepareFileSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: 'ファイル添付の見本', pleasanterSiteId },
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
    title: { ja: 'ファイル添付の見本' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        questions: [
          {
            questionId: 'q-photo',
            type: 'File',
            title: { ja: '写真' },
            isRequired: true,
            choices: [],
            settings: { maxFileCount: 2, maxFileSizeBytes: 1024 },
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
