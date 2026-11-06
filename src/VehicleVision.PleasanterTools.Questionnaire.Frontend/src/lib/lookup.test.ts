import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  answerWithValue,
  failureFromResponse,
  keySnapshot,
  keyValues,
  LookupRequestTracker,
  LookupSessionHolder,
  lookupWarningId,
  missingKeyQuestionIds,
  parsePublicLookups,
  planApply,
  restoreFocus,
  searchLookup,
  type LookupCandidate,
  type LookupPublicDefinition,
} from './lookup';
import type { AnswerState, Question } from './types';

afterEach(() => {
  vi.unstubAllGlobals();
});

const lookup: LookupPublicDefinition = {
  lookupId: 'l1',
  anchorQuestionId: 'a',
  keyQuestionIds: ['k1', 'k2'],
  targetQuestionIds: ['t1', 't2', 't3'],
  displayFields: [{ displayId: 'city' }],
};

function answer(...values: string[]): AnswerState {
  return { values, otherText: '' };
}

function question(id: string, type: Question['type'], choices: string[] = []): Question {
  return {
    questionId: id,
    type,
    title: { ja: id },
    isRequired: false,
    choices: choices.map((value) => ({ value, label: { ja: value }, isOther: false })),
    settings: {},
  };
}

const questions = new Map<string, Question>([
  ['t1', question('t1', 'Text')],
  ['t2', question('t2', 'Radio', ['東京都', '大阪府'])],
  ['t3', question('t3', 'Dropdown', ['a'])],
]);

function candidate(values: Record<string, string | null>): LookupCandidate {
  return { candidateKey: '1', display: {}, values };
}

const all = new Set(['t1', 't2', 't3']);

describe('失敗の種類', () => {
  it('code から種類を決める', () => {
    expect(failureFromResponse('LookupRateLimited', 429)).toBe('rateLimited');
    expect(failureFromResponse('LookupVersionChanged', 409)).toBe('versionChanged');
    expect(failureFromResponse('LookupClosed', 409)).toBe('closed');
    expect(failureFromResponse('LookupTooManyResults', 422)).toBe('tooMany');
    expect(failureFromResponse('LookupRequestTooLarge', 413)).toBe('tooLarge');
  });

  it('code が無ければ HTTP 状態から、知らなければ取得失敗にする', () => {
    expect(failureFromResponse(undefined, 504)).toBe('timeout');
    expect(failureFromResponse('Unknown', 418)).toBe('sourceInvalid');
  });
});

describe('公開用の定義の取り込み', () => {
  it('形の合うものだけ取り出し、参照元の構造は読まない', () => {
    const parsed = parsePublicLookups([
      {
        lookupId: 'l1',
        anchorQuestionId: 'a',
        keyQuestionIds: ['k1'],
        targetQuestionIds: ['t1'],
        displayFields: [{ displayId: 'city', heading: { ja: '市' } }, { nope: 1 }],
        sourceSiteId: 5,
      },
      { lookupId: 'bad' },
      null,
    ]);

    expect(parsed).toHaveLength(1);
    expect(parsed[0]).toEqual({
      lookupId: 'l1',
      anchorQuestionId: 'a',
      buttonLabel: null,
      keyQuestionIds: ['k1'],
      targetQuestionIds: ['t1'],
      displayFields: [{ displayId: 'city', heading: { ja: '市' } }],
    });
    expect('sourceSiteId' in parsed[0]!).toBe(false);
  });

  it('配列でなければ空、キーが無い定義は捨てる', () => {
    expect(parsePublicLookups(undefined)).toEqual([]);
    expect(
      parsePublicLookups([
        { lookupId: 'l', anchorQuestionId: 'a', keyQuestionIds: [], targetQuestionIds: [] },
      ]),
    ).toEqual([]);
  });
});

describe('キーの状態', () => {
  it('空・空白だけのキーは足りない扱い', () => {
    expect(missingKeyQuestionIds(lookup, { k1: answer('1000001'), k2: answer('  ') })).toEqual(['k2']);
    expect(missingKeyQuestionIds(lookup, {})).toEqual(['k1', 'k2']);
  });

  it('送るキー値は前後の空白を除き、先頭ゼロを保つ', () => {
    expect(keyValues(lookup, { k1: answer(' 0012 '), k2: answer('x') })).toEqual({
      k1: '0012',
      k2: 'x',
    });
  });

  it('キーが変われば写しが変わる', () => {
    const before = keySnapshot(lookup, { k1: answer('1'), k2: answer('2') });
    expect(keySnapshot(lookup, { k1: answer('1'), k2: answer('2') })).toBe(before);
    expect(keySnapshot(lookup, { k1: answer('1'), k2: answer('3') })).not.toBe(before);
  });
});

