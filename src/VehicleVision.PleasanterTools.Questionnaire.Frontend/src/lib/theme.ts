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
 * 名前の付いた配色（Issue #109）。**値はサーバの列挙そのもの。**
 *
 * **色を増やすほど、読めない配色を作れる幅が広がる。**
 * 組み合わせに名前を付けて選ばせるのが、その歯止めになる。
 */
export type ThemePreset = 'None' | 'Indigo' | 'Forest' | 'Crimson' | 'Sand' | 'Midnight';

/** 画面に出す並び。**指定なしが先頭。** */
export const THEME_PRESETS: ThemePreset[] = [
  'None',
  'Indigo',
  'Forest',
  'Crimson',
  'Sand',
  'Midnight',
];

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
  /** 釦の文字色（Issue #109）。 */
  accentTextColor?: string | null;
  /** 設問枠の地の色（Issue #109）。 */
  surfaceColor?: string | null;
  /** 枠線の色（Issue #109）。 */
  borderColor?: string | null;
  /** 名前の付いた配色（Issue #109）。**個別の指定が優先される。** */
  preset?: ThemePreset;
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
  accentTextColor: '#ffffff',
  surfaceColor: '#ffffff',
  borderColor: '#d0d5dd',
} as const;

/**
 * 名前の付いた配色の中身（Issue #109）。
 *
 * **サーバは「どれを選んだか」しか持たない。** 実際の色をここだけに置くのは、
 * **既定値との写しが 2 か所になると、片方だけ直す事故が起きる**ため。
 *
 * **どれも読める組み合わせであることを確かめてある**（背景と文字のコントラスト比は
 * いずれも 4.5:1 以上。WCAG 2.1 AA）。
 */
export const THEME_PRESET_COLORS: Record<ThemePreset, Partial<SurveyTheme>> = {
  // **指定なしは何も返さない。** 既定の見た目のまま
  None: {},
  Indigo: {
    accentColor: '#4338ca',
    backgroundColor: '#f8fafc',
    textColor: '#0f172a',
    accentTextColor: '#ffffff',
    surfaceColor: '#ffffff',
  },
  Forest: {
    accentColor: '#15803d',
    backgroundColor: '#f6faf6',
    textColor: '#14261a',
    accentTextColor: '#ffffff',
    surfaceColor: '#ffffff',
  },
  Crimson: {
    accentColor: '#b91c1c',
    backgroundColor: '#fdf8f7',
    textColor: '#2b1414',
    accentTextColor: '#ffffff',
    surfaceColor: '#ffffff',
  },
  Sand: {
    accentColor: '#92400e',
    backgroundColor: '#faf6ef',
    textColor: '#28211a',
    accentTextColor: '#ffffff',
    surfaceColor: '#fffdf8',
  },
  Midnight: {
    accentColor: '#60a5fa',
    backgroundColor: '#0f172a',
    textColor: '#e8eefc',
    // **明るいアクセントの上は暗い文字。** 白のままだと読めない
    accentTextColor: '#0b1220',
    surfaceColor: '#182034',
  },
};

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
 * ただし **4 書体すべてをアプリに同梱している**（Issue #152）。
 * 自前配信なので第三者へ要求は出ず、**インターネットへ出られないイントラでも同じ見た目**になる。
 *
 * **後ろに端末の書体も並べたままにする。** 配信に失敗しても文字が出なくならないため。
 */
