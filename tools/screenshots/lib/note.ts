import type { APIRequestContext } from '@playwright/test';

/**
 * 説明文ブロックの書式を確かめる見本を作って公開する（Issue #108）。
 *
 * **本文は記法のまま送る。** 記法を読んで構造にするのはサーバの仕事で、
 * 画面はその構造だけを描く。**ここで HTML を送らないのが要**で、
 * 送っても素通りしないことを合わせて確かめる。
 */
export async function prepareNoteSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: '説明文ブロックの見本', pleasanterSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId, publicId } = (await created.json()) as {
    surveyId: string;
    publicId: string;
  };

  // **受け付ける記法だけを並べる**（見出し・箇条書き・太字・斜体・リンク）。
  // 併せて、受け付けないもの（HTML、`http:` のリンク）も混ぜる
  const markup = [
    '## ご案内',
    '',
    'これは **太字** と *斜体* を含む段落です。',
    '',
    '- 1 つめの項目',
    '- 2 つめの項目',
    '',
    '1. 手順の 1',
    '2. 手順の 2',
    '',
    '詳しくは [会社の案内](https://www.example.com/guide) をご覧ください。',
    '',
    '<script>alert(1)</script> と <b>太字のつもりの HTML</b> は素通ししません。',
    '',
    '[危ない行き先](http://www.example.com/plain) は link になりません。',
  ].join('\n');

  const definition = {
    surveyId,
    version: 1,
    title: { ja: '説明文ブロックの見本' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        questions: [
          {
            questionId: 'q-note',
            type: 'Note',
            title: { ja: 'はじめにお読みください' },
            description: { ja: markup },
            isRequired: false,
            choices: [],
            settings: {},
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

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}