describe('応答の競合', () => {
  it('後から始めた検索だけが有効で、古い要求は捨てる', () => {
    const tracker = new LookupRequestTracker();
    const first = tracker.begin();
    const second = tracker.begin();

    expect(tracker.isCurrent(first, 's', 's')).toBe(false);
    expect(tracker.isCurrent(second, 's', 's')).toBe(true);
  });

  it('要求のあとでキーが変わったら無効', () => {
    const tracker = new LookupRequestTracker();
    const id = tracker.begin();

    expect(tracker.isCurrent(id, 'old', 'new')).toBe(false);
  });

  it('無効化すると進行中の要求も古くなる', () => {
    const tracker = new LookupRequestTracker();
    const id = tracker.begin();
    tracker.invalidate();

    expect(tracker.isCurrent(id, 's', 's')).toBe(false);
  });
});

describe('入力先への反映の計画', () => {
  it('空・null・空白の取得値は変えない', () => {
    const plan = planApply(
      lookup,
      candidate({ t1: '', t2: null, t3: '  ' }),
      [],
      { t1: answer('既存') },
      questions,
      all,
    );

    expect(plan.changes).toEqual([]);
    expect(plan.overwrites).toEqual([]);
  });

  it('既存入力を上書きするものを挙げる。同じ値は変えず確認も要らない', () => {
    const plan = planApply(
      lookup,
      candidate({ t1: '新', t2: '東京都' }),
      [],
      { t1: answer('旧'), t2: answer('東京都') },
      questions,
      all,
    );

    expect(plan.changes).toEqual([{ questionId: 't1', value: '新' }]);
    expect(plan.overwrites).toEqual(['t1']);
  });

  it('出ていない入力先へは入れない', () => {
    const plan = planApply(
      lookup,
      candidate({ t1: 'x', t2: '東京都' }),
      [],
      {},
      questions,
      new Set(['t2']),
    );

    expect(plan.changes).toEqual([{ questionId: 't2', value: '東京都' }]);
  });

  it('定義の入力先でない設問へは入れない', () => {
    const plan = planApply(lookup, candidate({ other: 'x' }), [], {}, questions, new Set(['other']));

    expect(plan.changes).toEqual([]);
  });

  it('選択肢にない値は変えず警告し、他は反映する（部分反映）', () => {
    const plan = planApply(
      lookup,
      candidate({ t1: '住所', t2: '北海道' }),
      [],
      { t2: answer('大阪府') },
      questions,
      all,
    );

    expect(plan.changes).toEqual([{ questionId: 't1', value: '住所' }]);
    expect(plan.warnings).toEqual([{ questionId: 't2', reason: 'choiceUnavailable' }]);
    expect(plan.overwrites).toEqual([]);
  });

  it('サーバの警告は、その候補の入力先だけ拾い、重複させない', () => {
    const plan = planApply(
      lookup,
      candidate({ t2: '北海道' }),
      [
        { candidateKey: '1', fieldId: 't1', code: 'ValueIncompatible' },
        { candidateKey: '2', fieldId: 't3', code: 'ValueIncompatible' },
        { candidateKey: '1', fieldId: 't2', code: 'ValueIncompatible' },
      ],
      {},
      questions,
      all,
    );

    expect(plan.warnings).toEqual([
      { questionId: 't1', reason: 'valueInvalid' },
      { questionId: 't2', reason: 'choiceUnavailable' },
    ]);
  });

  it('反映した値は「その他」の記述を持ち越さない', () => {
    expect(answerWithValue('x')).toEqual({ values: ['x'], otherText: '' });
  });
});

