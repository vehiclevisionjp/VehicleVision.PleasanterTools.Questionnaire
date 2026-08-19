/**
 * 回答画面の見た目（Issue #56）。
 *
 * **サーバの `Core/Definitions/SurveyTheme.cs` と対になる。**
 * 綴りを変えると JSON が読めなくなる。
 *
 * **管理画面もここを使う。** 検査と書体の並びを 2 か所に持つと、
 * 片方だけ緩んでも気付けない。
 */

/** 書体。**値はサーバの列挙そのもの。** */
export type ThemeFont = 'System' | 'Sans' | 'Serif' | 'Rounded' | 'Monospace';

/** 画面に出す並び。**既定（端末任せ）が先頭。** */
export const THEME_FONTS: ThemeFont[] = ['System', 'Sans', 'Serif', 'Rounded', 'Monospace'];

/**
 * テーマ。
 *
 * **値が無い項目はサーバが落として返す**ので、どれも省略可で書く。
 * **すべて省略なら既定の見た目**（今までと同じ）。
 */
export interface SurveyTheme {
  accentColor?: string | null;
  backgroundColor?: string | null;
  textColor?: string | null;
  font?: ThemeFont;
  /** ヘッダ画像の識別子。**中身ではなく識別子だけを持つ。** */
  headerImageId?: string | null;
}

/**
 * 既定の色。**管理画面の色欄の初期値に使う。**
 *
 * ⚠️ **`App.svelte` の `:global(:root)` に書いてある値と揃えること。**
 * CSS の変数を TypeScript から読めないので、ここだけは写しになる。
 * 食い違うと、色欄が「今の見た目と違う色」を初期値として出す。
 */
export const DEFAULT_COLORS = {
  accentColor: '#175cd3',
  backgroundColor: '#f9fafb',
  textColor: '#101828',
} as const;

/**
 * 受け付ける色の形。**`#rgb` と `#rrggbb` だけ。**
 *
 * ⚠️ **利用者が入れた文字列をそのまま CSS へ流し込まないこと。**
 * `red; } body { display:none } /*` のような値を混ぜられると、
 * スタイル表そのものを書き換えられる。
 * **受け付ける形をここまで絞れば、検査は 16 進の桁数を見るだけで済む。**
 */
const COLOR_PATTERN = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i;

/** CSS へ渡してよい色なら返す。**駄目なら `null`（＝既定の色を使う）。** */
export function safeColor(value: string | null | undefined): string | null {
  return typeof value === 'string' && COLOR_PATTERN.test(value) ? value : null;
}

/**
 * 書体の並び。
 *
 * **利用者が書いた文字列は 1 文字も入らない。** 選ばれた列挙で引くだけ。
 *
 * **外部から Web フォントを読み込まない**（`_documents/非機能設計.md` 1 章）。
 * 回答画面は完全匿名なので、回答者の端末から第三者へ要求を出させない。
 * 並べてあるのは端末が持っている書体だけで、無ければ後ろへ落ちる。
 */
const FONT_STACKS: Record<ThemeFont, string | null> = {
  // **既定は指定しない。** `:root` に書いてある今までの指定がそのまま効く
  System: null,
  Sans: '"Hiragino Sans", "Yu Gothic UI", "Noto Sans JP", "Meiryo", system-ui, sans-serif',
  Serif: '"Hiragino Mincho ProN", "Yu Mincho", "Noto Serif JP", "MS PMincho", serif',
  Rounded:
    '"Hiragino Maru Gothic ProN", "M PLUS Rounded 1c", "Quicksand", "Hiragino Sans", sans-serif',
  Monospace: 'ui-monospace, SFMono-Regular, Consolas, "Noto Sans Mono", monospace',
};

/**
 * テーマを CSS のカスタムプロパティへ写す。
 *
 * ⚠️ **スタイル表を組み立てて差し込まないこと。** `<style>` の中身を文字列で作ると、
 * `</style>` を混ぜられて画面を乗っ取られる。
 * **ここは `style.setProperty` しか使わない**（CSSOM への代入なので、
 * 値が CSS として妥当でなければブラウザが黙って捨てるだけになる）。
 * **`Content-Security-Policy` で inline style を許していない**のも同じ理由で、
 * 属性や `<style>` 経由では動かない。
 *
 * **指定の無い項目はプロパティごと消す。** 空文字を入れると
 * 「指定はあるが空」になり、`:root` に書いた既定値へ落ちない。
 *
 * @param target 写す先。**回答画面は `:root`、プレビューは差し込む枠。**
 */
export function applyTheme(
  target: HTMLElement | null | undefined,
  theme: SurveyTheme | null | undefined,
): void {
  if (!target) return;

  const accent = safeColor(theme?.accentColor);
  const background = safeColor(theme?.backgroundColor);
  const text = safeColor(theme?.textColor);
  const font = theme?.font ? FONT_STACKS[theme.font] : null;

  set('--accent', accent);
  set('--bg', background);
  set('--text', text);
  set('--font', font ?? null);

  // **地の色か文字色を変えたときだけ、薄い色と罫線を引き直す。**
  // 既定のまま（薄い灰色）だと、暗い地の上で補足文が読めなくなる。
  // **既定の見た目は変えない**ので、何も指定していないアンケートには触らない。
  // `color-mix` に入るのは決め打ちの数値と変数だけで、利用者の文字列は入らない
  const derive = background !== null || text !== null;
  set('--muted', derive ? 'color-mix(in srgb, var(--text) 62%, var(--bg))' : null);
  set('--border', derive ? 'color-mix(in srgb, var(--text) 28%, var(--bg))' : null);

  function set(name: string, value: string | null) {
    if (value === null) {
      target!.style.removeProperty(name);
    } else {
      target!.style.setProperty(name, value);
    }
  }
}

/**
 * ヘッダ画像の URL。**無ければ `null`。**
 *
 * **本アプリの口だけを指す。** テーマに外部の URL を持たせていないので、
 * 回答者のブラウザが第三者へ要求を出す経路は無い。
 *
 * **識別子を `?v=` に付ける。** 差し替えると識別子が変わるので、
 * 長く持たせた応答が古い画像を返し続けることがない。
 */
export function headerImageUrl(publicId: string, theme: SurveyTheme | null | undefined): string | null {
  const id = theme?.headerImageId;
  if (!id) return null;
  return `/api/forms/${encodeURIComponent(publicId)}/header-image?v=${encodeURIComponent(id)}`;
}
