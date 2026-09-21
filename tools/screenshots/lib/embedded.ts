import type { APIRequestContext } from '@playwright/test';

/** 埋め込みの通しを確かめるアンケートを作って公開する。 */
export async function prepareEmbeddedSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
  allowEmbedding: boolean,
): Promise<{ surveyId: string; publicId: string; assetId: string }> {
  const title = allowEmbedding ? '埋め込み回答の見本' : '埋め込みを許さない見本';
  const created = await request.post('/api/admin/surveys', {
    data: { title, pleasanterSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId, publicId } = (await created.json()) as {
    surveyId: string;
    publicId: string;
  };

  const uploaded = await request.post(`/api/admin/surveys/${surveyId}/assets`, {
    multipart: {
      asset: {
        name: 'embedded-e2e.pdf',
        mimeType: 'application/pdf',
        buffer: Buffer.from('%PDF-1.4\n埋め込み E2E の配布資料\n%%EOF\n'),
      },
    },
  });
  if (!uploaded.ok()) {
    throw new Error(`配布資産を登録できなかった: ${uploaded.status()} ${await uploaded.text()}`);
  }
  const { assetId } = (await uploaded.json()) as { assetId: string };

  const longNotes = Array.from({ length: 12 }, (_, index) => ({
    questionId: `q-note-${index + 1}`,
    type: 'Note',
    title: { ja: `高さを増やす説明 ${index + 1}` },
    description: {
      ja: 'ページ送りに伴う iframe の高さ変更を確かめるための説明文です。',
    },
    isRequired: false,
    choices: [],
    settings: {},
  }));
  const definition = {
    surveyId,
    version: 1,
    title: { ja: title },
    confirmationMessage: { ja: `[検証用資料](asset:${assetId})` },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: true,
    assetDelivery: { expiration: 'DaysAfterResponse', days: 1 },
    pages: [
      {
        pageId: 'page-1',
        title: { ja: '短い入力ページ' },
        questions: [
          {
            questionId: 'q-name',
            type: 'Text',
            title: { ja: 'お名前' },
            isRequired: true,
            choices: [],
            settings: { maxLength: 100 },
          },
        ],
      },
      {
        pageId: 'page-2',
        title: { ja: '長い説明ページ' },
        questions: longNotes,
      },
      {
        pageId: 'page-3',
        title: { ja: '短い確認ページ' },
        questions: [
          {
            questionId: 'q-final-note',
            type: 'Note',
            title: { ja: '回答を送信してください' },
            isRequired: false,
            choices: [],
            settings: {},
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

  const settings = await request.put(`/api/admin/surveys/${surveyId}/settings`, {
    data: {
      responseLimit: null,
      requireProofOfWork: false,
      allowEmbedding,
    },
  });
  if (!settings.ok()) {
    throw new Error(`公開設定を保存できなかった: ${settings.status()} ${await settings.text()}`);
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

  return { surveyId, publicId, assetId };
}
