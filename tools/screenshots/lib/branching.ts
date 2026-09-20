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

  // **下書きから直接は公開できない**（Issue #223）。版を固めるのはテスト公開の側
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

/**
 * 分岐の全体図を撮るための下書きを作る。
 *
 * **公開しない。** 不備のある定義も図へ重ねて確認するため、
 * 公開時に拒否される前の下書きを編集画面で開く。
 */
export async function prepareFlowchartDraft(
  request: APIRequestContext,
  pleasanterSiteId: number,
  withProblems = false,
): Promise<{ surveyId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: {
      title: withProblems ? '不備のある分岐図（見本）' : '分岐図（見本）',
      pleasanterSiteId,
    },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId } = (await created.json()) as { surveyId: string };
  const pages = withProblems
    ? [
        {
          pageId: 'page-start',
          title: { ja: '開始ページ' },
          next: { kind: 'Page', pageId: 'missing-page' },
          questions: [],
        },
        {
          pageId: 'page-isolated',
          title: { ja: '孤立したページ' },
          questions: [
            {
              questionId: 'q-isolated',
              type: 'Text',
              title: { ja: '到達しない設問' },
              isRequired: false,
              choices: [],
              settings: {},
            },
          ],
        },
      ]
    : [
        {
          pageId: 'page-start',
          title: { ja: '開始ページ' },
          // **選択肢に該当しない場合は、ページ末尾のジャンプを通る。**
          next: { kind: 'Page', pageId: 'page-finish' },
          questions: [
            {
              questionId: 'q-route',
              type: 'Radio',
              title: { ja: '詳しくお聞きしてもよいですか' },
              isRequired: true,
              choices: [
                {
                  value: 'details',
                  label: { ja: '詳しく答える' },
                  isOther: false,
                  next: { kind: 'Page', pageId: 'page-details' },
                },
                { value: 'skip', label: { ja: '次へ進む' }, isOther: false },
              ],
              settings: {},
            },
          ],
        },
        {
          pageId: 'page-details',
          title: { ja: '詳しい内容' },
          questions: [
            {
              questionId: 'q-details',
              type: 'Paragraph',
              title: { ja: '詳しい内容をお書きください' },
              isRequired: false,
              choices: [],
              settings: {},
            },
          ],
        },
        {
          pageId: 'page-finish',
          title: { ja: '最後のご意見' },
          next: { kind: 'Submit' },
          questions: [
            {
              questionId: 'q-finish',
              type: 'Paragraph',
              title: { ja: 'ご意見' },
              isRequired: false,
              choices: [],
              settings: {},
            },
          ],
        },
      ];

  const saved = await request.put(`/api/admin/surveys/${surveyId}`, {
    data: {
      revision: 0,
      definition: {
        surveyId,
        version: 1,
        title: { ja: withProblems ? '不備のある分岐図（見本）' : '分岐図（見本）' },
        displayMode: 'Paged',
        showProgress: true,
        allowEditingAfterSubmit: false,
        pages,
      },
      mapping: { assignments: [] },
    },
  });
  if (!saved.ok()) {
    throw new Error(`下書きを保存できなかった: ${saved.status()} ${await saved.text()}`);
  }

  return { surveyId };
}
