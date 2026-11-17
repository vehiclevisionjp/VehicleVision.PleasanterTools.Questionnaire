import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  loadForm,
  loadPendingAnswers,
  redeemAssetTicket,
  requestTicket,
  submitAnswers,
  type SubmitContext,
} from './api';

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

  it('503 の reason が maintenance なら、メンテナンスとして扱う（サーバの案内は使わない）', async () => {
    const result = await submitWith(503, { reason: 'maintenance', message: '本日 22 時まで停止します' });

    expect(result).toEqual({ accepted: false, unavailable: 'maintenance' });
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

describe('初回読み込みの応答の振り分け（Issue #965）', () => {
  /** 指定した状態コードと本文を返す fetch を差し込む。`body` が `undefined` なら JSON として読めない。 */
  function respondWith(status: number, body?: unknown) {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        status,
        ok: status >= 200 && status < 300,
        json: async () => {
          if (body === undefined) {
            throw new SyntaxError('Unexpected token < in JSON');
          }
          return body;
        },
      }),
    );
    // 枠内判定（`framedHeaders`）と Web Storage が `window` を読む。localStorage は使えない状況として扱う
    vi.stubGlobal('window', { localStorage: undefined });
  }

  function failToConnect() {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    vi.stubGlobal('window', { localStorage: undefined });
  }

  describe('loadForm', () => {
    it('200 ならアンケートを返す', async () => {
      respondWith(200, { publicId: 'pub-1' });

      expect(await loadForm('pub-1')).toEqual({ form: { publicId: 'pub-1' } });
    });

    it('404 だけが「見つからない」', async () => {
      respondWith(404);

      expect(await loadForm('pub-1')).toEqual({ rejection: 'notFound' });
    });

    it('403 は理由を使い、理由が無ければ「見つからない」', async () => {
      respondWith(403, { reason: 'closed' });
      expect(await loadForm('pub-1')).toEqual({ rejection: 'closed' });

      respondWith(403);
      expect(await loadForm('pub-1')).toEqual({ rejection: 'notFound' });
    });

    it.each([500, 502, 504, 599])('%i は「見つからない」にせず、一時的な失敗にする', async (status) => {
      respondWith(status, { title: 'error' });

      expect(await loadForm('pub-1')).toEqual({ unavailable: 'temporary' });
    });

    it('503 は reason が maintenance ならメンテナンス', async () => {
      respondWith(503, { reason: 'maintenance', message: '本日 22 時まで停止します' });

      expect(await loadForm('pub-1')).toEqual({ unavailable: 'maintenance' });
    });

    it('503 は reason が無い・未知・JSON でないなら一時的な失敗', async () => {
      respondWith(503, {});
      expect(await loadForm('pub-1')).toEqual({ unavailable: 'temporary' });

      respondWith(503, { reason: 'somethingElse' });
      expect(await loadForm('pub-1')).toEqual({ unavailable: 'temporary' });

      respondWith(503);
      expect(await loadForm('pub-1')).toEqual({ unavailable: 'temporary' });
    });

    it('通信できなければ一時的な失敗', async () => {
      failToConnect();

      expect(await loadForm('pub-1')).toEqual({ unavailable: 'temporary' });
    });
  });

  describe('requestTicket', () => {
    const ticket = { responseToken: 'r', ticket: 't' };

    it('200 ならチケットを返す', async () => {
      respondWith(200, ticket);

      expect(await requestTicket('pub-1')).toEqual({ ticket });
    });

    it.each([400, 404])('%i は恒久的な失敗（チケットも一時的な失敗も無い）', async (status) => {
      respondWith(status);

      expect(await requestTicket('pub-1')).toEqual({});
    });

    it.each([500, 502, 504, 429])('%i は「URL が正しくない」にせず、一時的な失敗にする', async (status) => {
      respondWith(status);

      expect(await requestTicket('pub-1')).toEqual({ unavailable: 'temporary' });
    });

    it('503 は reason でメンテナンスと一時的な失敗を分ける', async () => {
      respondWith(503, { reason: 'maintenance' });
      expect(await requestTicket('pub-1')).toEqual({ unavailable: 'maintenance' });

      respondWith(503, {});
      expect(await requestTicket('pub-1')).toEqual({ unavailable: 'temporary' });

      respondWith(503);
      expect(await requestTicket('pub-1')).toEqual({ unavailable: 'temporary' });
    });

    it('通信できなければ一時的な失敗', async () => {
      failToConnect();

      expect(await requestTicket('pub-1')).toEqual({ unavailable: 'temporary' });
    });
  });

  describe('loadPendingAnswers', () => {
    const answers = [{ questionId: 'q1', values: ['a'] }];

    it('200 なら回答を返す。回答が無ければ空', async () => {
      respondWith(200, { answers });
      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({ answers });

      respondWith(200, {});
      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({});
    });

    it('404 は「無い」。今は読めないとは区別する', async () => {
      respondWith(404);

      const result = await loadPendingAnswers('pub-1', 'token');

      expect(result).toEqual({});
      expect(result.unavailable).toBeUndefined();
    });

    it.each([500, 502, 504, 429])('%i は「無い」にせず、今は読めないにする', async (status) => {
      respondWith(status);

      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({ unavailable: 'temporary' });
    });

    it('503 は reason でメンテナンスと一時的な失敗を分ける', async () => {
      respondWith(503, { reason: 'maintenance' });
      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({ unavailable: 'maintenance' });

      // 読み出しの 503 は本文を持たない
      respondWith(503);
      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({ unavailable: 'temporary' });
    });

    it('200 でも本文が JSON でなければ、無いとは断定せず今は読めないにする', async () => {
      respondWith(200);

      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({ unavailable: 'temporary' });
    });

    it('通信できなければ今は読めない', async () => {
      failToConnect();

      expect(await loadPendingAnswers('pub-1', 'token')).toEqual({ unavailable: 'temporary' });
    });
  });
});
