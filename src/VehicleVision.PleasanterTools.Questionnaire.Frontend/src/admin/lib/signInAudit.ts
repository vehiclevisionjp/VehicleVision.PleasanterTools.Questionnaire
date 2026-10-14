/**
 * ログイン（SAML・Pleasanter）に関する監査ログの読み方（Issue #602）。
 *
 * **判断はここへ置き、`.svelte` には置かない。** `.svelte` は CodeQL の解析対象外で、
 * 単体テスト（vitest）も `lib/` の純粋な関数だけを見る決まりのため。
 */
import type { MessageKey } from './i18n/messages';

/** 絞り込みの選択肢。**値は操作名への部分一致**（サーバの `action` と同じ）。 */
export const AUDIT_KIND_OPTIONS: readonly { value: string; label: MessageKey }[] = [
  { value: '', label: 'audit.kind.all' },
  { value: '/saml/acs', label: 'audit.kind.saml' },
  { value: '/pleasanter-sso/check', label: 'audit.kind.pleasanterSso' },
  { value: '/sessions', label: 'audit.kind.sessions' },
];

const RESULT_KEYS: Readonly<Record<string, MessageKey>> = {
  'signed-in': 'audit.signIn.result.signed-in',
  'second-factor': 'audit.signIn.result.second-factor',
  enroll: 'audit.signIn.result.enroll',
  'not-allowed': 'audit.signIn.result.not-allowed',
  'unknown-user': 'audit.signIn.result.unknown-user',
  disabled: 'audit.signIn.result.disabled',
  invalid: 'audit.signIn.result.invalid',
  'upstream-error': 'audit.signIn.result.upstream-error',
  'setup-required': 'audit.signIn.result.setup-required',
};

const REASON_KEYS: Readonly<Record<string, MessageKey>> = {
  'relay-missing': 'audit.signIn.reason.relay-missing',
  'idp-refused': 'audit.signIn.reason.idp-refused',
  'response-invalid': 'audit.signIn.reason.response-invalid',
  'in-response-to-mismatch': 'audit.signIn.reason.in-response-to-mismatch',
  'login-id-missing': 'audit.signIn.reason.login-id-missing',
  'membership-not-allowed': 'audit.signIn.reason.membership-not-allowed',
  'membership-not-configured': 'audit.signIn.reason.membership-not-configured',
  'no-cookie': 'audit.signIn.reason.no-cookie',
};

/** 補足から読んだ、ログインの結果と理由。**文言の鍵が無い値は、原文を返す。** */
export interface SignInAuditNote {
  resultKey?: MessageKey;
  result?: string;
  reasonKey?: MessageKey;
  reason?: string;
}

function lookup(
  table: Readonly<Record<string, MessageKey>>,
  value: string,
): MessageKey | undefined {
  // **プロトタイプの名前（constructor など）を鍵として扱わない**
  return Object.hasOwn(table, value) ? table[value] : undefined;
}

/**
 * 監査ログの補足（JSON 文字列）から、ログインの結果と理由を取り出す。
 *
 * **ログインの操作でなければ `null`。** 補足が壊れていても落とさない。
 */
export function readSignInNote(
  action: string,
  detail: string | null | undefined,
): SignInAuditNote | null {
  if (!action.includes('/saml/acs') && !action.includes('/pleasanter-sso/check')) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(detail ?? '');
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }

  const record = parsed as Record<string, unknown>;
  const result = typeof record.result === 'string' ? record.result : '';
  const reason = typeof record.reason === 'string' ? record.reason : '';
  if (result === '' && reason === '') {
    return null;
  }

  const note: SignInAuditNote = {};
  if (result !== '') {
    note.result = result;
    note.resultKey = lookup(RESULT_KEYS, result);
  }

  if (reason !== '') {
    note.reason = reason;
    note.reasonKey = lookup(REASON_KEYS, reason);
  }

  return note;
}

/** 操作名の部分一致から、選択肢の値を選ぶ。**どれにも当たらなければ空（＝すべて）。** */
export function auditKindOf(action: string): string {
  return AUDIT_KIND_OPTIONS.some((option) => option.value === action) ? action : '';
}

/** 端末の一覧に出す、ログイン方式の文言の鍵。**知らない値・無い値は `null`。** */
export function loginMethodKey(method: string | null | undefined): MessageKey | null {
  switch (method) {
    case 'Password':
      return 'sessions.method.Password';
    case 'Saml':
      return 'sessions.method.Saml';
    case 'PleasanterSso':
      return 'sessions.method.PleasanterSso';
    default:
      return null;
  }
}
