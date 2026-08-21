import { describe, expect, it } from 'vitest';
import {
  DEFAULT_LANGUAGE,
  SUPPORTED_LANGUAGES,
  formatElapsed,
  formatNumber,
  interpolate,
  negotiateLanguage,
  normalizeLanguage,
} from './language';

describe('normalizeLanguage', () => {
  it('対応している言語はそのまま返す', () => {
    expect(normalizeLanguage('ja')).toBe('ja');
    expect(normalizeLanguage('en')).toBe('en');
  });

  // **地域まで見ない。** `en-US` も `en-GB` も同じ英語として扱う
  it('地域付きの言語タグは主部分だけを見る', () => {
    expect(normalizeLanguage('en-US')).toBe('en');
    expect(normalizeLanguage('ja-JP')).toBe('ja');
    expect(normalizeLanguage('en_GB')).toBe('en');
  });

  it('大文字小文字と前後の空白を無視する', () => {
    expect(normalizeLanguage('  EN-us ')).toBe('en');
  });

  it('対応していない言語は null', () => {
    expect(normalizeLanguage('fr')).toBeNull();
    expect(normalizeLanguage('zh-CN')).toBeNull();
  });

  it('空や未指定は null', () => {
    expect(normalizeLanguage('')).toBeNull();
    expect(normalizeLanguage(null)).toBeNull();
    expect(normalizeLanguage(undefined)).toBeNull();
  });
});

describe('negotiateLanguage', () => {
  it('明示された言語を最優先で使う', () => {
    expect(negotiateLanguage('en', ['ja'])).toBe('en');
  });

  // **明示が対応外なら、無かったものとして次を見る**
  it('明示が対応外ならブラウザの設定を見る', () => {
    expect(negotiateLanguage('fr', ['de', 'en-US'])).toBe('en');
  });

  it('ブラウザの設定は先頭から順に見る', () => {
    expect(negotiateLanguage(null, ['en', 'ja'])).toBe('en');
    expect(negotiateLanguage(null, ['ja', 'en'])).toBe('ja');
  });

  it('どれも当たらなければ既定の言語', () => {
    expect(negotiateLanguage(null, ['fr', 'de'])).toBe(DEFAULT_LANGUAGE);
    expect(negotiateLanguage(undefined)).toBe(DEFAULT_LANGUAGE);
  });

  // **既定はサーバ側の `LocalizedText.DefaultLanguage` と同じでなければならない**
  it('既定の言語は対応している言語の 1 つ', () => {
    expect(SUPPORTED_LANGUAGES).toContain(DEFAULT_LANGUAGE);
  });
});

describe('formatNumber', () => {
  it('言語ごとの区切りで書く', () => {
    expect(formatNumber(1234567, 'ja')).toBe('1,234,567');
    expect(formatNumber(1234567, 'en')).toBe('1,234,567');
  });

  it('小さい数はそのまま', () => {
    expect(formatNumber(0, 'ja')).toBe('0');
    expect(formatNumber(-5, 'en')).toBe('-5');
  });
});

describe('formatElapsed', () => {
  const now = new Date('2026-08-21T12:00:00Z');

  it('日単位で書く', () => {
    expect(formatElapsed(new Date('2026-08-19T12:00:00Z'), 'en', now)).toBe('2 days ago');
  });

  it('時間単位で書く', () => {
    expect(formatElapsed(new Date('2026-08-21T09:00:00Z'), 'en', now)).toBe('3 hours ago');
  });

  it('分単位で書く', () => {
    expect(formatElapsed(new Date('2026-08-21T11:30:00Z'), 'en', now)).toBe('30 minutes ago');
  });

  // **1 分未満は「0 分前」ではなく「今」と読ませる**
  it('1 分未満は今として書く', () => {
    expect(formatElapsed(new Date('2026-08-21T11:59:30Z'), 'en', now)).toBe('this minute');
  });

  // **言い回しは `Intl` に任せる。** 単複や「一昨日」の言い換えを自前で持たない
  it('日本語でも書ける', () => {
    expect(formatElapsed(new Date('2026-08-21T09:00:00Z'), 'ja', now)).toBe('3 時間前');
  });

  // **一番大きい単位だけで書く。** 滞留に気付くのが目的で、細かさは要らない
  it('日をまたぐときは時間を混ぜない', () => {
    expect(formatElapsed(new Date('2026-08-19T09:00:00Z'), 'en', now)).toBe('2 days ago');
  });

  it('未来の時刻も書ける', () => {
    expect(formatElapsed(new Date('2026-08-23T12:00:00Z'), 'en', now)).toBe('in 2 days');
  });
});

describe('interpolate', () => {
  it('名前の合う所を置き換える', () => {
    expect(interpolate('{name} さん', { name: '山田' }, 'ja')).toBe('山田 さん');
  });

  // **数値は言語に合わせて整形する**
  it('数値は区切りを付けて置き換える', () => {
    expect(interpolate('{count} 件', { count: 12345 }, 'ja')).toBe('12,345 件');
  });

  it('渡されなかった名前はそのまま残す', () => {
    expect(interpolate('{name} と {other}', { name: 'A' }, 'ja')).toBe('A と {other}');
  });

  it('差し込みが無ければそのまま返す', () => {
    expect(interpolate('そのまま', undefined, 'ja')).toBe('そのまま');
    expect(interpolate('そのまま', {}, 'ja')).toBe('そのまま');
  });

  it('同じ名前が 2 回あればどちらも置き換える', () => {
    expect(interpolate('{a}{a}', { a: 'x' }, 'ja')).toBe('xx');
  });

  it('空文字への置き換えもできる', () => {
    expect(interpolate('[{a}]', { a: '' }, 'ja')).toBe('[]');
  });
});
