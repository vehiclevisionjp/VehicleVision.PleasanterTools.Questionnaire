import { describe, expect, it } from 'vitest';
import { normalizeAnswer } from './answerNormalization';

describe('回答の自動変換', () => {
  it('指定した4種類を組み合わせて変換する', () => {
    expect(
      normalizeAnswer('　ＡＢＣ１２３－４５６７　ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ　', {
        convertFullWidthAsciiToHalfWidth: true,
        convertHalfWidthKanaToFullWidth: true,
        convertFullWidthSpacesToHalfWidth: true,
        trimWhitespace: true,
      }),
    ).toBe('ABC123-4567 パピプペポ');
  });

  it('NFKCで変わる互換文字は変更しない', () => {
    expect(
      normalizeAnswer('㍿①Ⅳ㎡', {
        convertFullWidthAsciiToHalfWidth: true,
        convertHalfWidthKanaToFullWidth: true,
        convertFullWidthSpacesToHalfWidth: true,
        trimWhitespace: true,
      }),
    ).toBe('㍿①Ⅳ㎡');
  });

  it('指定が無ければ変更しない', () => {
    expect(normalizeAnswer('　ＡＢＣ ﾊﾟﾋﾟ　', {})).toBe('　ＡＢＣ ﾊﾟﾋﾟ　');
  });
});
