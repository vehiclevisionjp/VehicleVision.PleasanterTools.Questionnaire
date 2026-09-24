import { interpolate, type Language, UI_FALLBACK_LANGUAGE } from '../language';
import { de } from './de';
import { en } from './en';
import { es } from './es';
import { ja } from './ja';
import { ko } from './ko';
import { vi } from './vi';
import { zh } from './zh';

/**
 * 回答画面の文言。
 *
 * **日本語のカタログが鍵の一覧そのもの。**
 * 英語は `Record<MessageKey, string>` として書くので、
 * **足し忘れると型検査（`npm run check` / `npm run build`）で落ちる**
 * （`_documents/多言語対応方針.md` 4 章）。
 */
export { de, en, es, ja, ko, vi, zh };

/** 文言の鍵。**日本語のカタログが一覧そのもの。** */
export type MessageKey = keyof typeof ja;

const CATALOGS: Record<Language, Partial<Record<MessageKey, string>>> = {
  ja,
  en,
  zh,
  de,
  ko,
  es,
  vi,
};

/**
 * サーバが返した検証エラーの符号に対する鍵。
 *
 * **知らない符号でも落とさない。** サーバ側に符号が増えたときは、
 * 当たり障りのない文言へ落として画面を止めない。
 */
export function serverValidationKey(code: string): MessageKey {
  const key = `serverValidation.${code}`;
  return (key in ja ? key : 'serverValidation.unknown') as MessageKey;
}

/** 文言を引く関数。 */
export type Translate = (
  key: MessageKey,
  parameters?: Record<string, string | number>,
) => string;

/**
 * その言語の文言を引く関数を作る。
 *
 * **翻訳が無ければ既定の言語へ落ちる**（`_documents/多言語対応方針.md` 1 章）。
 */
export function translator(language: Language): Translate {
  const catalog = CATALOGS[language];

  return (key, parameters) =>
    interpolate(
      catalog[key] || CATALOGS[UI_FALLBACK_LANGUAGE][key] || ja[key],
      parameters,
      language,
    );
}
