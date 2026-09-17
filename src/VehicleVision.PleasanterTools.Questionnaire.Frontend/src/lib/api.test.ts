import { afterEach, describe, expect, it, vi } from 'vitest';
import { redeemAssetTicket } from './api';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('redeemAssetTicket', () => {
  it('断片を本文で引き換えて直ちに消す', async () => {
    const replaceState = vi.fn();
    const fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        publicId: 'pub-1',
        definition: {
          surveyId: 's1',
          version: 1,
          title: { ja: '調査' },
          pages: [],
          displayMode: 'Paged',
          showProgress: true,
          allowEditingAfterSubmit: false,
        },
      }),
    });
    vi.stubGlobal('window', {
      location: {
        hash: '#d=secret-ticket',
        pathname: '/f/pub-1',
        search: '?lang=ja',
      },
      history: { replaceState },
    });
    vi.stubGlobal('fetch', fetch);

    const result = await redeemAssetTicket('pub-1');

    expect(result?.publicId).toBe('pub-1');
    expect(replaceState).toHaveBeenCalledWith(null, '', '/f/pub-1?lang=ja');
    expect(fetch).toHaveBeenCalledWith('/api/forms/pub-1/asset-ticket', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ assetTicket: 'secret-ticket' }),
    });
  });

  it('引換失敗でも断片を消す', async () => {
    const replaceState = vi.fn();
    vi.stubGlobal('window', {
      location: { hash: '#d=expired', pathname: '/f/pub-1', search: '' },
      history: { replaceState },
    });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));

    expect(await redeemAssetTicket('pub-1')).toBeNull();
    expect(replaceState).toHaveBeenCalledWith(null, '', '/f/pub-1');
  });
});
