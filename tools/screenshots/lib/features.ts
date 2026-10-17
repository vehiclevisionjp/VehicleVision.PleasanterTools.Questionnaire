import type { APIRequestContext } from '@playwright/test';
import { currentRevision } from './survey';

/**
 * v0.12.0 の編集・回答画面を撮るための見本を作る。
 *
 * 移動の警告、ページ下部の釦、選択肢の備考を 1 つの定義に集め、
 * 管理画面と回答画面で同じ設定を確かめられるようにする。
 */
export async function prepareFeatureSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title: '新機能の見本', pleasanterSiteId },
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
    title: { ja: '新機能の見本' },
    description: { ja: '取扱説明書に載せるための見本です。' },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: false,
    pages: [
      {
        pageId: 'page-start',
        title: { ja: '利用状況' },
        next: { kind: 'Page', pageId: 'page-finish' },
        questions: [
          {
            questionId: 'q-approval',
            type: 'Radio',
            title: { ja: 'サービスを利用していますか' },
            isRequired: true,
            choices: [
              {
                value: 'yes',
                label: { ja: '利用している' },
                remark: { ja: '継続して利用している方を選んでください。' },
              },
              {
                value: 'no',
                label: { ja: '利用していない' },
                remark: { ja: '利用予定がない場合はこちらを選んでください。' },
              },
            ],
            settings: {},
          },
          {
            questionId: 'q-details',
            type: 'Text',
            title: { ja: '利用しているサービス名' },
            isRequired: false,
            choices: [],
            settings: {},
            visibleWhen: {
              match: 'All',
              rules: [{ questionId: 'q-approval', operator: 'Equals', value: 'yes' }],
            },
          },
          {
            questionId: 'q-notice',
            type: 'Checkbox',
            title: { ja: 'お知らせの受け取り方法' },
            isRequired: false,
            choices: [
              {
                value: 'mail',
                label: { ja: 'メール' },
                remark: { ja: '登録済みのメールアドレスへお送りします。' },
              },
              {
                value: 'portal',
                label: { ja: 'ポータル' },
                remark: { ja: 'ポータルの通知一覧で確認できます。' },
              },
            ],
            settings: {},
          },
        ],
        buttons: {
          next: {
            enabledWhen: {
              match: 'All',
              rules: [{ questionId: 'q-approval', operator: 'Equals', value: 'yes' }],
            },
          },
          links: [
            {
              buttonId: 'button-guide',
              label: { ja: '利用案内を開く' },
              url: 'https://www.example.com/guide',
              openInNewTab: true,
              sortOrder: 0,
            },
          ],
        },
      },
      {
        pageId: 'page-finish',
        title: { ja: '確認' },
        questions: [
          {
            questionId: 'q-confirm',
            type: 'Confirm',
            title: { ja: '利用規約に同意します' },
            isRequired: false,
            choices: [],
            settings: {},
          },
        ],
        buttons: {
          submit: {
            enabledWhen: {
              match: 'All',
              rules: [{ questionId: 'q-confirm', operator: 'Equals', value: 'true' }],
            },
          },
        },
      },
    ],
  };

  const saved = await request.put(`/api/admin/surveys/${surveyId}`, {
    data: {
      revision: await currentRevision(request, surveyId),
      definition,
      mapping: { assignments: [] },
    },
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
