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

  describe('translator', () => {
    it('未翻訳の言語は英語の文言へ落ちる', () => {
      const key = Object.keys(ja)[0] as MessageKey;

      expect(translator('zh')(key)).toBe(en[key]);
      expect(translator('vi')(key)).toBe(en[key]);
    });
  });

  describe('文言の集合', () => {
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
});
