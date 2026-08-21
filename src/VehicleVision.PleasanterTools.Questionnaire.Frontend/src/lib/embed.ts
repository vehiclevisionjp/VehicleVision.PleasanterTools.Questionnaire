import type { EmbedSource, Question } from './types';

/**
 * 埋め込みの既定の縦横比（16:9）。**`.Core` の `EmbedSource.DefaultAspectRatio` と揃える。**
 */
export const DEFAULT_EMBED_ASPECT_RATIO = 16 / 9;

/** 受け付ける縦横比の下限。 */
export const MINIMUM_EMBED_ASPECT_RATIO = 0.2;

/** 受け付ける縦横比の上限。 */
export const MAXIMUM_EMBED_ASPECT_RATIO = 5;

/**
 * 画面へ出してよい埋め込み先か。
 *
 * **サーバが保存の時点で配信元を絞っているが、ここでももう一度確かめる。**
 * 設定は後から狭められるので、**保存した時点で許されていた URL が
 * いま許されているとは限らない。** 加えて、公開済みの定義は版として固まっており、
 * **後から書き換わらない。**
 *
 * **ここで通しても CSP が通すとは限らない**（CSP は運用側の設定だけが決める）。
 * この関数は「明らかに出してはいけないもの」を落とすためのもの。
 */
export function isSafeEmbedUrl(url: string | undefined | null): boolean {
  if (!url) {
    return false;
  }

  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }

  // **`https:` 以外は出さない。** 利用者情報（`user:pass@`）付きも紛らわしいので落とす
  return parsed.protocol === 'https:' && parsed.username === '' && parsed.password === '';
}

/** 画面へ出せる埋め込み。**出せないときは `undefined`。** */
export function embedSource(question: Question): EmbedSource | undefined {
  if (question.type !== 'Embed') {
    return undefined;
  }

  const embed = question.settings.embed;
  if (!embed || !isSafeEmbedUrl(embed.url)) {
    return undefined;
  }

  return embed;
}

/** 収まる範囲へ落とした縦横比。 */
export function embedAspectRatio(embed: EmbedSource): number {
  const ratio = embed.aspectRatio;
  if (typeof ratio !== 'number' || !Number.isFinite(ratio)) {
    return DEFAULT_EMBED_ASPECT_RATIO;
  }

  return Math.min(Math.max(ratio, MINIMUM_EMBED_ASPECT_RATIO), MAXIMUM_EMBED_ASPECT_RATIO);
}

/**
 * このページに、第三者へ通信が出る埋め込みがあるか。
 *
 * **回答者にその旨を知らせるため**（Issue #107）。
 * 完全匿名を掲げている以上、**IP や User-Agent が別の相手へ渡ることは黙って行わない。**
 * **`iframe` だけでなく画像も対象。** 画像でも取りに行った時点で相手に届く。
 */
export function hasThirdPartyEmbed(questions: readonly Question[]): boolean {
  return questions.some((question) => embedSource(question) !== undefined);
}

/** 埋め込み先のホスト。**回答者へ「どこへ繋がるか」を見せるのに使う。** */
export function embedHost(embed: EmbedSource): string {
  try {
    return new URL(embed.url).host;
  } catch {
    return '';
  }
}
