import { acceptLanguageHeader, t } from './i18n/state.svelte';
import type { NoteBlock } from '../../lib/types';
import { appUrl } from '../../lib/basePath';
import type { AltchaChallenge } from '../../lib/altcha';
import {
  failureFromResponse,
  parseLookupResponse,
  type LookupSearchResult,
} from '../../lib/lookup';
import type {
  AdminNotificationPage,
  AdminSession,
  AdminSessionRow,
  AdminUserRow,
  ApiTokenOverview,
  ApiTokenRow,
  IssuedApiToken,
  IssuedInvitation,
  AttachmentRejectionPage,
  AuditLogFilter,
  AuditLogPage,
  DeadLetterPage,
  LookupCredentialStatus,
  LookupInspectResult,
  LookupPublishProblem,
  MappingDefinition,
  OutboxStatus,
  PasskeyRow,
  MappingProblem,
  SurveyDefinition,
  SurveyDraft,
  SurveyVersionContent,
  SurveyVersionSummary,
  QuestionImportResult,
  QuestionImportSource,
  SurveyPage,
  SurveyTemplateSummary,
  SamlSettings,
  AdminSignInSettings,
  AppSettings,
  PleasanterSsoSettings,
  PleasanterSsoTestResult,
} from './types';
import type { PleasanterSsoCheckResponse } from './pleasanterSso';
import type { AssertionJson, AttestationJson, PasskeyApi, Reauthentication } from './webauthn';

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
    // **呼び先はサブパスから始める**（Issue #465）。呼び出し側は `/api/...` のまま書いてよい
    response = await fetch(appUrl(path), {
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

export interface ApplicationVersion {
  version: string;
  commit?: string | null;
  allowInsecure: boolean;
  usesSqlite: boolean;
  databaseMigration?: {
    appliedVersion?: number | null;
    latestVersion: number;
    pendingCount: number;
    lastAppliedAt?: string | null;
    lastResult: 'none' | 'succeeded';
  } | null;
}

/** 動作中の版を読む。**認証済みの管理者にだけサーバが返す。** */
export const getApplicationVersion = () =>
  call<ApplicationVersion>('/api/admin/application/version');

export interface MaintenanceModeStatus {
  isActive: boolean;
  environmentEnabled: boolean;
  databaseEnabled: boolean;
  messageJa: string;
  messageEn: string;
  enabledAt?: string | null;
  enabledByAdminUserId?: string | null;
}

/** 環境変数と DB の両方を含むメンテナンス状態を読む。 */
export const getMaintenanceMode = () =>
  call<MaintenanceModeStatus>('/api/admin/maintenance/');

/** DB 側のメンテナンス状態だけを変更する。環境変数側は変更できない。 */
export const setMaintenanceMode = (
  enabled: boolean,
  messageJa: string,
  messageEn: string,
) =>
  call<MaintenanceModeStatus>('/api/admin/maintenance/', {
    method: 'PUT',
    json: { enabled, messageJa, messageEn },
  });

export const setupFirstAdministrator = (loginId: string, password: string) =>
  call<{ next: string }>('/api/admin/setup', { method: 'POST', json: { loginId, password } });

export const getAdminCaptchaChallenge = () =>
  call<AltchaChallenge>('/api/admin/captcha/challenge');

export const login = (loginId: string, password: string, altcha?: string) =>
  call<{ next: string }>('/api/admin/login', {
    method: 'POST',
    json: { loginId, password, altcha },
  });

export const acceptInvitation = (token: string, password: string, altcha?: string) =>
  call<{ next: string }>('/api/admin/invitations/accept', {
    method: 'POST',
    json: { token, password, altcha },
  });

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

// ---- 管理者の管理（Issue #156）------------------------------------------------
// **入口を隠すだけでは守りにならない。** 権限はサーバ側で見ている
// （`users.read` / `users.write` / `users.resetTwoFactor`）

export const listAdminUsers = () =>
  call<{ users: AdminUserRow[] }>('/api/admin/users');

/**
 * そのアンケートの再編集リンクをすべて無効にする（Issue #202）。
 *
 * **漏れたときの止め方。** 回答そのものは消えない。
 */
export const revokeEditLinks = (surveyId: string) =>
  call<{ revoked: number }>(`/api/admin/surveys/${surveyId}/edit-links/revoke`, {
    method: 'POST',
    json: {},
  });

/** 管理者を追加し、招待を 1 通出す。**トークンはこの応答にしか出ない。** */
export const inviteAdminUser = (loginId: string, role: string) =>
  call<IssuedInvitation>('/api/admin/users', { method: 'POST', json: { loginId, role } });

/** 招待を出し直す。**前の招待は使えなくなる。** */
export const reissueInvitation = (adminUserId: string) =>
  call<IssuedInvitation>(`/api/admin/users/${adminUserId}/invitation`, {
    method: 'POST',
    json: {},
  });

export const setAdminUserRole = (adminUserId: string, role: string) =>
  call<{ role: string }>(`/api/admin/users/${adminUserId}/role`, {
    method: 'POST',
    json: { role },
  });

export const setAdminUserDisabled = (adminUserId: string, isDisabled: boolean) =>
  call<{ isDisabled: boolean }>(
    `/api/admin/users/${adminUserId}/${isDisabled ? 'disable' : 'enable'}`,
    { method: 'POST', json: {} },
  );

/**
 * 「パスワードなし（SSO のみ）」の印を付ける・外す（Issue #1086）。`users.write`。
 *
 * **ログインの成否は変えない**（パスワードのハッシュにもセッションにも触れない）。
 * 変わるのは、パスキーの最後の 1 件を消してよいかの判定だけ。操作の記録に残る。
 * **自分を「パスワードあり」へ戻す操作は 409**（知らない印で自分を締め出さないため）。
 */
export const setAdminUserPasswordless = (adminUserId: string, passwordless: boolean) =>
  call<{ passwordless: boolean }>(`/api/admin/users/${adminUserId}/passwordless`, {
    method: 'POST',
    json: { passwordless },
  });

/**
 * 他人の 2 要素を解除する（端末を失ったときの救済）。
 *
 * ⚠️ **保護を外す操作。** 操作の記録に残る。
 * **次に入るときは登録し直しになる**（方針が必須のとき）。
 */
export const resetAdminUserTwoFactor = (adminUserId: string, revokeApiTokens = true) =>
  call<{ reset: boolean; revokedApiTokens: number }>(
    `/api/admin/users/${adminUserId}/totp/reset`,
    {
      method: 'POST',
      // **「その人の API キーも失効させる」のチェック**（Issue #1025。初期値はオン）
      json: { revokeApiTokens },
    },
  );

/**
 * 他人のパスキーをすべて削除する（復旧。Issue #1087。Core の仕様書 3.10.9）。
 *
 * ⚠️ **取り返しのつかない操作。** 対象者の**全セッションが失効**し、監査ログに残る。
 * **自分は対象にできない**（400 `users.selfNotAllowed`。自分の分は「自分の設定」から削除する）。
 * 失敗は `{ code }` だけが返る（文言は `lib/adminUsers.ts` の `resetPasskeysFailureKey` が作る）。
 */
export const resetAdminUserPasskeys = (adminUserId: string) =>
  call<{ reset: boolean; removed: number }>(
    `/api/admin/users/${adminUserId}/passkeys/reset`,
    { method: 'POST', json: {} },
  );

// ---- 自分のアカウント（Issue #156）--------------------------------------------

export const changeOwnPassword = (
  currentPassword: string,
  newPassword: string,
  revokeApiTokens = true,
) =>
  call<{ changed: boolean; revokedApiTokens: number }>('/api/admin/me/password', {
    method: 'POST',
    // **「この人の API キーも失効させる」のチェック**（Issue #1025。初期値はオン）
    json: { currentPassword, newPassword, revokeApiTokens },
  });

/** 自分の 2 要素を登録し直す（端末を替えたとき）。**パスワードの再確認つき。** */
export const beginOwnTotp = (password: string) =>
  call<{ secret: string; uri: string }>('/api/admin/me/totp/begin', {
    method: 'POST',
    json: { password },
  });

export const completeOwnTotp = (code: string) =>
  call<{ recoveryCodes: string[] }>('/api/admin/me/totp/complete', {
    method: 'POST',
    json: { code },
  });

/**
 * 自分の 2 要素を解除する。**パスワードの再確認つき。**
 *
 * ⚠️ **必須にしている間は断られる**（設定を無視して保護を外せないため）。
 * **口は DELETE ではなく POST**（Issue #170。最小 API は DELETE の本文を推論しない）。
 */
export const disableOwnTotp = (password: string, revokeApiTokens = true) =>
  call<{ removed: boolean; revokedApiTokens: number }>('/api/admin/me/totp/disable', {
    method: 'POST',
    json: { password, revokeApiTokens },
  });

// ---- パスキー（Issue #1068。Core の仕様書 3.10.6・API 契約 5.3.1。Core#222 で確定）---
//
// **経路・形はこの節だけに閉じ込める。** 画面は `passkeyApi` の戻り値しか見ない。試験ではこのオブジェクトを差し替える。
// エラーは `{ code }`（Core は文言を持たない）。理由の鍵は `Result.body.code` から `lib/passkeys.ts` が文言にする。
// 削除は DELETE ではなく POST `…/remove`（API 契約 5.6.1）。

/** パスキーの API。 */
export const passkeyApi = {
  /** 自分のパスキーの一覧。公開鍵・資格情報 ID は返らない。再認証は要らない。 */
  list: () => call<{ passkeys: PasskeyRow[] }>('/api/admin/me/passkeys'),

  /** 登録の開始。**再認証の証拠（`{password}` か `{assertion}`）を要求ごとに添える**（時間窓は 0）。 */
  beginRegistration: (proof: Reauthentication) =>
    call<unknown>('/api/admin/me/passkeys/begin', { method: 'POST', json: proof }),

  /** 登録の完了。`name` は本人が付ける名前（空ならサーバが `Passkey` にする）。 */
  completeRegistration: (name: string, credential: AttestationJson) =>
    call<{ passkey: PasskeyRow }>('/api/admin/me/passkeys/complete', {
      method: 'POST',
      json: { credential, name },
    }),

  /** 名前の変更（再認証は要らない）。 */
  rename: (id: string, name: string) =>
    call<{ name: string }>(`/api/admin/me/passkeys/${encodeURIComponent(id)}`, {
      method: 'PUT',
      json: { name },
    }),

  /** 削除。**再認証の証拠を添える。** 最後のログイン方法になるときは 409 `passkeys.lastSignInMethod`。 */
  remove: (id: string, proof: Reauthentication) =>
    call<{ removed: boolean }>(`/api/admin/me/passkeys/${encodeURIComponent(id)}/remove`, {
      method: 'POST',
      json: proof,
    }),

  /** ログインの開始（認証の外。ユーザー名は送らない）。パスキーでの再認証のオプションにも使う。 */
  beginSignIn: () =>
    call<unknown>('/api/admin/login/passkey/begin', { method: 'POST', json: {} }),

  /** ログインの完了。成功するとサーバが Session（via=passkey）を発行する。 */
  completeSignIn: (credential: AssertionJson) =>
    call<{ next?: string }>('/api/admin/login/passkey/complete', {
      method: 'POST',
      json: { credential },
    }),
} satisfies PasskeyApi & Record<string, unknown>;

// ---- API キー（個人トークン。Issue #1025）------------------------------------

/** 自分の API キーの一覧と、発行の画面が出す情報。**値は含まない。** */
export const getOwnApiTokens = () => call<ApiTokenOverview>('/api/admin/me/api-tokens');

/** 発行の依頼。 */
export interface ApiTokenIssueRequest {
  name: string;
  scopes: string[];
  expiresInDays: number;
  ipAllowList: string[];
  /** パスワードの再確認（SSO の管理者は直前の再ログインなので不要）。 */
  password?: string;
}

/**
 * API キーを発行する。**平文を受け取れるのはこの応答だけ**（再表示はできない）。
 * 呼び出し側は、値をログ・ストレージ・URL へ置かないこと。
 */
export const issueApiToken = (request: ApiTokenIssueRequest) =>
  call<IssuedApiToken>('/api/admin/me/api-tokens', { method: 'POST', json: request });

/**
 * SAML の管理者が、API キーの発行の前に IdP での強制再認証（ForceAuthn）を始める（Issue #1034）。
 * 返る `redirectUrl`（IdP へ向かう URL）へブラウザを移す。**戻り先は指定できない**（サーバが固定する）。
 */
export const startApiTokenReauth = () =>
  call<{ redirectUrl: string }>('/api/admin/saml/reauth', { method: 'POST', json: {} });

/** 他人の API キーの一覧（`users.read`）か、自分の一覧。 */
export const listAdminUserApiTokens = (adminUserId: string) =>
  call<{ tokens: ApiTokenRow[] }>(`/api/admin/users/${adminUserId}/api-tokens`);

/** API キーを失効させる。`adminUserId` を渡すと他人のもの（`users.write`）。 */
export const revokeApiToken = (adminApiTokenId: string, adminUserId?: string) =>
  call<{ revoked: boolean }>(
    adminUserId === undefined
      ? `/api/admin/me/api-tokens/${adminApiTokenId}/revoke`
      : `/api/admin/users/${adminUserId}/api-tokens/${adminApiTokenId}/revoke`,
    { method: 'POST', json: {} },
  );

export const logout = () => call<{ signedOut: boolean }>('/api/admin/logout', { method: 'POST', json: {} });

export const listAdminSessions = (adminUserId?: string) =>
  call<{ sessions: AdminSessionRow[] }>(
    adminUserId === undefined
      ? '/api/admin/me/sessions'
      : `/api/admin/users/${adminUserId}/sessions`,
  );

export const revokeAdminSession = (adminSessionId: string, adminUserId?: string) =>
  call<{ revoked: boolean }>(
    adminUserId === undefined
      ? `/api/admin/me/sessions/${adminSessionId}/revoke`
      : `/api/admin/users/${adminUserId}/sessions/${adminSessionId}/revoke`,
    { method: 'POST', json: {} },
  );

export const revokeOtherOwnSessions = () =>
  call<{ revoked: number }>('/api/admin/me/sessions/revoke-others', {
    method: 'POST',
    json: {},
  });

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

/** 新しい回答のまとめ通知をメールで受け取るかを保存する。 */
export const saveResponseNotification = (enabled: boolean) =>
  call<{ enabled: boolean }>('/api/admin/me/response-notification', {
    method: 'PUT',
    json: { enabled },
  });

// ---- SAML 設定（Issue #254）-------------------------------------------------

// ---- Pleasanter のログイン（Issue #464） -------------------------------------

/**
 * Pleasanter にログインしているかをサーバに確かめさせ、していれば本アプリへ入る。
 *
 * **本文は空の JSON。** 他所のサイトの form から呼ばせないために JSON で送る。
 */
export const checkPleasanterSso = () =>
  call<PleasanterSsoCheckResponse>('/api/admin/pleasanter-sso/check', { method: 'POST', json: {} });

export const getPleasanterSsoSettings = () =>
  call<PleasanterSsoSettings>('/api/admin/pleasanter-sso/settings');

export const savePleasanterSsoSettings = (settings: PleasanterSsoSettings) =>
  call<PleasanterSsoSettings>('/api/admin/pleasanter-sso/settings', {
    method: 'PUT',
    json: settings,
  });

/** 保存前の値で、いまのブラウザの Pleasanter の cookie を使って問い合わせる。 */
export const testPleasanterSsoSettings = (settings: PleasanterSsoSettings) =>
  call<PleasanterSsoTestResult>('/api/admin/pleasanter-sso/settings/test', {
    method: 'POST',
    json: settings,
  });

export const getSamlSettings = () =>
  call<SamlSettings>('/api/admin/saml/settings');

export const saveSamlSettings = (settings: SamlSettings) =>
  call<SamlSettings>('/api/admin/saml/settings', {
    method: 'PUT',
    json: settings,
  });

export const getAdminSignInSettings = () =>
  call<AdminSignInSettings>('/api/admin/sign-in-settings');

export const saveAdminSignInSettings = (passwordSignInEnabled: boolean) =>
  call<AdminSignInSettings>('/api/admin/sign-in-settings', {
    method: 'PUT',
    json: { passwordSignInEnabled },
  });

export const testSamlMetadata = (metadataUrl: string) =>
  call<{ reachable: boolean; entityId?: string | null }>('/api/admin/saml/settings/test', {
    method: 'POST',
    json: { metadataUrl },
  });

// ---- アプリケーション設定（Issue #372）-------------------------------------

export const getAppSettings = () =>
  call<AppSettings>('/api/admin/settings');

const appSettingValues = (settings: AppSettings) =>
  Object.fromEntries(
    settings.fields
      .filter((field) => !field.isSecret || (field.value?.trim() ?? '') !== '')
      .map((field) => [field.key, field.value]),
  );

export const saveAppSettings = (settings: AppSettings) =>
  call<AppSettings>('/api/admin/settings', {
    method: 'PUT',
    json: {
      // 秘密欄は空なら送らず、既存値を保つ。入力した値だけを置き換える。
      values: appSettingValues(settings),
    },
  });

/** 保存せず、入力中のメール設定でログイン中の管理者へ 1 通送る。 */
export const testAppSettingsMail = (settings: AppSettings) =>
  call<{ sent: boolean }>('/api/admin/settings/test-send', {
    method: 'POST',
    json: { values: appSettingValues(settings) },
  });

export const testAppSettings = (settings: AppSettings) =>
  call<{ connected: boolean }>('/api/admin/settings/test', {
    method: 'POST',
    json: { values: appSettingValues(settings) },
  });

// ---- アンケート -------------------------------------------------------------

/**
 * アンケートの一覧（Issue #79 でページ送りと絞り込みを入れた）。
 *
 * **全件は返らない。** 絞り込みは題名の部分一致と状態。
 * `status` に `null` を渡すと絞り込まない。
 */
export const listSurveys = (
  offset: number,
  limit: number,
  title: string,
  status: number | null,
  includeArchived = false,
) => {
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });

  if (title.trim() !== '') {
    query.set('title', title.trim());
  }

  if (status !== null) {
    query.set('status', String(status));
  }

  if (includeArchived) {
    query.set('includeArchived', 'true');
  }

  return call<SurveyPage>(`/api/admin/surveys?${query}`);
};

