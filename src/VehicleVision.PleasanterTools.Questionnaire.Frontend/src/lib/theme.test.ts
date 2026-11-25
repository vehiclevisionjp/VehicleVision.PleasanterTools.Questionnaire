import { describe, expect, it } from 'vitest';
import {
  CONTRAST_AA,
  DEFAULT_COLORS,
  DISABLED_SURFACE_TEXT_PERCENT,
  DISABLED_TEXT_PERCENT,
  THEME_PRESETS,
  THEME_PRESET_COLORS,
  applyTheme,
  contrastRatio,
  mixColors,
  resolveTheme,
  safeColor,
  type SurveyTheme,
} from './theme';

describe('safeColor', () => {
  it('16 進 3 桁と 6 桁だけを通す', () => {
    expect(safeColor('#fff')).toBe('#fff');
    expect(safeColor('#175CD3')).toBe('#175CD3');
  });

  it('CSS へ流し込まれると困る値は捨てる', () => {
    // **これが通ると、スタイル表そのものを書き換えられる**
    expect(safeColor('red; } body { display:none } /*')).toBeNull();
    expect(safeColor('rgb(255,0,0)')).toBeNull();
    expect(safeColor('red')).toBeNull();
    expect(safeColor('#12345')).toBeNull();
    expect(safeColor('')).toBeNull();
    expect(safeColor(null)).toBeNull();
    expect(safeColor(undefined)).toBeNull();
  });
});

describe('resolveTheme', () => {
  it('配色を選ぶとその色になる', () => {
    const resolved = resolveTheme({ preset: 'Midnight' });

    expect(resolved.backgroundColor).toBe(THEME_PRESET_COLORS.Midnight.backgroundColor);
    expect(resolved.accentTextColor).toBe(THEME_PRESET_COLORS.Midnight.accentTextColor);
  });

  it('個別の指定が配色より優先される', () => {
    // **「配色を選んだうえで 1 色だけ変える」が普通の使い方。**
    // 逆にすると、選び直すたびに個別の指定が消える
    const resolved = resolveTheme({ preset: 'Midnight', accentColor: '#abcdef' });

    expect(resolved.accentColor).toBe('#abcdef');
    expect(resolved.backgroundColor).toBe(THEME_PRESET_COLORS.Midnight.backgroundColor);
  });

  it('指定なしの項目は配色を消さない', () => {
    // **`null` や `undefined` で上書きして配色を潰さないこと**
    const theme: SurveyTheme = { preset: 'Forest', accentColor: null, textColor: undefined };
    const resolved = resolveTheme(theme);

    expect(resolved.accentColor).toBe(THEME_PRESET_COLORS.Forest.accentColor);
    expect(resolved.textColor).toBe(THEME_PRESET_COLORS.Forest.textColor);
  });

  it('配色を選んでいなければ何も足さない', () => {
    expect(resolveTheme({ preset: 'None', accentColor: '#123456' })).toEqual({
      preset: 'None',
      accentColor: '#123456',
    });
    expect(resolveTheme(null)).toEqual({});
    expect(resolveTheme(undefined)).toEqual({});
  });
});

describe('contrastRatio', () => {
  it('白と黒は 21:1', () => {
    // WCAG 2.1 の定義上の最大値
    expect(contrastRatio('#ffffff', '#000000')).toBeCloseTo(21, 5);
  });

  it('同じ色どうしは 1:1', () => {
    expect(contrastRatio('#175cd3', '#175cd3')).toBeCloseTo(1, 5);
  });

  it('3 桁と 6 桁で同じ値になる', () => {
    expect(contrastRatio('#fff', '#000')).toBeCloseTo(contrastRatio('#ffffff', '#000000')!, 5);
  });

  it('順番を入れ替えても同じ値になる', () => {
    expect(contrastRatio('#101828', '#f9fafb')).toBeCloseTo(
      contrastRatio('#f9fafb', '#101828')!,
      5,
    );
  });

  it('読めない色は比べようがないので null', () => {
    expect(contrastRatio('red', '#fff')).toBeNull();
    expect(contrastRatio('#fff', null)).toBeNull();
  });
});

describe('配色として用意した組み合わせ', () => {
  it('既定の色は WCAG 2.1 AA を満たす', () => {
    expect(contrastRatio(DEFAULT_COLORS.textColor, DEFAULT_COLORS.backgroundColor)).toBeGreaterThan(
      CONTRAST_AA,
    );
    expect(
      contrastRatio(DEFAULT_COLORS.accentTextColor, DEFAULT_COLORS.accentColor),
    ).toBeGreaterThan(CONTRAST_AA);
  });

  it.each(THEME_PRESETS.filter((preset) => preset !== 'None'))(
    '%s は地と文字が読める',
    (preset) => {
      // **用意した配色そのものが読めないのでは、選ばせる意味が無い**
      const colors = THEME_PRESET_COLORS[preset];

      expect(contrastRatio(colors.textColor, colors.backgroundColor)).toBeGreaterThan(CONTRAST_AA);
      expect(contrastRatio(colors.textColor, colors.surfaceColor)).toBeGreaterThan(CONTRAST_AA);
      expect(contrastRatio(colors.accentTextColor, colors.accentColor)).toBeGreaterThan(
        CONTRAST_AA,
      );
    },
  );
});

