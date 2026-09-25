import { describe, expect, it } from 'vitest';
import { appRoute, appUrl, basePath, normalizeBasePath } from './basePath';

describe('normalizeBasePath', () => {
  it('差し替わっていない印はサブパス無しとして扱う', () => {
    expect(normalizeBasePath('__QUESTIONNAIRE_BASE_PATH__')).toBe('');
  });

  it('空や未設定はサブパス無し', () => {
    expect(normalizeBasePath('')).toBe('');
    expect(normalizeBasePath(null)).toBe('');
    expect(normalizeBasePath(undefined)).toBe('');
    expect(normalizeBasePath('/')).toBe('');
  });

  it('サブパスはそのまま、末尾の / は落とす', () => {
    expect(normalizeBasePath('/questionnaire')).toBe('/questionnaire');
    expect(normalizeBasePath('/questionnaire/')).toBe('/questionnaire');
    expect(normalizeBasePath('/apps/questionnaire')).toBe('/apps/questionnaire');
  });

  it('自サイトの外を指す値は受け付けない', () => {
    expect(normalizeBasePath('//example.com')).toBe('');
    expect(normalizeBasePath('https://example.com/q')).toBe('');
    expect(normalizeBasePath('questionnaire')).toBe('');
  });
});

describe('appUrl', () => {
  it('サブパスが無ければ経路をそのまま返す', () => {
    expect(appUrl('/api/forms/abc', '')).toBe('/api/forms/abc');
  });

  it('サブパスを先頭に付ける', () => {
    expect(appUrl('/api/forms/abc', '/questionnaire')).toBe('/questionnaire/api/forms/abc');
    expect(appUrl('/f/abc', '/questionnaire')).toBe('/questionnaire/f/abc');
  });

  it('/ で始まらない経路は拒否する', () => {
    expect(() => appUrl('api/forms', '/questionnaire')).toThrow();
  });

  it('meta 要素の無い試験環境ではサブパス無しで動く', () => {
    expect(basePath).toBe('');
    expect(appUrl('/api/analytics')).toBe('/api/analytics');
  });
});

describe('appRoute', () => {
  it('サブパスを除いた経路を返す', () => {
    expect(appRoute('/questionnaire/f/abc', '/questionnaire')).toBe('/f/abc');
    expect(appRoute('/questionnaire', '/questionnaire')).toBe('/');
  });

  it('サブパスの外は null', () => {
    expect(appRoute('/f/abc', '/questionnaire')).toBeNull();
    expect(appRoute('/questionnaire-other/f/abc', '/questionnaire')).toBeNull();
  });

  it('サブパスが無ければそのまま', () => {
    expect(appRoute('/f/abc', '')).toBe('/f/abc');
  });
});
