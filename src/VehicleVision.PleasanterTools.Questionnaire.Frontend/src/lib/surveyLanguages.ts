import { SUPPORTED_LANGUAGES, type Language } from './i18n/language';

/**
 * アンケートが文言を持っている言語（Issue #592）。**言語の切り替えを出すかどうかの判断に使う。**
 *
 * 定義の中の `LocalizedText`（言語コード → 文言）を総ざらいし、
 * 空でない文言が 1 つでもある言語を拾う。**項目を個別に数えない**ので、
 * 後から多言語の項目が増えても数え漏れない。
 *
 * - `LocalizedText` は「キーが全部 {@link SUPPORTED_LANGUAGES} で、値が文字列」のオブジェクトとして見分ける。
 *   完了画面の `confirmationBlocks` は値が配列なので対象外（文言の `confirmationMessage` に付いてくる）
 * - 並びは {@link SUPPORTED_LANGUAGES} の順
 */
export function availableLanguages(definition: unknown): Language[] {
  const found = new Set<Language>();
  collect(definition, found);
  return SUPPORTED_LANGUAGES.filter((language) => found.has(language));
}

function collect(value: unknown, found: Set<Language>): void {
  if (Array.isArray(value)) {
    for (const item of value) {
      collect(item, found);
    }
    return;
  }

  if (value === null || typeof value !== 'object') {
    return;
  }

  const entries = Object.entries(value);
  if (
    entries.length > 0 &&
    entries.every(
      ([key, item]) =>
        typeof item === 'string' && (SUPPORTED_LANGUAGES as readonly string[]).includes(key),
    )
  ) {
    for (const [key, item] of entries) {
      if ((item as string).trim() !== '') {
        found.add(key as Language);
      }
    }
    return;
  }

  for (const [, item] of entries) {
    collect(item, found);
  }
}
