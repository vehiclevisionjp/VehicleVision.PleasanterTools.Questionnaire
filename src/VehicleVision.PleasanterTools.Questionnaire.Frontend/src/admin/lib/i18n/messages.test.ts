import type { Language } from '../../../lib/i18n/language';
import { describe, expect, it } from 'vitest';
import {
  de,
  en,
  es,
  ja,
  ko,
  translator,
  vi,
  zh,
  type MessageKey,
} from './messages';

const catalogs = { ja, en, zh, de, ko, es, vi };

function placeholders(value: string): string[] {
  return [...value.matchAll(/\{(\w+)\}/g)]
    .map((match) => match[1]!)
    .filter((name, index, names) => names.indexOf(name) === index)
    .sort();
}

describe('一覧へ戻るリンクの文言', () => {
  const removedKeys = [
    'help.back',
    'users.back',
    'account.back',
    'saml.back',
    'appSettings.back',
    'editor.back',
    'audit.back',
    'notifications.back',
    'outbox.back',
  ] as const;

  it.each(removedKeys)('%s を日本語と英語のカタログから除いている', (key) => {
    expect(ja).not.toHaveProperty(key);
    expect(en).not.toHaveProperty(key);
  });
});

describe('translator', () => {
  /**
   * **未翻訳の言語は英語で操作できる状態にする。** 日本語へ直接落とさない。
   *
   * ⚠️ **確かめる言語を決め打ちにしない。** 訳が進むと、その言語では
   * 落とし先を通らなくなり、**試験が何も確かめなくなるか落ちる。**
   * 訳の無い鍵を探して確かめ、全部訳し終わっていたら
   * 代わりに各言語が自分の文言を返すことを確かめる。
   */
  it('訳の無い鍵は英語の文言へ落ちる', () => {
    const entries = Object.entries(catalogs) as [Language, Partial<Record<MessageKey, string>>][];
    const missing = entries
      .filter(([language]) => language !== 'ja' && language !== 'en')
      .flatMap(([language, catalog]) =>
        (Object.keys(ja) as MessageKey[])
          .filter((key) => !(key in catalog))
          .map((key) => [language, key] as const))
      .at(0);

    if (missing) {
      const [language, key] = missing;
      expect(translator(language)(key)).toBe(en[key]);
      return;
    }

    const key = Object.keys(ja)[0] as MessageKey;
    for (const [language, catalog] of entries) {
      expect(translator(language)(key), language).toBe(catalog[key]);
    }
  });
});

describe('文言の集合', () => {
  // **日英で鍵の集合が同じでなければならない。** 足し忘れると画面に鍵がそのまま出る
  it('日本語と英語で鍵が揃っている', () => {
    expect(Object.keys(en).sort()).toEqual(Object.keys(ja).sort());
  });

  it('空の文言を置かない', () => {
    for (const [language, catalog] of Object.entries(catalogs)) {
      for (const [key, value] of Object.entries(catalog)) {
        expect(value, `${language}.${key}`).not.toBe('');
      }
    }
  });

  it('各言語のカタログに日本語カタログに無い鍵がない', () => {
    for (const [language, catalog] of Object.entries(catalogs)) {
      const extra = Object.keys(catalog).filter((key) => !(key in ja));
      expect(extra, language).toEqual([]);
    }
  });

  it('各言語にある文言の差し込み名が日本語と一致する', () => {
    for (const [language, catalog] of Object.entries(catalogs)) {
      for (const [key, value] of Object.entries(catalog)) {
        expect(placeholders(value), `${language}.${key}`).toEqual(
          placeholders(ja[key as MessageKey]),
        );
      }
    }
  });
});
