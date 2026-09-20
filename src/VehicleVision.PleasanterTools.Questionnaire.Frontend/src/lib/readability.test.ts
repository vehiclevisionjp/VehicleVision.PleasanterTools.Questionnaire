import { describe, expect, it } from 'vitest';
import {
  COLOR_MODES,
  readabilityContrastRatios,
  readabilityCssProperties,
  resolveReadabilityPreferences,
} from './readability';
import { CONTRAST_AA } from './theme';

describe('resolveReadabilityPreferences', () => {
  it('保存した選択を OS の設定より優先する', () => {
    expect(
      resolveReadabilityPreferences(
        { fontSize: 'large', colorMode: 'default' },
        { dark: true, highContrast: true, reducedMotion: true },
      ),
    ).toEqual({ fontSize: 'large', colorMode: 'default', reducedMotion: true });
  });

  it('保存値が無ければ OS の高コントラストを優先する', () => {
    expect(
      resolveReadabilityPreferences(null, {
        dark: true,
        highContrast: true,
        reducedMotion: false,
      }).colorMode,
    ).toBe('highContrast');
  });

  it('高コントラストでなければ OS のダーク設定を使う', () => {
    expect(
      resolveReadabilityPreferences(null, {
        dark: true,
        highContrast: false,
        reducedMotion: false,
      }).colorMode,
    ).toBe('dark');
  });
});

describe('readabilityCssProperties', () => {
  it('文字の大きさをルート要素の倍率へ変換する', () => {
    const properties = readabilityCssProperties({
      fontSize: 'extraLarge',
      colorMode: 'default',
      reducedMotion: false,
    });

    expect(properties['font-size']).toBe('125%');
  });

  it('既定配色では作成者の色を上書きしない', () => {
    const properties = readabilityCssProperties({
      fontSize: 'standard',
      colorMode: 'default',
      reducedMotion: false,
    });

    expect(properties['--accent']).toBeNull();
    expect(properties['--bg']).toBeNull();
    expect(properties['--text']).toBeNull();
  });

  it('動きを減らす OS 設定を CSS へ渡す', () => {
    const properties = readabilityCssProperties({
      fontSize: 'standard',
      colorMode: 'default',
      reducedMotion: true,
    });

    expect(properties['--motion-duration']).toBe('0s');
  });
});

describe('見る人が選べる配色', () => {
  it.each(COLOR_MODES.filter((mode) => mode !== 'default'))(
    '%s の本文に使う全組み合わせが WCAG 2.1 AA を満たす',
    (mode) => {
      for (const ratio of readabilityContrastRatios(mode)) {
        expect(ratio).toBeGreaterThanOrEqual(CONTRAST_AA);
      }
    },
  );
});