export const createSurvey = (
  title: string,
  pleasanterSiteId: number,
  responseJsonColumn?: string,
  createPleasanterSite = false,
) =>
  call<{ surveyId: string; publicId: string }>('/api/admin/surveys', {
    method: 'POST',
    json: { title, pleasanterSiteId, responseJsonColumn: responseJsonColumn || null, createPleasanterSite },
  });

/**
 * アンケートを複製する（Issue #46）。
 *
 * **公開用 ID はサーバが作り直す。** 使い回すと 2 つのアンケートが同じ URL を指す。
 * **書き込み先のサイトはコピーしない**ので、ここで新しく指定する。
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

/**
 * エクスポートした JSON から、新しいアンケートを下書きとしてインポートする（Issue #1009）。
 *
 * **書き込み先のサイトはエクスポートに入っていない**ので、ここで指定する。
 * **公開前の検査と同じ検査をサーバが行い、通らなければ 1 件も作らない。**
 */
export const importSurvey = (
  document: unknown,
  pleasanterSiteId: number,
  responseJsonColumn?: string,
) =>
  call<{ surveyId: string; publicId: string }>('/api/admin/surveys/import', {
    method: 'POST',
    json: { document, pleasanterSiteId, responseJsonColumn: responseJsonColumn || null },
  });