describe('等幅の書体', () => {
  it('Myrica M を先頭に置き、範囲の外は Zen Kaku Gothic New へ落とす（Issue #778・#789）', () => {
    const target = { style: new Map<string, string>() };
    const el = {
      style: {
        setProperty: (name: string, value: string) => target.style.set(name, value),
        removeProperty: (name: string) => target.style.delete(name),
      },
    } as unknown as HTMLElement;

    applyTheme(el, { font: 'Monospace' });

    const font = target.style.get('--font') ?? '';
    expect(font.startsWith('"Myrica M", "Zen Kaku Gothic New", ')).toBe(true);
    expect(font).toContain('monospace');
    // **同梱をやめた M PLUS 1 Code は、もう並べない**
    expect(font).not.toContain('M PLUS 1 Code');
    // **廃止された Noto CJK 系は、もう並べない**（DesignReference 第 2.3 版）
    expect(font).not.toContain('Noto');
  });

  it('ゴシック体は Zen Kaku Gothic New を先頭に置く（Issue #789）', () => {
    const target = { style: new Map<string, string>() };
    const el = {
      style: {
        setProperty: (name: string, value: string) => target.style.set(name, value),
        removeProperty: (name: string) => target.style.delete(name),
      },
    } as unknown as HTMLElement;

    applyTheme(el, { font: 'Sans' });

    const font = target.style.get('--font') ?? '';
    expect(font.startsWith('"Zen Kaku Gothic New", ')).toBe(true);
    expect(font).not.toContain('Noto');
  });
});

/** `applyTheme` が `style` へ入れた値を覚えておく入れ物。 */
function styleTarget() {
  const values = new Map<string, string>();
  const el = {
    style: {
      setProperty: (name: string, value: string) => values.set(name, value),
      removeProperty: (name: string) => values.delete(name),
    },
  } as unknown as HTMLElement;
  return { values, el };
}

describe('無効の入力欄の色（Issue #1069）', () => {
  it('何も指定していなければ触らない（既定の見た目のまま）', () => {
    const { values, el } = styleTarget();

    applyTheme(el, {});

    expect(values.has('--disabled-surface')).toBe(false);
    expect(values.has('--disabled-text')).toBe(false);
  });

  it.each<[string, SurveyTheme]>([
    ['地だけ', { backgroundColor: '#101820' }],
    ['文字だけ', { textColor: '#eeeeee' }],
    ['設問枠だけ', { surfaceColor: '#1f2937' }],
    ['配色', { preset: 'Midnight' }],
  ])('%s を指定すると、地・文字・設問枠から導く', (_name, theme) => {
    const { values, el } = styleTarget();

    applyTheme(el, theme);

    expect(values.get('--disabled-surface')).toBe(
      `color-mix(in srgb, var(--text) ${DISABLED_SURFACE_TEXT_PERCENT}%, var(--surface))`,
    );
    expect(values.get('--disabled-text')).toBe(
      `color-mix(in srgb, var(--text) ${DISABLED_TEXT_PERCENT}%, var(--disabled-surface))`,
    );
  });

  it('指定を外すと導いた値も消える', () => {
    const { values, el } = styleTarget();

    applyTheme(el, { backgroundColor: '#101820' });
    applyTheme(el, {});

    expect(values.has('--disabled-surface')).toBe(false);
    expect(values.has('--disabled-text')).toBe(false);
  });

  // **CSS の `color-mix` はブラウザが実行時に計算する**ので、同じ計算（mixColors）で値を出して比べる。
  const palettes: [string, { text: string; surface: string }][] = [
    ...THEME_PRESETS.filter((preset) => preset !== 'None').map(
      (preset): [string, { text: string; surface: string }] => [
        `配色 ${preset}`,
        {
          text: THEME_PRESET_COLORS[preset].textColor!,
          surface: THEME_PRESET_COLORS[preset].surfaceColor!,
        },
      ],
    ),
    ['作成者のダーク（黒に近い地）', { text: '#f5f5f5', surface: '#111111' }],
    ['作成者のダーク（紺の地）', { text: '#e8eefc', surface: '#1f2937' }],
    ['作成者のライト（白の地）', { text: '#000000', surface: '#ffffff' }],
    ['作成者のライト（既定の色）', { text: DEFAULT_COLORS.textColor, surface: DEFAULT_COLORS.surfaceColor }],
  ];

  it.each(palettes)('%s: 読めて、有効の入力欄と見分けがつく', (_name, { text, surface }) => {
    const disabledSurface = mixColors(text, surface, DISABLED_SURFACE_TEXT_PERCENT)!;
    const disabledText = mixColors(text, disabledSurface, DISABLED_TEXT_PERCENT)!;

    // 無効でも読める（WCAG 2.1 AA。目安の 3:1 より厳しく）
    expect(contrastRatio(disabledText, disabledSurface)!).toBeGreaterThanOrEqual(CONTRAST_AA);
    // 地は有効の入力欄の地と違う色になる（同じ色ではない）
    expect(disabledSurface).not.toBe(surface);
    // 文字は有効の文字より薄い（有効の文字と同じにならない）
    expect(disabledText).not.toBe(text);
    expect(contrastRatio(disabledText, disabledSurface)!).toBeLessThan(
      contrastRatio(text, surface)!,
    );
  });
});
