import { afterEach, describe, expect, it, vi } from 'vitest';
import { redeemAssetTicket, submitAnswers, type SubmitContext } from './api';

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

describe('submitAnswers の応答の振り分け（Issue #961）', () => {
  const context: SubmitContext = { ticket: 't', trap: '', altcha: 'a' };

  /** 指定した状態コードと本文を返す fetch を差し込んで送信する。`body` が `undefined` なら JSON として読めない。 */
  async function submitWith(status: number, body?: unknown) {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        status,
        json: async () => {
          if (body === undefined) {
            throw new SyntaxError('Unexpected token < in JSON');
          }
          return body;
        },
      }),
    );
    // 枠内判定（`framedHeaders`）が `window` を読む。localStorage は使えない状況として扱う
    vi.stubGlobal('window', { localStorage: undefined });
    return submitAnswers('pub-1', 'token', [], context);
  }

  it('503 の reason が maintenance なら、メンテナンスとしてサーバの案内を返す', async () => {
    const result = await submitWith(503, { reason: 'maintenance', message: '本日 22 時まで停止します' });

    expect(result).toEqual({
      accepted: false,
      unavailable: 'maintenance',
      maintenanceMessage: '本日 22 時まで停止します',
    });
  });

  it('メンテナンスの案内が文字列でなければ空にする', async () => {
    const result = await submitWith(503, { reason: 'maintenance', message: 42 });

    expect(result.unavailable).toBe('maintenance');
    expect(result.maintenanceMessage).toBe('');
  });

  it('503 の reason が scannerUnavailable なら、スキャナ停止の表示にする', async () => {
    const result = await submitWith(503, { reason: 'scannerUnavailable' });

    expect(result).toEqual({ accepted: false, errors: { '': ['ScannerUnavailable'] } });
  });

  it('503 の本文が JSON でなければ、スキャナ停止にせず一時的な不調にする', async () => {
    const result = await submitWith(503);

    expect(result).toEqual({ accepted: false, unavailable: 'temporary' });
  });

  it('503 の reason が未知なら、一時的な不調にする', async () => {
    const result = await submitWith(503, { reason: 'somethingElse' });

    expect(result).toEqual({ accepted: false, unavailable: 'temporary' });
  });

  it.each([500, 502, 504, 599])('想定外の %i は「見つからない」にせず、一時的な不調にする', async (status) => {
    const result = await submitWith(status, { title: 'error' });

    expect(result).toEqual({ accepted: false, unavailable: 'temporary' });
    expect(result.rejection).toBeUndefined();
  });

  it('本文が JSON でない 500 も一時的な不調にする', async () => {
    const result = await submitWith(500);

    expect(result).toEqual({ accepted: false, unavailable: 'temporary' });
  });

  it('これまでの振り分けは変えない（404 は見つからない・403 は理由・429 は混雑・413 は大きすぎ）', async () => {
    expect((await submitWith(404, {})).rejection).toBe('notFound');
    expect((await submitWith(403, { reason: 'closed' })).rejection).toBe('closed');
    expect((await submitWith(403)).rejection).toBe('notFound');
    expect((await submitWith(429)).rejection).toBe('tooManyRequests');
    expect((await submitWith(413)).errors).toEqual({ '': ['TooLarge'] });
    expect((await submitWith(422, { errors: { q1: ['TooFewSelections'] } })).errors).toEqual({
      q1: ['TooFewSelections'],
    });
  });
});
