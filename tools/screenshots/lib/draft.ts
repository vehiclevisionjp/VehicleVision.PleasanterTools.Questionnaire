import type { APIRequestContext } from '@playwright/test';

/**
 * 下書きを確かめるための見本アンケートを作って公開する（Issue #59）。
 *
 * **1 ページに記述式が 2 つだけ。** 見たいのは「書いた内容が端末に残るか」なので、
 * 設問の形式は最も単純なものにする。
 *
 * @param allowDraft 下書きを許すか。**既定は無効**（端末は共有され得るため）。
 */
export async function prepareDraftSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
  allowDraft: boolean,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: '下書きの見本', pleasanterSiteId },
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
    title: { ja: '下書きの見本' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        questions: [
          {
            questionId: 'q-free',
            type: 'Paragraph',
            title: { ja: 'ご意見' },
            isRequired: false,
            choices: [],
            settings: { maxLength: 500 },
          },
          {
            questionId: 'q-name',
            type: 'Text',
            title: { ja: 'お名前' },
            isRequired: false,
            choices: [],
            settings: { maxLength: 100 },
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

  // **公開設定は定義と別。** 公開し直さずに切り替えられる
  const settings = await request.put(`/api/admin/surveys/${surveyId}/settings`, {
    data: { responseLimit: null, allowDraft },
  });
  if (!settings.ok()) {
    throw new Error(`公開設定を保存できなかった: ${settings.status()} ${await settings.text()}`);
  }

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}
