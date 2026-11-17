import { afterEach, describe, expect, it, vi } from 'vitest';
import { submitAnswers } from './api';
import { renewSubmitCredentials } from './submitCredentials';

afterEach(() => {
  vi.unstubAllGlobals();
});

const TOKEN = 'a'.repeat(32);

/**
 * サーバの振る舞いを写した偽物。
 *
 * - 送信（PUT）は、proof-of-work の解答を**受付の前に使い切る**（同じ解答は 2 度通らない）
 * - そのあと `outcomes` の順に、受付の結果（422・503・202 など）を返す
 */
function fakeServer(outcomes: number[]) {
  const usedSolutions = new Set<string>();
  let issued = 0;
  const calls: string[] = [];

  const fetch = vi.fn(async (input: string, init?: RequestInit) => {
    const url = String(input);

    if (url.endsWith('/ticket')) {
      issued += 1;
      calls.push('ticket');
      return {
        ok: true,
        json: async () => ({
          responseToken: TOKEN,
          ticket: `ticket-${issued}`,
          altcha: { algorithm: 'SHA-256', challenge: `c${issued}`, salt: 's', signature: 'g', maxnumber: 1 },
        }),
      };
    }

    const body = JSON.parse(String(init?.body)) as { altcha: string };
    if (usedSolutions.has(body.altcha) || body.altcha === '') {
      calls.push('PUT 403');
      return { status: 403, json: async () => ({ reason: 'rejected' }) };
    }
    usedSolutions.add(body.altcha);

    const status = outcomes.shift() ?? 202;
    calls.push(`PUT ${status}`);
    return {
      status,
      json: async () =>
        status === 422
          ? { attachments: [{ questionId: 'q1', fileName: 'memo.txt', reason: 'rejected' }] }
          : {},
    };
  });

  return { fetch, calls };
}

/** 解答は課題ごとに違う値にする（解けた印）。 */
const solve = async (challenge: { challenge: string }) => `solved-${challenge.challenge}`;

describe('renewSubmitCredentials', () => {
  it.each([422, 503])('%i のあとに取り直せば、次の送信は 1 回で通る', async (status) => {
    const server = fakeServer([status]);
    vi.stubGlobal('fetch', server.fetch);
    vi.stubGlobal('window', { localStorage: undefined });

    const first = await renewSubmitCredentials('pub-1', true, solve);
    expect(first).not.toBeNull();

    const rejected = await submitAnswers('pub-1', TOKEN, [], {
      ticket: first!.ticket,
      trap: '',
      altcha: first!.altcha,
    });
    expect(rejected.accepted).toBe(false);

    // 取り直さずに同じ解答で送ると、使用済みで 403 になる（Issue #959 の事象）
    const stale = await submitAnswers('pub-1', TOKEN, [], {
      ticket: first!.ticket,
      trap: '',
      altcha: first!.altcha,
    });
    expect(stale.rejection).toBe('rejected');

    // 取り直してから送ると通る
    const second = await renewSubmitCredentials('pub-1', true, solve);
    expect(second!.altcha).not.toBe(first!.altcha);
    const accepted = await submitAnswers('pub-1', TOKEN, [], {
      ticket: second!.ticket,
      trap: '',
      altcha: second!.altcha,
    });
    expect(accepted.accepted).toBe(true);
    expect(server.calls.filter((call) => call === 'PUT 403')).toHaveLength(1);
  });

  it('proof-of-work が要らなければ解かない', async () => {
    const server = fakeServer([]);
    vi.stubGlobal('fetch', server.fetch);
    vi.stubGlobal('window', { localStorage: undefined });
    const solver = vi.fn(solve);

    const fresh = await renewSubmitCredentials('pub-1', false, solver);

    expect(fresh?.altcha).toBe('');
    expect(solver).not.toHaveBeenCalled();
  });

  it('解けなければ解答は空にする', async () => {
    vi.stubGlobal('fetch', fakeServer([]).fetch);
    vi.stubGlobal('window', { localStorage: undefined });

    const fresh = await renewSubmitCredentials('pub-1', true, async () => null);

    expect(fresh?.altcha).toBe('');
  });

  it('通信が切れていれば例外を投げず null を返す', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('network')));
    vi.stubGlobal('window', { localStorage: undefined });

    expect(await renewSubmitCredentials('pub-1', true, solve)).toBeNull();
  });

  it('チケットを取れなければ null を返す', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
    vi.stubGlobal('window', { localStorage: undefined });

    expect(await renewSubmitCredentials('pub-1', true, solve)).toBeNull();
  });
});