/** エクスポートのダウンロード先。**認証は cookie なので、リンクで開いてよい。** */
export const surveyExportUrl = (surveyId: string) =>
  appUrl(`/api/admin/surveys/${surveyId}/export`);

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
 * **題名は指定しない。** 元のアンケートの題名をそのままコピーする。
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

/** 固めた版の一覧（新しい順）。Issue #544 */
export const listVersions = (surveyId: string) =>
  call<SurveyVersionSummary[]>(`/api/admin/surveys/${surveyId}/versions`);

/** 固めた版の中身。**読むだけ。** 編集へ読み込むかどうかは画面が決める */
export const loadVersion = (surveyId: string, version: number) =>
  call<SurveyVersionContent>(`/api/admin/surveys/${surveyId}/versions/${version}`);

/** インポート元のページと設問を読む。**マッピングは返さない。** */
export const loadQuestionImportSource = (surveyId: string, sourceSurveyId: string) =>
  call<QuestionImportSource>(
    `/api/admin/surveys/${surveyId}/question-import/${sourceSurveyId}`,
  );

/**
 * 選んだ設問をインポート先用にコピーする。
 *
 * **ID の再採番と、分岐・表示条件・資産参照の除去はサーバが行う。**
 */
export const importQuestions = (
  surveyId: string,
  sourceSurveyId: string,
  questionIds: string[],
  existingQuestionIds: string[],
) =>
  call<QuestionImportResult>(
    `/api/admin/surveys/${surveyId}/question-import/${sourceSurveyId}`,
    {
      method: 'POST',
      json: { questionIds, existingQuestionIds },
    },
  );

