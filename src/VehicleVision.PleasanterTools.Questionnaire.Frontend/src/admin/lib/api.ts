import { acceptLanguageHeader, t } from './i18n/state.svelte';
import type {
  AdminSession,
  MappingDefinition,
  MappingProblem,
  SurveyDefinition,
  SurveyDraft,
  SurveySummary,
} from './types';

/**
 * 呼び出しの結果。
 *
 * **例外を投げずに返す。** 画面は「通った / 通らなかった」と
 * その理由を出し分ける必要があり、例外だと分岐が書きづらい。
 */
export type Result<T> =
  | { ok: true; value: T }
  | { ok: false; status: number; message: string; body?: unknown };

async function call<T>(
  path: string,
  init?: RequestInit & { json?: unknown },
): Promise<Result<T>> {
  const { json, ...rest } = init ?? {};

  let response: Response;
  try {
    response = await fetch(path, {
      ...rest,
      // **cookie を必ず送る。** 認証は cookie で持っている
      credentials: 'same-origin',
      headers: {
        // **画面が描いている言語をサーバへ伝える。**
        // サーバが返す文言と画面の文言を揃えるため
        // （`_documents/多言語対応方針.md` 2 章）
        'Accept-Language': acceptLanguageHeader(),
        ...(json === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...rest.headers,
      },
      body: json === undefined ? rest.body : JSON.stringify(json),
    });
  } catch {
    return { ok: false, status: 0, message: t('app.networkError') };
  }

  if (response.status === 204) {
    return { ok: true, value: undefined as T };
  }

  const raw = await response.text();
  let body: unknown = undefined;
  if (raw !== '') {
    try {
      body = JSON.parse(raw);
    } catch {
      body = raw;
    }
  }

  if (response.ok) {
    return { ok: true, value: body as T };
  }

  // **サーバの文言をそのまま出す。** 要求した言語で返ってきている
  const message =
    typeof body === 'object' && body !== null && 'message' in body
      ? String((body as { message: unknown }).message)
      : t('app.requestFailed', { status: response.status });

  return { ok: false, status: response.status, message, body };
}

// ---- 認証 -------------------------------------------------------------------

export const getSession = () => call<AdminSession>('/api/admin/session');

export const setupFirstAdministrator = (loginId: string, password: string) =>
  call<{ next: string }>('/api/admin/setup', { method: 'POST', json: { loginId, password } });

export const login = (loginId: string, password: string) =>
  call<{ next: string }>('/api/admin/login', { method: 'POST', json: { loginId, password } });

export const verifyTotp = (code: string) =>
  call<{ authenticated: boolean }>('/api/admin/login/totp', { method: 'POST', json: { code } });

export const verifyRecoveryCode = (code: string) =>
  call<{ authenticated: boolean }>('/api/admin/login/recovery', { method: 'POST', json: { code } });

export const beginEnrollment = () =>
  call<{ secret: string; uri: string }>('/api/admin/enroll/begin', { method: 'POST', json: {} });

export const completeEnrollment = (code: string) =>
  call<{ recoveryCodes: string[] }>('/api/admin/enroll/complete', {
    method: 'POST',
    json: { code },
  });

export const logout = () => call<{ signedOut: boolean }>('/api/admin/logout', { method: 'POST', json: {} });

/**
 * 管理画面を出す言語を決める。
 *
 * **`null` で「選んでいない」に戻す。** ブラウザの言語設定に従うようになる。
 */
export const saveLanguage = (language: string | null) =>
  call<{ language: string | null }>('/api/admin/me/language', {
    method: 'PUT',
    json: { language },
  });

// ---- アンケート -------------------------------------------------------------

export const listSurveys = () => call<SurveySummary[]>('/api/admin/surveys');

export const createSurvey = (title: string, pleasanterSiteId: number, responseJsonColumn?: string) =>
  call<{ surveyId: string; publicId: string }>('/api/admin/surveys', {
    method: 'POST',
    json: { title, pleasanterSiteId, responseJsonColumn: responseJsonColumn || null },
  });

export const loadDraft = (surveyId: string) => call<SurveyDraft>(`/api/admin/surveys/${surveyId}`);

export const saveDraft = (
  surveyId: string,
  definition: SurveyDefinition,
  mapping: MappingDefinition,
  revision: number,
) =>
  call<{ revision: number }>(`/api/admin/surveys/${surveyId}`, {
    method: 'PUT',
    json: { definition, mapping, revision },
  });

export const loadProblems = (surveyId: string) =>
  call<MappingProblem[]>(`/api/admin/surveys/${surveyId}/problems`);

export const publish = (surveyId: string) =>
  call<{ version: number; warnings: MappingProblem[] }>(`/api/admin/surveys/${surveyId}/publish`, {
    method: 'POST',
    json: {},
  });

export const suspend = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/suspend`, { method: 'POST', json: {} });

export const resume = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/resume`, { method: 'POST', json: {} });
