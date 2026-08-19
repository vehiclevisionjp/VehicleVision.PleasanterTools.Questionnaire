/**
 * 画面に出す言語の決め方と、言語に合わせた書式。
 *
 * **回答画面と管理画面の両方から使う。**
 * `_documents/多言語対応方針.md` の決め事をここに 1 つだけ置く。
 */

/** 本アプリが画面に出せる言語。 */
export type Language = 'ja' | 'en';

/** 選べる言語。**先頭が既定。** */
export const SUPPORTED_LANGUAGES: readonly Language[] = ['ja', 'en'];

/**
 * 未翻訳・未指定のときに使う言語。
 *
 * **`LocalizedText.DefaultLanguage`（サーバ側）と同じ値でなければならない。**
 * 食い違うと、サーバが `ja` へ落とした文字列を画面が別の言語として扱う。
 */
export const DEFAULT_LANGUAGE: Language = 'ja';

/** 画面に出す言語の名前。**その言語自身で書く**（読めない名前で選ばせない）。 */
export const LANGUAGE_NAMES: Record<Language, string> = {
  ja: '日本語',
  en: 'English',
};

/** `Intl` へ渡すロケール。 */
const LOCALES: Record<Language, string> = {
  ja: 'ja-JP',
  en: 'en-US',
};

/**
 * 言語タグを本アプリの言語コードへ寄せる。対応していなければ `null`。
 *
 * **地域まで見ない。** `en-US` も `en-GB` も `en` として扱う。
 */
export function normalizeLanguage(languageTag: string | null | undefined): Language | null {
  if (!languageTag) return null;

  const primary = languageTag.trim().split(/[-_]/)[0]?.toLowerCase() ?? '';
  return SUPPORTED_LANGUAGES.find((language) => language === primary) ?? null;
}

/**
 * 使う言語を決める。
 *
 * **明示の指定 → ブラウザの言語設定 → 既定**の順
 * （`_documents/多言語対応方針.md` 2 章）。
 * 明示が対応外の値なら、無かったものとして次を見る。
 *
 * @param requested URL や利用者の設定で明示された言語
 * @param browserLanguages ブラウザの言語設定（`navigator.languages` を想定）
 */
export function negotiateLanguage(
  requested: string | null | undefined,
  browserLanguages: readonly string[] = [],
): Language {
  const explicit = normalizeLanguage(requested);
  if (explicit) return explicit;

  for (const candidate of browserLanguages) {
    const language = normalizeLanguage(candidate);
    if (language) return language;
  }

  return DEFAULT_LANGUAGE;
}

/**
 * ブラウザが申告している言語。
 *
 * **`navigator.languages` は `Accept-Language` と同じ出どころ。**
 * サーバへ言語を送らずに済むので、回答画面はこちらを見る。
 */
export function browserLanguages(): readonly string[] {
  if (typeof navigator === 'undefined') return [];
  return navigator.languages ?? (navigator.language ? [navigator.language] : []);
}

/**
 * 文書の言語を宣言する。
 *
 * **読み上げの声と、行折り返しの規則がこれで変わる。**
 * `<html lang>` を日本語のまま英語を出すと、英語が日本語として読み上げられる。
 */
export function applyDocumentLanguage(language: Language): void {
  if (typeof document !== 'undefined') {
    document.documentElement.lang = language;
  }
}

/** 数値を言語に合わせて書く。 */
export function formatNumber(value: number, language: Language): string {
  return new Intl.NumberFormat(LOCALES[language]).format(value);
}

/**
 * 日時を言語に合わせて書く。
 *
 * **端末のタイムゾーンで出す**（`_documents/画面設計.md` 3 章）。
 * ただし**保存値の解釈には使わない。**
 */
export function formatDateTime(value: Date, language: Language): string {
  return new Intl.DateTimeFormat(LOCALES[language], {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(value);
}

/** 経過時間を「〜前」の形で書くときの単位。**大きい方から見る。** */
const ELAPSED_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['day', 86_400_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
];

/**
 * ある時刻からどれだけ経ったかを言語に合わせて書く。
 *
 * **文言を自前で持たない**（`Intl.RelativeTimeFormat`）。
 * 「3 日前」「3 days ago」の言い回しや単複は言語ごとに違い、
 * 鍵で持つと言語を足すたびに書き分けが増える。
 *
 * **一番大きい単位だけで書く。** 滞留に気付くのが目的なので、
 * 「2 日 3 時間 12 分」の精度は要らない。
 */
export function formatElapsed(from: Date, language: Language, now: Date = new Date()): string {
  const elapsed = now.getTime() - from.getTime();
  const format = new Intl.RelativeTimeFormat(LOCALES[language], { numeric: 'auto' });

  for (const [unit, span] of ELAPSED_UNITS) {
    if (Math.abs(elapsed) >= span) {
      return format.format(-Math.floor(elapsed / span), unit);
    }
  }

  // **1 分未満は「0 分前」ではなく「今」と読ませる**
  return format.format(0, 'minute');
}

/**
 * 差し込みのある文言を組み立てる。
 *
 * `{name}` の形で書いた所を置き換える。**数値は言語に合わせて整形する。**
 */
export function interpolate(
  template: string,
  parameters: Record<string, string | number> | undefined,
  language: Language,
): string {
  if (!parameters) return template;

  return template.replace(/\{(\w+)\}/g, (match, name: string) => {
    const value = parameters[name];
    if (value === undefined) return match;
    return typeof value === 'number' ? formatNumber(value, language) : value;
  });
}
