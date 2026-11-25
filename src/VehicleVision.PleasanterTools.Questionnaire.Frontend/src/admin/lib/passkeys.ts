/**
 * パスキーの画面の判断（Issue #1068。Core の仕様書 3.10）。
 *
 * **`.svelte` に判断を置かず、ここへ置く**（単体試験は `lib/` の純粋な関数だけを見る決まり）。
 * 認可・再認証の判定・「最後のログイン方法か」の判断はサーバ側の責務で、ここは表示と入力の整形だけ。
 */
import type { MessageKey } from './i18n/messages';
import type { PasskeyFailure } from './webauthn';
import type { PasskeyRow } from './types';

/** 名前の最大の長さ（サーバは前後の空白を落とした **UTF-16 の長さ** で 100 まで。`AdminPasskeyService.CleanName`）。 */
export const PASSKEY_NAME_MAX_LENGTH = 100;

/** 入力された名前を整える（前後の空白を落とす）。**空・長すぎる・制御文字を含むときは `null`。** サーバと同じ規則（二重に守る）。 */
export function normalizePasskeyName(input: string): string | null {
  const name = input.trim();
  if (name === '' || name.length > PASSKEY_NAME_MAX_LENGTH || /\p{Cc}/u.test(name)) {
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

/**
 * 失敗を、画面へ出す文言の鍵にする。
 *
 * **Core のエラーは `{ code }` だけで、文言は画面が作る**（3.10.6）。`code` を文言へ写す。
 * **ログインの失敗は、理由を出さず決まった文言にする**（3.10.4。理由を区別しない）。
 * 例外はレート制限（429。IP ごとの枠で、利用者の列挙にならない）。
 * 未知の `code` は、サーバの `message` があればそれを、無ければ決まった文言を出す。
 */
export function failureMessage(
  failure: PasskeyFailure,
  purpose: 'signIn' | 'manage',
): { key: MessageKey; max?: number } | { serverMessage: string } {
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
      return serverFailureMessage(failure, purpose);
  }
}

function serverFailureMessage(
  failure: Extract<PasskeyFailure, { kind: 'server' }>,
  purpose: 'signIn' | 'manage',
): { key: MessageKey; max?: number } | { serverMessage: string } {
  if (failure.status === 429) {
    return { key: 'passkey.error.rateLimited' };
  }
  if (purpose === 'signIn') {
    return { key: 'passkey.error.signInFailed' };
  }

  switch (failure.code) {
    case 'passkeys.limitReached':
      return { key: 'passkey.error.limitReached' };
    case 'passkeys.invalidName':
      return { key: 'passkey.nameInvalid', max: PASSKEY_NAME_MAX_LENGTH };
    case 'passkeys.invalidResponse':
      return { key: 'passkey.error.invalidResponse' };
    case 'passkeys.originNotAllowed':
      return { key: 'passkey.error.insecure' };
    case 'passkeys.duplicateCredential':
      return { key: 'passkey.error.alreadyRegistered' };
    case 'passkeys.lastSignInMethod':
      return { key: 'passkey.error.lastSignInMethod' };
    case 'passkeys.reauthRequired':
      return { key: 'passkey.error.reauthRequired' };
    case 'users.currentPasswordRejected':
      return { key: 'passkey.error.passwordRejected' };
    case 'users.operationTemporarilyLocked':
      return { key: 'passkey.error.locked' };
    case 'passkeys.notFound':
      return { key: 'passkey.error.notFound' };
    default:
      break;
  }

  if (failure.status === 423) {
    return { key: 'passkey.error.locked' };
  }
  // `message` は、`code` を持たない応答（ネットワークの失敗など）のときの文言。HTTP の定型文は出さない
  return failure.code === '' && failure.status === 0 && failure.message !== ''
    ? { serverMessage: failure.message }
    : { key: 'passkey.error.failed' };
}
