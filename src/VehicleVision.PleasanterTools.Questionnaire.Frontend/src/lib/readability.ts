import { contrastRatio } from './theme';

export type FontSize = 'standard' | 'large' | 'extraLarge';
export type ColorMode = 'default' | 'highContrast' | 'dark';

export interface ReadabilityPreferences {
  fontSize: FontSize;
  colorMode: ColorMode;
  reducedMotion: boolean;
}

export interface SystemReadabilityPreferences {
  dark: boolean;
  highContrast: boolean;
  reducedMotion: boolean;
}

interface StoredReadabilityPreferences {
  fontSize: FontSize;
  colorMode: ColorMode;
}

export const FONT_SIZES: FontSize[] = ['standard', 'large', 'extraLarge'];
export const COLOR_MODES: ColorMode[] = ['default', 'highContrast', 'dark'];

export const READABILITY_STORAGE_KEY = 'questionnaire.readability';

export const READABILITY_COLORS = {
  highContrast: {
    accent: '#0047b3',
    accentText: '#ffffff',
    background: '#ffffff',
    surface: '#ffffff',
    text: '#000000',
    muted: '#404040',
    border: '#595959',
    error: '#b00020',
    errorSurface: '#ffffff',
    success: '#006b35',
    successSurface: '#ffffff',
    warning: '#6b3a00',
    warningSurface: '#ffffff',
    warningBorder: '#6b3a00',
    disabledSurface: '#eeeeee',
    disabledText: '#333333',
    infoSurface: '#ffffff',
    infoText: '#000000',
  },
  dark: {
    accent: '#8ab4f8',
    accentText: '#101010',
    background: '#121212',
    surface: '#1e1e1e',
    text: '#f5f5f5',
    muted: '#c7c7c7',
    border: '#8a8a8a',
    error: '#ffb4ab',
    errorSurface: '#3b2020',
    success: '#75d69c',
    successSurface: '#173528',
    warning: '#ffca80',
    warningSurface: '#3a2a14',
    warningBorder: '#d59b4a',
    disabledSurface: '#2c2c2c',
    disabledText: '#c7c7c7',
    infoSurface: '#1e2b43',
    infoText: '#d8e6ff',
  },
} as const;

const FONT_SIZE_PERCENT: Record<FontSize, string> = {
  standard: '100%',
  large: '112.5%',
  extraLarge: '125%',
};

/**
 * 保存値と OS の設定から初期値を決める。
 *
 * 明示した配色は OS より優先する。保存値が無いときは、高コントラストを
 * ダークより先に見る。
 */
export function resolveReadabilityPreferences(
  stored: StoredReadabilityPreferences | null,
  system: SystemReadabilityPreferences,
): ReadabilityPreferences {
  return {
    fontSize: stored?.fontSize ?? 'standard',
    colorMode: stored?.colorMode ?? (system.highContrast ? 'highContrast' : system.dark ? 'dark' : 'default'),
    reducedMotion: system.reducedMotion,
  };
}

/** 読みやすさ設定を CSSOM へ渡す値に変換する。 */
export function readabilityCssProperties(
  preferences: ReadabilityPreferences,
): Record<string, string | null> {
  const colors =
    preferences.colorMode === 'default' ? null : READABILITY_COLORS[preferences.colorMode];

  return {
    'font-size': FONT_SIZE_PERCENT[preferences.fontSize],
    '--accent': colors?.accent ?? null,
    '--accent-text': colors?.accentText ?? null,
    '--bg': colors?.background ?? null,
    '--surface': colors?.surface ?? null,
    '--text': colors?.text ?? null,
    '--muted': colors?.muted ?? null,
    '--border': colors?.border ?? null,
    '--error': colors?.error ?? null,
    '--error-surface': colors?.errorSurface ?? null,
    '--success': colors?.success ?? null,
    '--success-surface': colors?.successSurface ?? null,
    '--warning-text': colors?.warning ?? null,
    '--warning-surface': colors?.warningSurface ?? null,
    '--warning-border': colors?.warningBorder ?? null,
    '--disabled-surface': colors?.disabledSurface ?? null,
    '--disabled-text': colors?.disabledText ?? null,
    '--info-surface': colors?.infoSurface ?? null,
    '--info-text': colors?.infoText ?? null,
    '--motion-duration': preferences.reducedMotion ? '0s' : null,
  };
}

/**
 * 読みやすさ設定を CSSOM で反映する。
 *
 * `style` 属性や動的な `<style>` は CSP で拒否されるため、テーマと同じく
 * `style.setProperty` だけを使う。
 */
export function applyReadability(
  target: HTMLElement | null | undefined,
  preferences: ReadabilityPreferences,
): void {
  if (!target) return;

  for (const [name, value] of Object.entries(readabilityCssProperties(preferences))) {
    if (value === null) {
      target.style.removeProperty(name);
    } else {
      target.style.setProperty(name, value);
    }
  }
}

/** OS の表示設定を読む。 */
export function systemReadabilityPreferences(): SystemReadabilityPreferences {
  return {
    dark: window.matchMedia('(prefers-color-scheme: dark)').matches,
    highContrast: window.matchMedia('(prefers-contrast: more)').matches,
    reducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  };
}

/**
 * 端末に保存した設定を読む。保存領域を使えない環境では未保存として扱う。
 */
export function readReadabilityPreferences(): StoredReadabilityPreferences | null {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(READABILITY_STORAGE_KEY) ?? 'null');
    if (
      typeof value !== 'object' ||
      value === null ||
      !('fontSize' in value) ||
      !FONT_SIZES.includes(value.fontSize as FontSize) ||
      !('colorMode' in value) ||
      !COLOR_MODES.includes(value.colorMode as ColorMode)
    ) {
      return null;
    }

    return {
      fontSize: value.fontSize as FontSize,
      colorMode: value.colorMode as ColorMode,
    };
  } catch {
    return null;
  }
}

/**
 * 明示した設定を端末だけに保存する。保存できなくても表示の切り替えは続ける。
 */
export function saveReadabilityPreferences(preferences: ReadabilityPreferences): void {
  try {
    const stored: StoredReadabilityPreferences = {
      fontSize: preferences.fontSize,
      colorMode: preferences.colorMode,
    };
    localStorage.setItem(READABILITY_STORAGE_KEY, JSON.stringify(stored));
  } catch {
    // 保存領域を使えなくても、その場の表示は変えられる。
  }
}

/** 配色内で本文に使う組み合わせの最小コントラスト比。 */
export function readabilityContrastRatios(mode: Exclude<ColorMode, 'default'>): number[] {
  const colors = READABILITY_COLORS[mode];
  return [
    contrastRatio(colors.text, colors.background)!,
    contrastRatio(colors.text, colors.surface)!,
    contrastRatio(colors.muted, colors.background)!,
    contrastRatio(colors.accentText, colors.accent)!,
    contrastRatio(colors.error, colors.background)!,
    contrastRatio(colors.error, colors.errorSurface)!,
    contrastRatio(colors.success, colors.background)!,
    contrastRatio(colors.success, colors.successSurface)!,
    contrastRatio(colors.warning, colors.warningSurface)!,
    contrastRatio(colors.disabledText, colors.disabledSurface)!,
    contrastRatio(colors.infoText, colors.infoSurface)!,
  ];
}
