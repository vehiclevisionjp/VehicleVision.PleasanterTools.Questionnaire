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