const FONT_STACKS: Record<ThemeFont, string | null> = {
  // **既定は指定しない。** `:root` に書いてある今までの指定がそのまま効く
  System: null,
  // **同梱した可変フォントを先頭に置く**（無い環境でも端末の書体へ落ちる）
  Sans: '"Noto Sans JP Variable", "Hiragino Sans", "Yu Gothic UI", "Meiryo", system-ui, sans-serif',
  Serif: '"Noto Serif JP Variable", "Hiragino Mincho ProN", "Yu Mincho", "MS PMincho", serif',
  Rounded:
    '"M PLUS Rounded 1c", "Hiragino Maru Gothic ProN", "Quicksand", "Hiragino Sans", sans-serif',
  // **日本語も等幅**にしたいので M PLUS 1 Code を同梱している
  Monospace: '"M PLUS 1 Code Variable", ui-monospace, SFMono-Regular, Consolas, monospace',
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

  const resolved = resolveTheme(theme);

  const accent = safeColor(resolved.accentColor);
  const background = safeColor(resolved.backgroundColor);
  const text = safeColor(resolved.textColor);
  const accentText = safeColor(resolved.accentTextColor);
  const surface = safeColor(resolved.surfaceColor);
  const border = safeColor(resolved.borderColor);
  const font = resolved.font ? FONT_STACKS[resolved.font] : null;

  set('--accent', accent);
  set('--bg', background);
  set('--text', text);
  set('--accent-text', accentText);
  set('--surface', surface);
  set('--font', font ?? null);

  // **地の色か文字色を変えたときだけ、薄い色と罫線を引き直す。**
  // 既定のまま（薄い灰色）だと、暗い地の上で補足文が読めなくなる。
  // **既定の見た目は変えない**ので、何も指定していないアンケートには触らない。
  // `color-mix` に入るのは決め打ちの数値と変数だけで、利用者の文字列は入らない
  const derive = background !== null || text !== null;
  set('--muted', derive ? 'color-mix(in srgb, var(--text) 62%, var(--bg))' : null);
  // **枠線は指定があればそちらを使う**（Issue #109）。無ければ今までどおり導く
  set(
    '--border',
    border ?? (derive ? 'color-mix(in srgb, var(--text) 28%, var(--bg))' : null),
  );

  function set(name: string, value: string | null) {
    if (value === null) {
      target!.style.removeProperty(name);
    } else {
      target!.style.setProperty(name, value);
    }
  }
}

/**
 * 配色を当ててから個別の指定で上書きしたテーマ（Issue #109）。
 *
 * **個別の指定が勝つ。** 「配色を選んだうえで 1 色だけ変える」が普通の使い方なので、
 * 逆にすると選び直すたびに個別の指定が消える。
 *
 * **管理画面の下見もこれを使う。** 解き方が 2 か所にあると、
 * 下見と本物で色が食い違う。
 */
export function resolveTheme(theme: SurveyTheme | null | undefined): SurveyTheme {
  if (!theme) return {};

  const preset = theme.preset && theme.preset !== 'None' ? THEME_PRESET_COLORS[theme.preset] : null;
  if (!preset) return theme;

  return {
    ...preset,
    // **`undefined` と `null` は「指定なし」。** 空で上書きして配色を消さない
    ...Object.fromEntries(
      Object.entries(theme).filter(([, value]) => value !== null && value !== undefined),
    ),
  };
}

/**
 * 色の相対輝度（WCAG 2.1）。**`#rgb` と `#rrggbb` だけを受ける。**
 *
 * 出典: <https://www.w3.org/TR/WCAG21/#dfn-relative-luminance>（2026-08-21 参照）
 */
function luminance(color: string): number | null {
  const hex = safeColor(color);
  if (hex === null) return null;

  const body = hex.slice(1);
  const full =
    body.length === 3
      ? body
          .split('')
          .map((c) => c + c)
          .join('')
      : body;

  const channels = [0, 2, 4].map((i) => {
    const v = Number.parseInt(full.slice(i, i + 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];

  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

/**
 * 2 色のコントラスト比（Issue #109）。**読めない配色を管理画面で警告するために使う。**
 *
 * **保存は止めない。** 社内利用など、事情があることもある。
 * **どちらかの色が読めなければ `null`**（比べようがない）。
 *
 * 出典: <https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio>（2026-08-21 参照）
 */
export function contrastRatio(
  foreground: string | null | undefined,
  background: string | null | undefined,
): number | null {
  const a = foreground ? luminance(foreground) : null;
  const b = background ? luminance(background) : null;
  if (a === null || b === null) return null;

  const [light, dark] = a >= b ? [a, b] : [b, a];
  return (light + 0.05) / (dark + 0.05);
}

/** WCAG 2.1 AA（本文）を満たすか。**満たさなければ警告を出す。** */
export const CONTRAST_AA = 4.5;

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
