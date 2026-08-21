import { describe, expect, it } from 'vitest';
import {
  CONTRAST_AA,
  DEFAULT_COLORS,
  THEME_PRESETS,
  THEME_PRESET_COLORS,
  contrastRatio,
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
