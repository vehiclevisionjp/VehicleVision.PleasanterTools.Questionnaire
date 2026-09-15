import { describe, expect, it } from 'vitest';
import { en, ja, serverValidationKey, translator, type MessageKey } from './messages';

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
    for (const [key, value] of Object.entries(ja)) {
      expect(value, `ja.${key}`).not.toBe('');
    }
    for (const [key, value] of Object.entries(en)) {
      expect(value, `en.${key}`).not.toBe('');
    }
  });
});

describe('translator', () => {
  it('その言語の文言を引く', () => {
    const key = Object.keys(ja)[0] as MessageKey;

    expect(translator('ja')(key)).toBe(ja[key]);
    expect(translator('en')(key)).toBe(en[key]);
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
