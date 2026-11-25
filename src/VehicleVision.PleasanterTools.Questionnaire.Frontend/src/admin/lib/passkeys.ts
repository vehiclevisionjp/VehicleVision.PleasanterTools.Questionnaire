/**
 * パスキーの画面の判断（Issue #1068。Core の仕様書 3.10）。
 *
 * **`.svelte` に判断を置かず、ここへ置く**（単体試験は `lib/` の純粋な関数だけを見る決まり）。
 * 認可・再認証の判定・「最後のログイン方法か」の判断はサーバ側の責務で、ここは表示と入力の整形だけ。
 */
import type { MessageKey } from './i18n/messages';
import type { PasskeyFailure } from './webauthn';
import type { PasskeyRow } from './types';

/** 名前の最大の長さ（`Core_AdminPasskeys.Name` は 100 文字。仕様書 3.10.2）。 */
export const PASSKEY_NAME_MAX_LENGTH = 100;

/** 入力された名前を整える（前後の空白を落とす）。**空・長すぎるときは `null`。** */
export function normalizePasskeyName(input: string): string | null {
  const name = input.trim();
  if (name === '' || [...name].length > PASSKEY_NAME_MAX_LENGTH) {
    return null;
  }
  return name;
}

/** 一覧に出す日付。**不正な値は空**（生の値を出さない）。 */
export function formatPasskeyDate(value: string | null | undefined, locale?: string): string {
  if (!value) {
    return '';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  return date.toLocaleDateString(locale);
}

/** 削除の対象が、いま登録されている最後のパスキーか（確認ダイアログの説明を変える）。 */
export function isLastPasskey(rows: readonly PasskeyRow[], id: string): boolean {
  return rows.length === 1 && rows[0]?.id === id;
}

/** サーバが「直近の再認証が要る」と返したか（仕様書 3.10.5 の P4。**401**）。 */
export function needsReauthentication(failure: PasskeyFailure): boolean {
  return failure.kind === 'server' && failure.status === 401;
}

/**
 * 失敗を、画面へ出す文言の鍵にする。
 *
 * **ログインの失敗は、サーバの理由を出さず決まった文言にする**
 * （仕様書 3.10.4。理由を区別しない。外へ漏らさない）。
 * 登録・管理の失敗は、サーバが言語を合わせて返した文言があればそれを使う（`serverMessage`）。
 */
export function failureMessage(
  failure: PasskeyFailure,
  purpose: 'signIn' | 'manage',
): { key: MessageKey } | { serverMessage: string } {
  switch (failure.kind) {
    case 'unsupported':
      return { key: 'passkey.error.unsupported' };
    case 'insecureContext':
      return { key: 'passkey.error.insecureContext' };
    case 'cancelled':
      return { key: 'passkey.error.cancelled' };
    case 'noAuthenticator':
      return { key: 'passkey.error.noAuthenticator' };
    case 'alreadyRegistered':
      return { key: 'passkey.error.alreadyRegistered' };
    case 'insecure':
      return { key: 'passkey.error.insecure' };
    case 'invalidOptions':
      return { key: 'passkey.error.invalidOptions' };
    case 'noResponse':
    case 'failed':
      return { key: 'passkey.error.failed' };
    case 'server':
      if (purpose === 'signIn') {
        return { key: 'passkey.error.signInFailed' };
      }
      return failure.message === ''
        ? { key: 'passkey.error.failed' }
        : { serverMessage: failure.message };
  }
}