describe('フォーカスの戻し先', () => {
  it('起点のボタンが残っていればそこへ', () => {
    const button = { isConnected: true, focus: vi.fn() };
    const fallback = { focus: vi.fn() };

    expect(restoreFocus(button, fallback)).toBe(true);
    expect(button.focus).toHaveBeenCalledOnce();
    expect(fallback.focus).not.toHaveBeenCalled();
  });

  it('起点が無くなっていれば本文へ、どちらも無ければ何もしない', () => {
    const fallback = { focus: vi.fn() };

    expect(restoreFocus({ isConnected: false, focus: vi.fn() }, fallback)).toBe(true);
    expect(fallback.focus).toHaveBeenCalledOnce();
    expect(restoreFocus(null, null)).toBe(false);
  });
});

describe('検索用トークンの使い回し', () => {
  it('期限内は使い回し、断られたら取り直す', async () => {
    let now = 0;
    const fetchSession = vi.fn().mockResolvedValueOnce('t1').mockResolvedValueOnce('t2');
    const holder = new LookupSessionHolder('pub', () => now, fetchSession);

    expect(await holder.get()).toBe('t1');
    expect(await holder.get()).toBe('t1');
    expect(fetchSession).toHaveBeenCalledOnce();

    holder.discard();
    expect(await holder.get()).toBe('t2');

    now = 26 * 60 * 1000;
    fetchSession.mockResolvedValueOnce('t3');
    expect(await holder.get()).toBe('t3');
  });
});

describe('検索の呼び出し', () => {
  function stubFetch(response: unknown) {
    const fetch = vi.fn().mockResolvedValue(response);
    vi.stubGlobal('fetch', fetch);
    vi.stubGlobal('window', { self: 1, top: 1 });
    return fetch;
  }

  it('キーは本文で送り、URL に載せない', async () => {
    const fetch = stubFetch({
      ok: true,
      json: async () => ({
        version: 3,
        outcome: 'single',
        candidates: [{ candidateKey: '1', display: { city: '千代田区' }, values: { t1: '千代田区' } }],
        warnings: [],
      }),
    });

    const result = await searchLookup('pub-1', 'l1', 3, { k1: '1000001' }, 'token');

    expect(result.ok).toBe(true);
    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe('/api/forms/pub-1/lookups/l1/search');
    expect(String(url)).not.toContain('1000001');
    expect(JSON.parse(init.body)).toEqual({
      version: 3,
      keys: { k1: '1000001' },
      lookupSession: 'token',
    });
  });

  it('区分と件数が食い違う応答は使わない', async () => {
    stubFetch({
      ok: true,
      json: async () => ({
        version: 1,
        outcome: 'single',
        candidates: [
          { candidateKey: '1', display: {}, values: {} },
          { candidateKey: '2', display: {}, values: {} },
        ],
        warnings: [],
      }),
    });

    const result = await searchLookup('p', 'l', 1, { k1: 'x' }, 't');

    expect(result).toMatchObject({ ok: false, failure: 'sourceInvalid' });
  });

  it('429 は Retry-After を秒で返し、400 は定義にあるキー項目の ID を返す', async () => {
    stubFetch({
      ok: false,
      status: 429,
      headers: { get: () => '12' },
      json: async () => ({ code: 'LookupRateLimited' }),
    });
    expect(await searchLookup('p', 'l', 1, {}, 't')).toMatchObject({
      ok: false,
      failure: 'rateLimited',
      retryAfterSeconds: 12,
    });

    stubFetch({
      ok: false,
      status: 400,
      headers: { get: () => null },
      json: async () => ({ code: 'LookupInvalidKeys', fieldIds: ['k1', 5] }),
    });
    expect(await searchLookup('p', 'l', 1, {}, 't')).toMatchObject({
      failure: 'invalidKeys',
      fieldIds: ['k1'],
    });
  });

  it('通信の失敗は例外にせず network で返す', async () => {
    const fetch = vi.fn().mockRejectedValue(new TypeError('failed'));
    vi.stubGlobal('fetch', fetch);
    vi.stubGlobal('window', { self: 1, top: 1 });

    expect(await searchLookup('p', 'l', 1, {}, 't')).toMatchObject({ ok: false, failure: 'network' });
  });
});

describe('入力先への警告の要素 ID', () => {
  it('定義と入力先の組ごとに別の ID になる', () => {
    expect(lookupWarningId('l1', 't1')).toBe('lookup-warning-l1-t1');
    expect(lookupWarningId('l1', 't2')).not.toBe(lookupWarningId('l1', 't1'));
    expect(lookupWarningId('l2', 't1')).not.toBe(lookupWarningId('l1', 't1'));
  });
});
