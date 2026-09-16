import { describe, expect, it } from 'vitest';
import { assetMarkup } from './asset';

describe('assetMarkup', () => {
  it('画像は埋め込み記法にする', () => {
    expect(assetMarkup('figure.png', 'asset-id', true)).toBe(
      '![figure.png](asset:asset-id)',
    );
  });

  it('画像以外はリンク記法にする', () => {
    expect(assetMarkup('catalog.pdf', 'asset-id', false)).toBe(
      '[catalog.pdf](asset:asset-id)',
    );
  });
});
