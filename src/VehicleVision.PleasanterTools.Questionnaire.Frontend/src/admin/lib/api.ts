import { acceptLanguageHeader, t } from './i18n/state.svelte';
import type {
  AdminSession,
  AttachmentRejectionPage,
  AuditLogFilter,
  AuditLogPage,
  DeadLetterPage,
  MappingDefinition,
  OutboxStatus,
  MappingProblem,
  SurveyDefinition,
  SurveyDraft,
  SurveySummary,
  SurveyTemplateSummary,
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

/**
 * アンケートを複製する（Issue #46）。
 *
 * **公開用 ID はサーバが作り直す。** 使い回すと 2 つのアンケートが同じ URL を指す。
 * **書き込み先のサイトは写さない**ので、ここで新しく指定する。
 * **Administrator だけが通る**（サーバ側で判定する）。
 */
export const duplicateSurvey = (
  surveyId: string,
  pleasanterSiteId: number,
  responseJsonColumn?: string,
) =>
  call<{ surveyId: string; publicId: string }>(`/api/admin/surveys/${surveyId}/duplicate`, {
    method: 'POST',
    json: { pleasanterSiteId, responseJsonColumn: responseJsonColumn || null },
  });

// ---- テンプレート -----------------------------------------------------------

/**
 * テンプレートの一覧（Issue #58）。
 *
 * **アンケートの一覧には出てこない。** 逆も同じ。
 */
export const listTemplates = () => call<SurveyTemplateSummary[]>('/api/admin/templates');

/**
 * アンケートをテンプレートにする。
 *
 * **題名は指定しない。** 元のアンケートの題名をそのまま写す。
 * 題名は多言語の器なので、1 つの文字列を送ると
 * 日本語の題名が英語として保存されてしまう。
 */
export const saveAsTemplate = (surveyId: string) =>
  call<{ templateId: string }>('/api/admin/templates', {
    method: 'POST',
    json: { surveyId },
  });

/**
 * テンプレートからアンケートを作る。
 *
 * **書き込み先のサイトはここで指定する。** テンプレートは持っていない。
 * **公開用 ID はサーバが作る**ので、同じテンプレートから作った
 * アンケートが同じ URL を指すことはない。
 */
export const createSurveyFromTemplate = (
  templateId: string,
  pleasanterSiteId: number,
  responseJsonColumn?: string,
) =>
  call<{ surveyId: string; publicId: string }>(`/api/admin/templates/${templateId}/surveys`, {
    method: 'POST',
    json: { pleasanterSiteId, responseJsonColumn: responseJsonColumn || null },
  });

/** テンプレートを消す。**消せるのはテンプレートだけ**（アンケートには回答が紐づく）。 */
export const deleteTemplate = (templateId: string) =>
  call<void>(`/api/admin/templates/${templateId}`, { method: 'DELETE' });

// ---- アンケートの下書き -----------------------------------------------------

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

/**
 * ヘッダ画像を上げる（Issue #56）。
 *
 * **返るのは識別子だけ。** 下書きへは書かないので、
 * 呼んだ側が定義のテーマへ入れて保存すること。
 * **サーバが書くと、下書きの版と照合せずに書くことになる**（黙った上書きになる）。
 *
 * **`Content-Type` は指定しない。** `FormData` を渡すと境界付きの型を
 * ブラウザが付けるので、こちらで書くと壊れる。
 */
export const uploadHeaderImage = (surveyId: string, file: File) => {
  const body = new FormData();
  body.append('image', file);

  return call<{ assetId: string }>(`/api/admin/surveys/${surveyId}/theme/header-image`, {
    method: 'POST',
    body,
  });
};

/**
 * 編集中のヘッダ画像を見る URL。
 *
 * **管理画面の口を使う。** 回答画面の口は公開中の版が指す画像しか返さないので、
 * 上げたばかりの画像はまだ出ない。
 */
export const adminAssetUrl = (surveyId: string, assetId: string): string =>
  `/api/admin/surveys/${encodeURIComponent(surveyId)}/assets/${encodeURIComponent(assetId)}`;

export const loadProblems = (surveyId: string) =>
  call<MappingProblem[]>(`/api/admin/surveys/${surveyId}/problems`);

export const publish = (surveyId: string) =>
  call<{ version: number; warnings: MappingProblem[] }>(`/api/admin/surveys/${surveyId}/publish`, {
    method: 'POST',
    json: {},
  });

/**
 * 公開設定を保存する（Issue #53）。
 *
 * **`null` で「上限なし」。** 0 はサーバが断る（誰も回答できない設定になるため）。
 * **上限を引き上げても自動では再開しない。** 再開は人が押す。
 *
 * **proof-of-work の要否**（Issue #66）**と下書きの可否**（Issue #59）**もここで切り替える。**
 * **定義ではなく運用の設定**なので、切り替えても公開し直さなくてよい。
 * サーバは省略を「変えない」と解釈するので、**必ず今の値を添えて送る。**
 */
export const saveSurveySettings = (
  surveyId: string,
  responseLimit: number | null,
  requireProofOfWork: boolean,
  allowDraft: boolean,
) =>
  call<{ responseLimit: number | null; requireProofOfWork: boolean; allowDraft: boolean }>(
    `/api/admin/surveys/${surveyId}/settings`,
    { method: 'PUT', json: { responseLimit, requireProofOfWork, allowDraft } },
  );

export const suspend = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/suspend`, { method: 'POST', json: {} });

export const resume = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/resume`, { method: 'POST', json: {} });

/**
 * 管理操作の記録を読む。
 *
 * **絞り込みはサーバへ渡す。** 全件受け取って画面で絞ると、
 * 増え続ける表を毎回そのまま送ることになる。
 */
export const listAuditLogs = (
  filter: AuditLogFilter,
  offset: number,
  limit: number,
) => {
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });

  if (filter.failedOnly) {
    query.set('failedOnly', 'true');
  }

  if (filter.action.trim() !== '') {
    query.set('action', filter.action.trim());
  }

  // **空欄は「指定なし」。** 空文字を送るとサーバ側で日付として読めない
  if (filter.from !== '') {
    query.set('from', filter.from);
  }

  if (filter.to !== '') {
    query.set('to', filter.to);
  }

  return call<AuditLogPage>(`/api/admin/audit-logs?${query}`);
};

// ---- 送信状況 ---------------------------------------------------------------

/**
 * 滞留の状況を読む。
 *
 * **数えるのはサーバ側で 1 回。** 件数と最古の時刻を別々に問い合わせない。
 */
export const getOutboxStatus = () => call<OutboxStatus>('/api/admin/outbox/status');

/** 送信できなかった回答を読む。**回答本文は返らない。** */
export const listDeadLetters = (offset: number, limit: number) => {
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });

  return call<DeadLetterPage>(`/api/admin/outbox/dead-letters?${query}`);
};

/**
 * 添付を弾いた記録を読む（Issue #39）。
 *
 * **送信元もファイル名も返らない。** サーバ側の型にも入る場所が無い。
 */
export const listAttachmentRejections = (offset: number, limit: number) => {
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });

  return call<AttachmentRejectionPage>(`/api/admin/outbox/attachment-rejections?${query}`);
};

/**
 * 送信できなかった回答を送信待ちへ戻す。
 *
 * **回答トークンは本文で送る。** URL に載せると、サーバ側で
 * 監査ログの経路の値として記録されてしまう（トークンは記録しない決まり）。
 */
export const requeueDeadLetter = (responseToken: string) =>
  call<{ requeued: boolean }>('/api/admin/outbox/dead-letters/requeue', {
    method: 'POST',
    json: { responseToken },
  });
