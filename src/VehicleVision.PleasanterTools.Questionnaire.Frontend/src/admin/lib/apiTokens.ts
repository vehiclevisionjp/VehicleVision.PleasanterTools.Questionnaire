/**
 * API キー（個人トークン）の画面の決まり（Issue #1025）。
 *
 * ⚠️ **ここにある判定は、画面を迷わせないためのもの。守りはサーバ側にある**
 * （スコープの許可リスト・期限の上限・役割ごとの可否・本人確認は、すべてサーバが断る）。
 */
import type { MessageKey } from './i18n/messages';
import type { ApiTokenRow, ApiTokenStatus } from './types';

/** 付けられるスコープの表示の順。**サーバの許可リストと同じ名前**（既存の権限のサブセット）。 */
export const API_TOKEN_SCOPES = ['surveys.read', 'surveys.write', 'surveys.publish'] as const;

const SCOPE_LABELS: Record<string, MessageKey> = {
  'surveys.read': 'apiTokens.scope.surveysRead',
  'surveys.write': 'apiTokens.scope.surveysWrite',
  'surveys.publish': 'apiTokens.scope.surveysPublish',
};

const STATUS_LABELS: Record<ApiTokenStatus, MessageKey> = {
  active: 'apiTokens.status.active',
  expired: 'apiTokens.status.expired',
  revoked: 'apiTokens.status.revoked',
};

const REVOKE_REASON_LABELS: Record<string, MessageKey> = {
  owner: 'apiTokens.reason.owner',
  administrator: 'apiTokens.reason.administrator',
  ownerDisabled: 'apiTokens.reason.ownerDisabled',
  passwordChanged: 'apiTokens.reason.passwordChanged',
  twoFactorReset: 'apiTokens.reason.twoFactorReset',
};

/** スコープの表示文言の鍵。**知らないスコープは `null`**（名前をそのまま出す）。 */
export function scopeLabelKey(scope: string): MessageKey | null {
  return SCOPE_LABELS[scope] ?? null;
}

export function statusLabelKey(status: ApiTokenStatus): MessageKey {
  return STATUS_LABELS[status];
}

/** 失効の理由の表示文言の鍵。**知らない理由・理由なしは `null`。** */
export function revokeReasonLabelKey(reason: string | null | undefined): MessageKey | null {
  return reason === null || reason === undefined ? null : (REVOKE_REASON_LABELS[reason] ?? null);
}

/** いま使えるキーの本数。 */
export function activeTokenCount(tokens: readonly ApiTokenRow[]): number {
  return tokens.filter((token) => token.status === 'active').length;
}

/**
 * 接続元の許可リストの入力を、1 件ずつに分ける。
 * 区切りは**カンマ・空白・改行・セミコロン**。重複は 1 つにする。
 * ⚠️ **CIDR として正しいかは見ない**（サーバが断る。黙って捨てると、制限したつもりで開く）。
 */
export function parseIpAllowListInput(input: string): string[] {
  const entries = input
    .split(/[\s,;]+/)
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '');
  return entries.filter((entry, index) => entries.indexOf(entry) === index);
}

/** 期限の入力が、サーバの範囲（1 日以上、上限以下の整数）か。無期限は作れない。 */
export function isExpiryInRange(days: number, maxDays: number): boolean {
  return Number.isInteger(days) && days >= 1 && days <= maxDays;
}

/** 期限までの残り日数（切り上げ）。**過ぎていれば 0。** */
export function daysRemaining(expiresAt: string, now: Date): number {
  const remaining = new Date(expiresAt).getTime() - now.getTime();
  return remaining <= 0 ? 0 : Math.ceil(remaining / 86_400_000);
}

/**
 * パスワード変更・2 要素の解除の確認に、「API キーも失効させる」を出すか。
 *
 * - 自分の操作: **いま使えるキーがあるときだけ**（無ければ出しても意味が無い）
 * - 他人の 2 要素の解除: **機能が有効なら常に**（相手のキーの有無は一覧に無い）
 *
 * 初期値は常にオン（サーバも、指定が無ければ失効させる）。
 */
export function shouldOfferTokenRevoke(
  enabled: boolean | undefined,
  activeCount: number | null,
): boolean {
  if (enabled !== true) {
    return false;
  }

  return activeCount === null || activeCount > 0;
}

