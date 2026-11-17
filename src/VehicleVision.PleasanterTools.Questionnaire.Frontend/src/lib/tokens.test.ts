import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { READABILITY_COLORS } from './readability';
import { CONTRAST_AA, DEFAULT_COLORS, contrastRatio } from './theme';

/**
 * `styles/tokens.css`（DesignReference の明示型）を読み、TypeScript 側の写しと突き合わせる（Issue #776）。
 * 値の出どころは 1 か所（tokens.css）。ここが食い違えば、DesignReference と画面の色がずれている。
 */
const css = readFileSync(new URL('../styles/tokens.css', import.meta.url), 'utf-8');

function tokens(block: string): Record<string, string> {
  const start = css.indexOf(block);
  const body = css.slice(start, css.indexOf('}', start));
  return Object.fromEntries(
    [...body.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-f]{6});/g)].map((m) => [m[1]!, m[2]!]),
  );
}

const light = tokens(':root,');
const dark = tokens('[data-theme="dark"] {');

describe('既定の色は DesignReference のライトと同じ', () => {
  it('DEFAULT_COLORS', () => {
    expect(DEFAULT_COLORS).toEqual({
      accentColor: light['color-primary'],
      backgroundColor: light['color-bg'],
      textColor: light['color-text'],
      accentTextColor: light['color-on-fill'],
      surfaceColor: light['color-surface'],
      borderColor: light['color-border'],
    });
  });
});

describe('読みやすさ設定のダークは DesignReference のダークと同じ', () => {
  it('値が一致する', () => {
    const c = READABILITY_COLORS.dark;
    expect(c).toMatchObject({
      accent: dark['color-primary-text'],
      accentText: dark['color-primary'],
      background: dark['color-bg'],
      surface: dark['color-surface'],
      text: dark['color-text'],
      muted: dark['color-text-muted'],
      border: dark['color-border'],
      borderStrong: dark['color-border-strong'],
      error: dark['color-danger-text'],
      errorSurface: dark['color-danger-subtle'],
      success: dark['color-success-text'],
      successSurface: dark['color-success-subtle'],
      warning: dark['color-warning-text'],
      warningSurface: dark['color-warning-subtle'],
      infoSurface: dark['color-primary-subtle'],
      infoText: dark['color-text'],
    });
  });
});

/** DesignReference の check-web-contrast.mjs と同じ基準（文字 4.5、枠・図形 3）で、このアプリが使う組み合わせを検査する。 */
const pairs: [string, string, string, number][] = [
  ['本文/地', 'color-text', 'color-bg', CONTRAST_AA],
  ['本文/面', 'color-text', 'color-surface', CONTRAST_AA],
  ['補助の文字/地', 'color-text-muted', 'color-bg', CONTRAST_AA],
  ['補助の文字/面', 'color-text-muted', 'color-surface', CONTRAST_AA],
  ['リンク/面', 'color-primary-text', 'color-surface', CONTRAST_AA],
  ['リンク/地', 'color-primary-text', 'color-bg', CONTRAST_AA],
  ['主要釦の文字', 'color-on-fill', 'color-primary', CONTRAST_AA],
  ['危険釦・バッジの文字', 'color-on-fill', 'color-danger', CONTRAST_AA],
  ['エラー文/面', 'color-danger-text', 'color-surface', CONTRAST_AA],
  ['エラー文/淡い面', 'color-danger-text', 'color-danger-subtle', CONTRAST_AA],
  ['成功/淡い面', 'color-success-text', 'color-success-subtle', CONTRAST_AA],
  ['警告/淡い面', 'color-warning-text', 'color-warning-subtle', CONTRAST_AA],
  ['本文/情報の淡い面', 'color-text', 'color-primary-subtle', CONTRAST_AA],
  ['入力欄の枠/面', 'color-border-strong', 'color-surface', 3],
  ['入力欄の枠/地', 'color-border-strong', 'color-bg', 3],
  ['フォーカスリング/面', 'color-primary-text', 'color-surface', 3],
  ['フォーカスリング/地', 'color-primary-text', 'color-bg', 3],
  ['フォーカスリング/選択行', 'color-primary-text', 'color-primary-subtle', 3],
];

describe.each([
  ['ライト', light],
  ['ダーク', dark],
])('色の組み合わせ（%s）', (_name, scheme) => {
  // ダークの主要釦は面と文字を入れ替える（base.css）。ダークでは 淡い鉄紺の面 + 鉄紺の文字
  const resolved = (key: string) => scheme[key]!;
  it.each(pairs)('%s', (_label, fg, bg, min) => {
    // ダークの釦文字は color-on-fill ではなく color-primary（base.css の --accent-text）。別途検査する
    const isFillPair = fg === 'color-on-fill' && bg === 'color-primary';
    const f = isFillPair && scheme === dark ? resolved('color-primary') : resolved(fg);
    const b = isFillPair && scheme === dark ? resolved('color-primary-text') : resolved(bg);
    expect(contrastRatio(f, b)!).toBeGreaterThanOrEqual(min);
  });
});
