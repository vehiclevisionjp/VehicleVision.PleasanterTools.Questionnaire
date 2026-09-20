import { describe, expect, it } from 'vitest';
import { framedHeaders, isFramed } from './framed';

describe('枠内の判定', () => {
  it('自分自身が最上位なら枠外と判定する', () => {
    const page = {};

    expect(isFramed({ self: page, top: page })).toBe(false);
    expect(framedHeaders({ self: page, top: page })).toEqual({});
  });

  it('最上位が別なら申告ヘッダを付ける', () => {
    expect(isFramed({ self: {}, top: {} })).toBe(true);
    expect(framedHeaders({ self: {}, top: {} })).toEqual({
      'X-Questionnaire-Framed': '1',
    });
  });
});