/** 選択肢 1 件（`値,表示文字列`）。 */
export interface ColumnChoice {
  value: string;
  text: string;
}

/** 列の設定。**編集画面と一覧で別の項目名を付けられる**ので両方持つ（どちらも省略可）。 */
export interface ColumnDetail {
  /** 編集画面の項目名。 */
  label?: string;
  /** 一覧の項目名。 */
  gridLabel?: string;
  /** リンク項目か（`Link: true`、または JSON のリンク定義がある）。 */
  isLink: boolean;
  /** リンクの指定の形。**JSON の形は `Link: true` が付かない**ので、別に持つ。 */
  linkFormat?: 'Lines' | 'Json';
  /** 選択肢（上限あり。全件の数は `choiceCount`）。 */
  choices: ColumnChoice[];
  choiceCount: number;
  /** 参照先（`[[…]]` の中身、または JSON の `SiteId`。サイト ID・Users・Depts など）。 */
  references: string[];
  /** 選択肢の見せ方（`Radio` など）。選択肢があるときだけ。 */
  choicesControlType?: string;
}

/** マッピング先サイトの列数。取得できないときは標準の本数を使う。 */
export interface ColumnAvailabilityResponse {
  source: 'site' | 'standard';
  availableByPrefix: Record<string, number>;
  /**
   * 列の物理名ごとの設定（項目名・リンク・選択肢。Issue #549）。
   * **設定を付けた列だけ**（Pleasanter は付けていない列を返さない）。古いサーバは返さないので省略可
   */
  columns?: Record<string, ColumnDetail>;
}

