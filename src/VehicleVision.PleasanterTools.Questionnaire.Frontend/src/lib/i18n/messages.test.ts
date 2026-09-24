import { describe, expect, it } from 'vitest';
import {
  de,
  en,
  es,
  ja,
  ko,
  serverValidationKey,
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

describe('serverValidationKey', () => {
  it('文言のある符号はその鍵を返す', () => {
    expect(serverValidationKey('Required')).toBe('serverValidation.Required');
  });

  // **サーバに符号が増えても画面を壊さない。** 知らないものは一括の文言へ落とす
  it('文言の無い符号は既定の鍵へ落とす', () => {
    expect(serverValidationKey('MadeUpCode')).toBe('serverValidation.unknown');
  });

  it('空の符号も既定の鍵へ落とす', () => {
    expect(serverValidationKey('')).toBe('serverValidation.unknown');
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

describe('translator', () => {
  it('その言語の文言を引く', () => {
    const key = Object.keys(ja)[0] as MessageKey;

    expect(translator('ja')(key)).toBe(ja[key]);
    expect(translator('en')(key)).toBe(en[key]);
  });

  it('未翻訳の言語は英語の文言へ落ちる', () => {
    const key = Object.keys(ja)[0] as MessageKey;

    expect(translator('zh')(key)).toBe(en[key]);
    expect(translator('vi')(key)).toBe(en[key]);
  });

  it('差し込みのある文言を組み立てる', () => {
    // 差し込みを持つ鍵を 1 つ選び、置き換わることだけを見る
    const key = (Object.keys(ja) as MessageKey[]).find((candidate) =>
      /\{\w+\}/.test(ja[candidate]),
    );

    expect(key, '差し込みのある文言が 1 つも無い').toBeDefined();

    const name = /\{(\w+)\}/.exec(ja[key!])![1]!;
    expect(translator('ja')(key!, { [name]: '差し込み' })).not.toContain(`{${name}}`);
  });
});
