import {
  applyDocumentLanguage,
  browserLanguages,
  DEFAULT_LANGUAGE,
  formatDateTime as formatDateTimeIn,
  formatElapsed as formatElapsedIn,
  negotiateLanguage,
  type Language,
} from '../../../lib/i18n/language';
import { translator, type MessageKey } from './messages';

/**
 * 管理画面が今出している言語。
 *
 * **画面の隅々まで props で配らない。** 管理画面は 1 つのアプリなので、
 * 言語は 1 つで足りる。ここを読む所は自動で描き直る（`$state`）。
 *
 * **決め方は「利用者ごとの設定 → ブラウザの言語設定 → `ja`」**
 * （`_documents/多言語対応方針.md` 2 章）。
 */
let current = $state<Language>(DEFAULT_LANGUAGE);

/** 今の言語。 */
export function language(): Language {
  return current;
}

/**
 * 言語を決める。
 *
 * @param preferred 利用者ごとの設定（`AdminUsers.Language`）。未設定なら `null`
 */
export function resolveLanguage(preferred: string | null | undefined): void {
  const next = negotiateLanguage(preferred, browserLanguages());
  if (next !== current) {
    current = next;
  }

  // **読み上げの声と行折り返しが変わる。** `<html lang>` を合わせておく
  applyDocumentLanguage(current);
}

/** 文言を引く。**`$state` を読むので、呼んだ所が言語の切り替えで描き直る。** */
export function t(key: MessageKey, parameters?: Record<string, string | number>): string {
  return translator(current)(key, parameters);
}

/** 日時を今の言語で書く。 */
export function formatDateTime(value: Date): string {
  return formatDateTimeIn(value, current);
}

/** その時刻からどれだけ経ったかを今の言語で書く。 */
export function formatElapsed(value: Date): string {
  return formatElapsedIn(value, current);
}

/**
 * サーバへ「この言語で返してほしい」と伝えるヘッダ。
 *
 * **画面が実際に描いている言語をそのまま渡す。**
 * サーバは利用者ごとの設定を見ないので、これが唯一の手掛かりになる
 * （`_documents/多言語対応方針.md` 2 章）。
 */
export function acceptLanguageHeader(): string {
  return current === DEFAULT_LANGUAGE
    ? `${current}, en;q=0.8`
    : `${current}, ${DEFAULT_LANGUAGE};q=0.8`;
}