export const loadColumnAvailability = (surveyId: string) =>
  call<ColumnAvailabilityResponse>(`/api/admin/surveys/${surveyId}/column-availability`);

/** 埋め込みを許す配信元（Issue #104 / #107）。**運用側の設定なので変わらない。** */
export interface EmbedOptions {
  enabled: boolean;
  /** `www.example.com` か `*.example.net` の形。 */
  allowedHosts: string[];
}

export const loadEmbedOptions = () =>
  call<EmbedOptions>('/api/admin/surveys/embed-options');

/** 回答画面を埋め込める親サイト（Issue #334）。**運用側の設定なので変わらない。** */
export interface EmbedParentOptions {
  enabled: boolean;
  /** `www.example.com` か `*.example.net` の形。 */
  allowedParents: string[];
}

export const loadEmbedParentOptions = () =>
  call<EmbedParentOptions>('/api/admin/surveys/embed-parent-options');

export const saveDraft = (
  surveyId: string,
  definition: SurveyDefinition,
  mapping: MappingDefinition,
  revision: number,
  assetHistorySiteId: number,
  assetHistoryMapping: MappingDefinition,
) =>
  call<{
    revision: number;
    /**
     * テスト公開中の保存は、サーバが同時に版を固め直す（Issue #1057）。
     * `true` なら反映した（`version`）、`false` なら保存はできたが版は固められなかった（`reflectError`）。
     * テスト公開中でなければ付かない。
     */
    reflected?: boolean;
    version?: number;
    warnings?: MappingProblem[];
    reflectError?: string;
  }>(`/api/admin/surveys/${surveyId}`, {
    method: 'PUT',
    json: { definition, mapping, revision, assetHistorySiteId, assetHistoryMapping },
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

/** 説明文と完了画面で使う自前画像を上げる（Issue #266 / #269）。 */
export interface AssetOptions {
  allowedExtensions: string[];
  maxFileSizeBytes: number;
  maxFileCount: number;
}

export const loadAssetOptions = () =>
  call<AssetOptions>('/api/admin/surveys/asset-options');

export const uploadContentAsset = (surveyId: string, file: File) => {
  const body = new FormData();
  body.append('asset', file);

  return call<{ assetId: string; isImage: boolean }>(`/api/admin/surveys/${surveyId}/assets`, {
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
  appUrl(`/api/admin/surveys/${encodeURIComponent(surveyId)}/assets/${encodeURIComponent(assetId)}`);

export const loadProblems = (surveyId: string) =>
  call<MappingProblem[]>(`/api/admin/surveys/${surveyId}/problems`);

export const testPublish = (surveyId: string) =>
  call<{ version: number; warnings: MappingProblem[] }>(`/api/admin/surveys/${surveyId}/test-publish`, {
    method: 'POST',
    json: {},
  });

/** テスト公開中の編集を反映する。**下書きへ戻さない。** 失敗してもテスト公開のまま残る（Issue #1008）。 */
export const testRepublish = (surveyId: string) =>
  call<{ version: number; warnings: MappingProblem[] }>(`/api/admin/surveys/${surveyId}/test-republish`, {
    method: 'POST',
    json: {},
  });

/**
 * 本公開する（Issue #1057）。**下書きからも、テスト公開中からも押せる。**
 *
 * - 下書きからは、その場で版を固めて本公開する（`reflectDraft` は効かない）
 * - テスト公開中からは、固めた版のまま本公開する。`reflectDraft: true` なら、下書きを新しい版へ
 *   固めてから本公開する（テスト公開後に編集した分を入れる）
 */
export const publish = (surveyId: string, reflectDraft = false) =>
  call<{ status: string; version: number; warnings?: MappingProblem[] }>(
    `/api/admin/surveys/${surveyId}/publish`,
    {
      method: 'POST',
      json: { reflectDraft },
    },
  );

export const revertToDraft = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/revert-to-draft`, {
    method: 'POST',
    json: {},
  });

export const updateSurveySiteId = (surveyId: string, pleasanterSiteId: number) =>
  call<{ pleasanterSiteId: number }>(`/api/admin/surveys/${surveyId}/site-id`, {
    method: 'PUT',
    json: { pleasanterSiteId },
  });

export interface SiteSettingsSyncPreview {
  addedColumns: string[];
  gridColumns: string[];
  editorColumns: string[];
  historyColumns: string[];
  unchanged: string[];
}

export const previewSiteSettingsSync = (surveyId: string) =>
  call<SiteSettingsSyncPreview>(`/api/admin/surveys/${surveyId}/site-settings/preview`, {
    method: 'POST',
    json: {},
  });

export const syncSiteSettings = (surveyId: string) =>
  call<{ synchronized: true }>(`/api/admin/surveys/${surveyId}/site-settings/sync`, {
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
  allowEmbedding: boolean,
  acceptFrom: string | null,
  acceptTo: string | null,
) =>
  call<{
    responseLimit: number | null;
    requireProofOfWork: boolean;
    allowDraft: boolean;
    allowEmbedding: boolean;
    acceptFrom?: string | null;
    acceptTo?: string | null;
  }>(
    `/api/admin/surveys/${surveyId}/settings`,
    {
      method: 'PUT',
      json: {
        responseLimit,
        requireProofOfWork,
        allowDraft,
        allowEmbedding,
        // **受付期間（Issue #1006）。** 空は「その側の制限なし」。ISO 8601（UTC）で送る
        acceptPeriod: { from: acceptFrom, to: acceptTo },
      },
    },
  );

export const suspend = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/suspend`, { method: 'POST', json: {} });

export const resume = (surveyId: string) =>
  call<{ status: string }>(`/api/admin/surveys/${surveyId}/resume`, { method: 'POST', json: {} });

export const archiveSurvey = (surveyId: string) =>
  call<{ archivedAt: string }>(`/api/admin/surveys/${surveyId}/archive`, {
    method: 'POST',
    json: {},
  });

export const restoreSurvey = (surveyId: string) =>
  call<{ archivedAt?: string }>(`/api/admin/surveys/${surveyId}/restore`, {
    method: 'POST',
    json: {},
  });

/**
 * アーカイブ済みアンケートを本アプリから完全に削除する。
 *
 * **Pleasanter 側の回答は削除しない。** 題名は押し間違いを防ぐ確認値で、
 * サーバでも現在の題名との完全一致を確かめる。
 */
/** Pleasanter 側のテスト回答の削除の結果（Issue #1010）。 */
export interface TestResponseCleanupResult {
  deleted: number;
  alreadyGone: number;
  skipped: number;
  failed: number;
  remaining: number;
}

/**
 * 管理画面から消せるテスト回答の件数（Issue #1010）。
 *
 * **確認ダイアログに出す件数は、一覧の件数ではなくこちらを使う。**
 * 一覧の件数には、まだ Pleasanter へ届いていない回答も含まれる。
 */
export const countDeletableTestResponses = (surveyId: string) =>
  call<{ deletable: number; maxPerRun: number }>(
    `/api/admin/surveys/${surveyId}/test-responses`,
  );

/**
 * Pleasanter 側のテスト回答を削除する（Issue #1010）。**取り消せない。**
 *
 * **画面に見せた件数（expectedCount）を必ず送る。** 今の件数と違えばサーバは何も消さない。
 * 消す対象はサーバが自分の記録から決め、**ここから ID は送らない。**
 */
export const deleteTestResponses = (surveyId: string, expectedCount: number) =>
  call<TestResponseCleanupResult>(`/api/admin/surveys/${surveyId}/test-responses/delete`, {
    method: 'POST',
    json: { expectedCount },
  });

export const deleteSurvey = (surveyId: string, title: string) =>
  call<void>(`/api/admin/surveys/${surveyId}/delete`, {
    method: 'POST',
    json: { title },
  });

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

// ---- 管理者への知らせ（Issue #80）--------------------------------------------

/**
 * 溜まっている知らせを読む。
 *
 * **未読の件数も同じ応答で返る。** バッジのために別の呼び出しを増やさない。
 */
export const listNotifications = (offset: number, limit: number) => {
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });

  return call<AdminNotificationPage>(`/api/admin/notifications?${query}`);
};

/**
 * すべて既読にする。
 *
 * **既読は全体で 1 つ。** 誰かが気付いたら全員にとって既読になる。
 */
export const markNotificationsRead = () =>
  call<{ read: number }>('/api/admin/notifications/read', { method: 'POST' });

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

/** 記法 1 件を読んだ結果（Issue #108）。 */
export interface NotePreviewResult {
  blocks: NoteBlock[];
  /** 上限を超えて切り落とされたか。 */
  truncated: boolean;
}

/**
 * 説明文ブロックの記法を読んでもらう（Issue #108）。
 *
 * **読むのはサーバだけ。** 画面に同じ実装を置くと、
 * プレビューで通った書き方が公開後に通らないというずれが起きる。
 * **渡した順で返る。**
 */
export const previewNotes = (markups: (string | null)[]) =>
  call<{ results: NotePreviewResult[] }>('/api/admin/note/preview', {
    method: 'POST',
    json: { markups },
  });

export interface AutoReplyPreview {
  fromAddress: string;
  fromName: string | null;
  toAddress: string;
  replyToAddress: string | null;
  bccAddress: string | null;
  subject: string;
  body: string;
  unknownKeywords: string[];
}

/** 保存前の定義から、本番と同じ処理で自動返信を組み立てる（Issue #319）。 */
export const previewAutoReply = (definition: SurveyDefinition, language: string) =>
  call<AutoReplyPreview>('/api/admin/auto-reply/preview', {
    method: 'POST',
    json: { definition, language },
  });

/** 保存前の定義を、ログイン中の管理者本人へ試し送信する（Issue #319）。 */
export const sendAutoReplyTest = (definition: SurveyDefinition, language: string) =>
  call<{ queued: boolean }>('/api/admin/auto-reply/test-send', {
    method: 'POST',
    json: { definition, language },
  });

// ---- ルックアップ（Issue #880） ----------------------------------------------

const lookupUrl = (surveyId: string, path: string) =>
  `/api/admin/surveys/${encodeURIComponent(surveyId)}/lookups${path}`;

/**
 * 専用資格情報の状態を読む。**秘密は返ってこない**（設定済みの有無・有効状態・更新日時だけ）。
 */
export const loadLookupCredentials = (surveyId: string) =>
  call<LookupCredentialStatus[]>(lookupUrl(surveyId, '/credentials'));

/**
 * 専用 API キーを登録する。**キーは本文で送るだけで、応答には載らない。**
 * 呼び出し側は送ったら入力欄を空にすること。
 */
export const createLookupCredential = (surveyId: string, apiKey: string) =>
  call<LookupCredentialStatus>(lookupUrl(surveyId, '/credentials'), {
    method: 'POST',
    json: { apiKey },
  });

/** 専用 API キーを入れ替える（再入力）。公開済みの版は書き換わらない。 */
export const rotateLookupCredential = (surveyId: string, credentialId: string, apiKey: string) =>
  call<LookupCredentialStatus>(
    lookupUrl(surveyId, `/credentials/${encodeURIComponent(credentialId)}`),
    { method: 'PUT', json: { apiKey } },
  );

/** 失効（`false`）・再開（`true`）。**公開し直さずに全公開版の実行時へ効く。** */
export const setLookupCredentialEnabled = (
  surveyId: string,
  credentialId: string,
  enabled: boolean,
) =>
  call<LookupCredentialStatus>(
    lookupUrl(surveyId, `/credentials/${encodeURIComponent(credentialId)}/enabled`),
    { method: 'PUT', json: { enabled } },
  );

/** 専用資格情報を解除（削除）する。 */
export const deleteLookupCredential = (surveyId: string, credentialId: string) =>
  call<void>(lookupUrl(surveyId, `/credentials/${encodeURIComponent(credentialId)}`), {
    method: 'DELETE',
  });

/** 接続試験と列の取得。**読み取りだけ。** 結果は分類・統合/継承の有無・列の名前と種類だけ。 */
export const inspectLookupSource = (
  surveyId: string,
  credentialId: string,
  sourceSiteId: number,
) =>
  call<LookupInspectResult>(lookupUrl(surveyId, '/inspect'), {
    method: 'POST',
    json: { credentialId, sourceSiteId },
  });

/** 保存済みの下書きのルックアップの公開前検査（テスト公開と同じ検証）。 */
export const loadLookupProblems = (surveyId: string) =>
  call<LookupPublishProblem[]>(lookupUrl(surveyId, '/problems'));

/**
 * 管理プレビューの検索。**公開 API とは別経路**で、保存済みの下書きのルックアップを使う
 * （回答者向けのトークン・版の照合は通らない）。失敗は種類で返し、サーバの本文は出さない。
 */
export async function previewSearchLookup(
  surveyId: string,
  lookupId: string,
  keys: Record<string, string>,
): Promise<LookupSearchResult> {
  const result = await call<unknown>(
    lookupUrl(surveyId, `/${encodeURIComponent(lookupId)}/preview-search`),
    { method: 'POST', json: { keys } },
  );

  if (result.ok) {
    const parsed = parseLookupResponse(result.value);
    return parsed
      ? { ok: true, response: parsed }
      : { ok: false, failure: 'sourceInvalid', fieldIds: [], retryAfterSeconds: null };
  }

  if (result.status === 0) {
    return { ok: false, failure: 'network', fieldIds: [], retryAfterSeconds: null };
  }

  const body = (result.body ?? {}) as { code?: string; fieldIds?: unknown };
  return {
    ok: false,
    failure: failureFromResponse(body.code, result.status),
    fieldIds: Array.isArray(body.fieldIds)
      ? body.fieldIds.filter((id): id is string => typeof id === 'string')
      : [],
    retryAfterSeconds: null,
  };
}
