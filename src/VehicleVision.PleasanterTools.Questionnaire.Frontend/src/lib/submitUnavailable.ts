/**
 * サーバ側の都合で送信できなかったときに、回答者へ出す文言を決める（Issue #961）。
 *
 * **画面は切り替えず、入力を残したまま、この文言を送信ボタンの近くへ出す。**
 */

import type { SubmitResult } from './api';
import type { Language } from './i18n/language';

/** サーバがメンテナンスの案内を用意している言語。**日本語と英語だけ**（`MaintenanceMode.MessageOf`）。 */
const SERVER_MESSAGE_LANGUAGES: readonly Language[] = ['ja', 'en'];

/**
 * メンテナンスの案内を選ぶ。
 *
 * **管理者が書いた案内（再開の見込みなど）があれば、それを優先する。**
 * ただしサーバの案内は日本語か英語しかない。**画面がそれ以外の言語なら、
 * 翻訳済みの固定文言を使う**（別の言語の文章を読ませない）。
 */
export function maintenanceNotice(
  serverMessage: string | undefined,
  language: Language,
  fixedMessage: string,
): string {
  const message = serverMessage?.trim() ?? '';
  return message !== '' && SERVER_MESSAGE_LANGUAGES.includes(language) ? message : fixedMessage;
}

/**
 * 送信結果が「サーバ側の都合」なら、出す文言を返す。そうでなければ `null`。
 *
 * @param messages 翻訳済みの固定文言（`submit.maintenance` と `submit.unavailable`）
 */
export function unavailableNotice(
  result: Pick<SubmitResult, 'unavailable' | 'maintenanceMessage'>,
  language: Language,
  messages: { maintenance: string; unavailable: string },
): string | null {
  if (result.unavailable === 'maintenance') {
    return maintenanceNotice(result.maintenanceMessage, language, messages.maintenance);
  }

  return result.unavailable === 'temporary' ? messages.unavailable : null;
}