// ---- SAML の強制再認証（Issue #1034）-----------------------------------------------

/** 再認証へ出る間、入力の途中の値を置く `sessionStorage` のキー。 */
export const API_TOKEN_DRAFT_KEY = 'q.admin.apiTokenDraft';

/** 入力の途中の値を置いておける長さ（IdP での再認証に掛かる分）。 */
export const API_TOKEN_DRAFT_LIFETIME_MS = 15 * 60 * 1000;

/**
 * 再認証へ出る間だけ、このタブへ置く入力の途中の値。
 *
 * ⚠️ **秘密は含めない。** 名前・スコープ・期限・接続元の許可リストだけ。
 * 平文のトークンは発行の応答の画面変数にしか無く、パスワードも置かない。
 */
export interface ApiTokenDraft {
  name: string;
  scopes: string[];
  expiresInDays: number;
  ipText: string;
}

/** 戻りの印（サーバが固定の飛び先に付ける `?apiTokenReauth=done|failed`）。 */
export type ReauthMarker = 'done' | 'failed';

/** `sessionStorage` を使えない環境（プライベートウィンドウ・無効化）では `null`。 */
function draftStorage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

/** 入力の途中の値を置く。**置けなくても失敗にしない**（戻ったあと入力し直すだけ）。 */
export function saveApiTokenDraft(
  draft: ApiTokenDraft,
  now: number = Date.now(),
  storage: Pick<Storage, 'setItem'> | null = draftStorage(),
): void {
  try {
    storage?.setItem(API_TOKEN_DRAFT_KEY, JSON.stringify({ draft, savedAt: now }));
  } catch {
    // 容量超過など。置けないだけ
  }
}

/** 置いた値を消す。 */
export function clearApiTokenDraft(
  storage: Pick<Storage, 'removeItem'> | null = draftStorage(),
): void {
  try {
    storage?.removeItem(API_TOKEN_DRAFT_KEY);
  } catch {
    // 何もしない
  }
}

/**
 * 置いた値を読む。**読んだら消す**（1 回だけ使う）。
 * 形が違う・古い（15 分より前）・無いものは `null`。
 */
export function takeApiTokenDraft(
  now: number = Date.now(),
  storage: Pick<Storage, 'getItem' | 'removeItem'> | null = draftStorage(),
): ApiTokenDraft | null {
  let raw: string | null = null;
  try {
    raw = storage?.getItem(API_TOKEN_DRAFT_KEY) ?? null;
  } catch {
    return null;
  } finally {
    clearApiTokenDraft(storage);
  }

  if (raw === null) {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) {
      return null;
    }

    const { draft, savedAt } = parsed as { draft?: Partial<ApiTokenDraft>; savedAt?: unknown };
    if (
      typeof savedAt !== 'number' ||
      now - savedAt > API_TOKEN_DRAFT_LIFETIME_MS ||
      now < savedAt - 60_000
    ) {
      return null;
    }

    if (
      typeof draft?.name !== 'string' ||
      !Array.isArray(draft.scopes) ||
      !draft.scopes.every((scope) => typeof scope === 'string') ||
      typeof draft.expiresInDays !== 'number' ||
      typeof draft.ipText !== 'string'
    ) {
      return null;
    }

    return {
      name: draft.name,
      scopes: draft.scopes,
      expiresInDays: draft.expiresInDays,
      ipText: draft.ipText,
    };
  } catch {
    return null;
  }
}

/**
 * 戻りの印を読み、**URL から消す**（再読み込み・ブックマークで再び動かないように）。
 * 印が無い・知らない値は `null`。
 */
export function takeReauthMarker(
  location: Pick<Location, 'pathname' | 'search' | 'hash'>,
  replaceUrl: (url: string) => void,
): ReauthMarker | null {
  const params = new URLSearchParams(location.search);
  const value = params.get('apiTokenReauth');
  if (value === null) {
    return null;
  }

  params.delete('apiTokenReauth');
  const rest = params.toString();
  replaceUrl(`${location.pathname}${rest === '' ? '' : `?${rest}`}${location.hash}`);

  return value === 'done' || value === 'failed' ? value : null;
}
