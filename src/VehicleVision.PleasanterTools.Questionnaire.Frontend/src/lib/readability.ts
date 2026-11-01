import { contrastRatio } from './theme';

export type FontSize = 'standard' | 'large' | 'extraLarge';
export type ColorMode = 'system' | 'default' | 'highContrast' | 'dark';
export type ColorScheme = 'System' | 'Light' | 'Dark';

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
  fontSize?: FontSize;
  colorMode?: ColorMode;
}

export type ReadabilityPreferenceName = 'fontSize' | 'colorMode';

export const FONT_SIZES: FontSize[] = ['standard', 'large', 'extraLarge'];
export const COLOR_MODES: ColorMode[] = ['system', 'default', 'highContrast', 'dark'];

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
    borderStrong: '#595959',
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
  // **DesignReference のダーク**（styles/tokens.css の `[data-theme="dark"]`。値は readability.test.ts が突き合わせる）。
  // accent は面にも文字にも使うので、文字用の淡い鉄紺（--color-primary-text）。その上の文字は鉄紺（--color-primary）
  dark: {
    accent: '#d0d3ff',
    accentText: '#17184b',
    background: '#30334f',
    surface: '#3b3f66',
    text: '#f4f4f8',
    muted: '#c4c6dc',
    border: '#60668f',
    borderStrong: '#9398c8',
    error: '#ffb0b4',
    errorSurface: '#5b3150',
    success: '#7fdcae',
    successSurface: '#2a5a52',
    warning: '#f2b277',
    warningSurface: '#5a4440',
    warningBorder: '#f2b277',
    disabledSurface: '#30334f',
    disabledText: '#c4c6dc',
    infoSurface: '#4a4f80',
    infoText: '#f4f4f8',
  },
} as const;

const FONT_SIZE_PERCENT: Record<FontSize, string> = {
  standard: '100%',
  large: '112.5%',
  extraLarge: '125%',
};

/**
 * 保存値が無いときの配色。
 *
 * **既定はライト**（DesignReference の明示型。Issue #776）。OS のダーク設定には従わない。
 * - アンケートの配色固定 `Light` / `Dark` はそのとおりにする
 * - `System` は、作成者が「OS に合わせる」を選んだ明示なので、OS（高コントラスト → ダーク → ライト）に従う
 * - 固定が無いときだけ、OS の高コントラスト設定（アクセシビリティ）には従い、ダークには従わない
 */
function defaultColorMode(
  colorScheme: ColorScheme | null | undefined,
  system: SystemReadabilityPreferences,
): ColorMode {
  if (colorScheme === 'Light') {return 'default';}
  if (colorScheme === 'Dark') {return 'dark';}
  if (colorScheme === 'System') {return 'system';}
  return system.highContrast ? 'highContrast' : 'default';
}

/**
 * 保存値と OS の設定から初期値を決める。
 *
 * 明示した配色（保存値・アンケートの固定）は OS より優先する。
 */
export function resolveReadabilityPreferences(
  stored: StoredReadabilityPreferences | null,
  system: SystemReadabilityPreferences,
  colorScheme: ColorScheme | null | undefined = null,
): ReadabilityPreferences {
  return {
    fontSize: stored?.fontSize ?? 'standard',
    colorMode: stored?.colorMode ?? defaultColorMode(colorScheme, system),
    reducedMotion: system.reducedMotion,
  };
}

/** OS に従う指定を、実際に当てる配色へ解決する。 */
export function resolvedColorMode(
  colorMode: ColorMode,
  system: SystemReadabilityPreferences,
): Exclude<ColorMode, 'system'> {
  return colorMode === 'system'
    ? system.highContrast
      ? 'highContrast'
      : system.dark
        ? 'dark'
        : 'default'
    : colorMode;
}

/** 読みやすさ設定を CSSOM へ渡す値に変換する。 */
export function readabilityCssProperties(
  preferences: ReadabilityPreferences,
  system: SystemReadabilityPreferences = { dark: false, highContrast: false, reducedMotion: false },
): Record<string, string | null> {
  const colorMode = resolvedColorMode(preferences.colorMode, system);
  const colors =
    colorMode === 'default' ? null : READABILITY_COLORS[colorMode];

  return {
    'font-size': FONT_SIZE_PERCENT[preferences.fontSize],
    '--accent': colors?.accent ?? null,
    '--accent-text': colors?.accentText ?? null,
    '--bg': colors?.background ?? null,
    '--surface': colors?.surface ?? null,
    '--text': colors?.text ?? null,
    '--muted': colors?.muted ?? null,
    '--border': colors?.border ?? null,
    '--border-strong': colors?.borderStrong ?? null,
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
  system: SystemReadabilityPreferences,
): void {
  if (!target) {return;}

  // **ダークのときだけ `data-theme="dark"`**（DesignReference の明示型）。
  // 色のトークン（--color-*）と、ブラウザ標準の部品（スクロールバー・入力欄）の配色が切り替わる
  if (resolvedColorMode(preferences.colorMode, system) === 'dark') {
    target.setAttribute('data-theme', 'dark');
  } else {
    target.removeAttribute('data-theme');
  }

  for (const [name, value] of Object.entries(readabilityCssProperties(preferences, system))) {
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
    if (typeof value !== 'object' || value === null) {
      return null;
    }

    const candidate = value as { fontSize?: unknown; colorMode?: unknown };
    const fontSize = FONT_SIZES.includes(candidate.fontSize as FontSize)
      ? (candidate.fontSize as FontSize)
      : undefined;
    const colorMode = COLOR_MODES.includes(candidate.colorMode as ColorMode)
      ? (candidate.colorMode as ColorMode)
      : undefined;
    if (fontSize === undefined && colorMode === undefined) {return null;}

    return {
      fontSize,
      colorMode,
    };
  } catch {
    return null;
  }
}

/**
 * 明示した設定を端末だけに保存する。保存できなくても表示の切り替えは続ける。
 */
export function saveReadabilityPreferences(
  preferences: ReadabilityPreferences,
  changed: ReadabilityPreferenceName,
): void {
  try {
    const stored = readReadabilityPreferences() ?? {};
    if (changed === 'fontSize') {
      stored.fontSize = preferences.fontSize;
    } else {
      stored.colorMode = preferences.colorMode;
    }
    localStorage.setItem(READABILITY_STORAGE_KEY, JSON.stringify(stored));
  } catch {
    // 保存領域を使えなくても、その場の表示は変えられる。
  }
}

/** 配色内で本文に使う組み合わせの最小コントラスト比。 */
export function readabilityContrastRatios(mode: Exclude<ColorMode, 'default' | 'system'>): number[] {
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
