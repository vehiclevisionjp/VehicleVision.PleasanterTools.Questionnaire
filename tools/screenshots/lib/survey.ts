import type { APIRequestContext } from '@playwright/test';

/** 保存に使う、その時点の下書きの版を読む。 */
export async function currentRevision(
  request: APIRequestContext,
  surveyId: string,
): Promise<number> {
  const draft = await request.get(`/api/admin/surveys/${surveyId}`);
  if (!draft.ok()) {
    throw new Error(`下書きを読めなかった: ${draft.status()} ${await draft.text()}`);
  }

  const body = (await draft.json()) as { revision: number };
  return body.revision;
}
