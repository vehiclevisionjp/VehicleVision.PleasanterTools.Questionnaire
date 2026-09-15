import type { APIRequestContext, APIResponse } from '@playwright/test';

/** アンケートの入れ物を作る。**中身はまだ入れない。** */
export async function createSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
  title: string,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title, pleasanterSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  return (await created.json()) as { surveyId: string; publicId: string };
}

/**
 * 配色を入れた下書きを保存。**応答をそのまま返す。**
 *
 * **成否を呼び出し側に判断させる。** 断られること自体を確かめたい試験があるので、
 * ここで投げてしまうと「断られた」を見られなくなる。
 */
export function saveTheme(
  request: APIRequestContext,
  surveyId: string,
  title: string,
  theme: Record<string, unknown>,
): Promise<APIResponse> {
  const definition = {
    surveyId,
    version: 1,
    title: { ja: title },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: true,
    theme,
    pages: [
      {
        pageId: 'page-1',
        questions: [
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

  return request.put(`/api/admin/surveys/${surveyId}`, {
    data: { revision: 0, definition, mapping: { assignments: [] } },
  });
}

/**
 * **配色**を確かめる見本を作って公開する（Issue #109 / #124）。
 *
 * **色の中身は画面側にしか無い。** サーバは「どれを選んだか」しか持たないので、
 * 名前の付いた配色が実際の色になるかは通してみないと分からない。
 */
export async function prepareThemeSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
  theme: Record<string, unknown>,
  title: string,
): Promise<{ surveyId: string; publicId: string }> {
  const { surveyId, publicId } = await createSurvey(request, pleasanterSiteId, title);

  const saved = await saveTheme(request, surveyId, title, theme);
  if (!saved.ok()) {
    throw new Error(`下書きを保存できなかった: ${saved.status()} ${await saved.text()}`);
  }

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}
