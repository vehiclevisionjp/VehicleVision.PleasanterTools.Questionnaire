/**
 * メンテナンス中で送信できなかったときの案内（Issue #961）。
 *
 * **画面は切り替えず、入力を残したまま、送信ボタンの近くへ出す。**
 *
 * **対応言語（7 言語）の固定文言をすべて並べる。** 画面の `?lang=` とサーバの `Accept-Language` が
 * 食い違っても、読める言語が必ず含まれる。サーバが返す `message`（日本語・英語だけ）は使わない。
 * 現在の画面の言語を先頭にし、残りは `SUPPORTED_LANGUAGES` の順に続ける。
 */

import { LANGUAGE_NAMES, SUPPORTED_LANGUAGES, type Language } from './i18n/language';
import { translator } from './i18n/messages';

/** 案内 1 行ぶん。 */
export interface MaintenanceNotice {
  language: Language;
  /** その言語自身で書いた名前（言語切り替えと同じ表記）。 */
  name: string;
  text: string;
}

/** 7 言語すべての案内。`current` を先頭に置く。 */
export function maintenanceNotices(current: Language): MaintenanceNotice[] {
  const ordered = [current, ...SUPPORTED_LANGUAGES.filter((language) => language !== current)];

  return ordered.map((language) => ({
    language,
    name: LANGUAGE_NAMES[language],
    text: translator(language)('submit.maintenance'),
  }));
}
