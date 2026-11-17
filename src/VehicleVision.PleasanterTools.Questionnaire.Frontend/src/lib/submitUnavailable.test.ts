import { describe, expect, it } from 'vitest';
import { SUPPORTED_LANGUAGES } from './i18n/language';
import { maintenanceNotices } from './submitUnavailable';

describe('maintenanceNotices', () => {
  it('7 言語すべての案内を返す', () => {
    const notices = maintenanceNotices('ja');

    expect(notices.map((notice) => notice.language).sort()).toEqual([...SUPPORTED_LANGUAGES].sort());
    expect(new Set(notices.map((notice) => notice.text)).size).toBe(SUPPORTED_LANGUAGES.length);
  });

  it('現在の言語を先頭に置き、残りは対応言語の順に続ける', () => {
    expect(maintenanceNotices('ja').map((notice) => notice.language)).toEqual(SUPPORTED_LANGUAGES);
    expect(maintenanceNotices('de').map((notice) => notice.language)).toEqual([
      'de', 'ja', 'en', 'zh', 'ko', 'es', 'vi',
    ]);
  });

  it('各行に、その言語自身の名前と、その言語の文言を付ける', () => {
    const [first, second] = maintenanceNotices('zh');

    expect(first).toMatchObject({ language: 'zh', name: '中文' });
    expect(first?.text).toContain('维护');
    expect(second).toMatchObject({ language: 'ja', name: '日本語' });
    expect(second?.text).toContain('メンテナンス');
  });

  it('読み込み時の案内（Issue #965）も 7 言語を並べ、送信時とは別の文言になる', () => {
    const loading = maintenanceNotices('en', 'unavailable.maintenance');
    const submitting = maintenanceNotices('en');

    expect(loading.map((notice) => notice.language)).toEqual(['en', ...SUPPORTED_LANGUAGES.filter((language) => language !== 'en')]);
    expect(loading.map((notice) => notice.text)).not.toEqual(submitting.map((notice) => notice.text));
    expect(loading[1]?.text).toContain('メンテナンス');
  });
});
