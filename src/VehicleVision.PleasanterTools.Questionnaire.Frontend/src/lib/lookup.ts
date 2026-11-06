import { appUrl } from './basePath';
import { framedHeaders } from './framed';
import type { MessageKey } from './i18n/messages';
import type { AnswerState, LocalizedText, Question } from './types';

/**
 * ルックアップ項目の回答画面側の部品（Issue #871・仕様書 2・4 章）。
 *
 * **この画面側は体験のためだけのもの。** 何を返すか・何を許すかを決めるのはサーバ側で、
 * ここでは**サーバが返した値を、入力の規則を迂回せずに画面へ写す**ことだけを受け持つ。
 * 取得値はプレーンテキストとして扱い、HTML・記法として解釈しない。
 */

/** モーダルに出す列 1 つ（表示 ID と見出し。参照元の列名は来ない）。 */
export interface LookupPublicDisplayField {
  displayId: string;
  heading?: LocalizedText | null;
}

/** 回答画面へ渡るルックアップ定義（`LookupPublicDefinition`）。 */
export interface LookupPublicDefinition {
  lookupId: string;
  /** ボタンを置く起点項目。 */
  anchorQuestionId: string;
  buttonLabel?: LocalizedText | null;
  keyQuestionIds: string[];
  targetQuestionIds: string[];
  displayFields: LookupPublicDisplayField[];
}

/** 候補 1 件。`candidateKey` はその応答の中だけの識別子。 */
export interface LookupCandidate {
  candidateKey: string;
  /** 表示 ID → 値。 */
  display: Record<string, string | null>;
  /** 入力先の設問 ID → 値。**入れられる値だけ。空の取得値は含まれない。** */
  values: Record<string, string | null>;
}

/** 入力先に入れられなかった値の警告。値は持たない。 */
export interface LookupWarning {
  candidateKey: string;
  fieldId: string;
  code: string;
}

export type LookupOutcome = 'notFound' | 'single' | 'multiple';

export interface LookupResponse {
  version: number;
  outcome: LookupOutcome;
  candidates: LookupCandidate[];
  warnings: LookupWarning[];
}

/** 検索の失敗の種類。サーバの `code` と通信の失敗を、画面の扱いごとにまとめたもの。 */
export type LookupFailure =
  | 'invalidKeys'
  | 'sessionInvalid'
  | 'unavailable'
  | 'versionChanged'
  | 'closed'
  | 'tooMany'
  | 'tooLarge'
  | 'rateLimited'
  | 'sourceInvalid'
  | 'temporarilyUnavailable'
  | 'timeout'
  | 'network';

export type LookupSearchResult =
  | { ok: true; response: LookupResponse }
  | {
      ok: false;
      failure: LookupFailure;
      /** `invalidKeys` のとき、サーバが指した（定義にある）キー項目。 */
      fieldIds: string[];
      /** `429`・`503` の `Retry-After`（秒）。無ければ `null`。 */
      retryAfterSeconds: number | null;
    };

const CODE_TO_FAILURE: Record<string, LookupFailure> = {
  LookupInvalidKeys: 'invalidKeys',
  LookupSessionInvalid: 'sessionInvalid',
  LookupUnavailable: 'unavailable',
  LookupVersionChanged: 'versionChanged',
  LookupClosed: 'closed',
  LookupTooManyResults: 'tooMany',
  LookupRequestTooLarge: 'tooLarge',
  LookupRateLimited: 'rateLimited',
  LookupSourceInvalid: 'sourceInvalid',
  LookupTemporarilyUnavailable: 'temporarilyUnavailable',
  LookupTimeout: 'timeout',
};

/** `code` が無い・知らないときの、HTTP 状態からの見立て。 */
const STATUS_TO_FAILURE: Record<number, LookupFailure> = {
  400: 'invalidKeys',
  403: 'sessionInvalid',
  404: 'unavailable',
  409: 'closed',
  413: 'tooLarge',
  422: 'tooMany',
  429: 'rateLimited',
  502: 'sourceInvalid',
  503: 'temporarilyUnavailable',
  504: 'timeout',
};

/** サーバの `code`（無ければ HTTP 状態）を失敗の種類へ当てる。**知らないものは取得失敗。** */
export function failureFromResponse(code: string | undefined, status: number): LookupFailure {
  return (code ? CODE_TO_FAILURE[code] : undefined) ?? STATUS_TO_FAILURE[status] ?? 'sourceInvalid';
}

/** 失敗の種類に対応する文言。**サーバの本文・生の応答は出さない。** */
export const LOOKUP_FAILURE_MESSAGES: Record<LookupFailure, MessageKey> = {
  invalidKeys: 'lookup.invalidKeys',
  sessionInvalid: 'lookup.sessionExpired',
  unavailable: 'lookup.failed',
  versionChanged: 'lookup.versionChanged',
  closed: 'lookup.closed',
  tooMany: 'lookup.tooMany',
  tooLarge: 'lookup.requestTooLarge',
  rateLimited: 'lookup.rateLimited',
  sourceInvalid: 'lookup.failed',
  temporarilyUnavailable: 'lookup.failed',
  timeout: 'lookup.timeout',
  network: 'lookup.failed',
};

/** 画面の外から来た `lookups` を、使える形だけ取り出す。**形が違うものは捨てる。** */
export function parsePublicLookups(raw: unknown): LookupPublicDefinition[] {
  if (!Array.isArray(raw)) {
    return [];
  }

  const strings = (value: unknown): string[] | null =>
    Array.isArray(value) && value.every((item) => typeof item === 'string')
      ? (value as string[])
      : null;

  const result: LookupPublicDefinition[] = [];
  for (const item of raw as unknown[]) {
    if (typeof item !== 'object' || item === null) {
      continue;
    }

    const entry = item as Record<string, unknown>;
    const keys = strings(entry.keyQuestionIds);
    const targets = strings(entry.targetQuestionIds);
    if (
      typeof entry.lookupId !== 'string' ||
      typeof entry.anchorQuestionId !== 'string' ||
      keys === null ||
      keys.length === 0 ||
      targets === null
    ) {
      continue;
    }

    const displayFields = Array.isArray(entry.displayFields)
      ? (entry.displayFields as unknown[]).flatMap((field) => {
          const value = field as Record<string, unknown> | null;
          return value && typeof value.displayId === 'string'
            ? [
                {
                  displayId: value.displayId,
                  heading: (value.heading as LocalizedText | null | undefined) ?? null,
                },
              ]
            : [];
        })
      : [];

    result.push({
      lookupId: entry.lookupId,
      anchorQuestionId: entry.anchorQuestionId,
      buttonLabel: (entry.buttonLabel as LocalizedText | null | undefined) ?? null,
      keyQuestionIds: keys,
      targetQuestionIds: targets,
      displayFields,
    });
  }

  return result;
}

/** 回答の最初の値。**無ければ空文字。** */
function firstValue(answer: AnswerState | undefined): string {
  return answer?.values[0] ?? '';
}

/** 検索に送るキー値（前後の空白を除く）。 */
export function keyValues(
  lookup: LookupPublicDefinition,
  answers: Record<string, AnswerState | undefined>,
): Record<string, string> {
  return Object.fromEntries(
    lookup.keyQuestionIds.map((id) => [id, firstValue(answers[id]).trim()]),
  );
}

/** 入力が空のキー項目。**全キーがそろうまで検索させない。** */
export function missingKeyQuestionIds(
  lookup: LookupPublicDefinition,
  answers: Record<string, AnswerState | undefined>,
): string[] {
  const values = keyValues(lookup, answers);
  return lookup.keyQuestionIds.filter((id) => values[id] === '');
}

/**
 * キー値の組を 1 つの文字列にする。**要求時と今で同じかを比べるためだけに使う。**
 * 値の順序・区切りの曖昧さを避けるため、JSON の配列にする。
 */
export function keySnapshot(
  lookup: LookupPublicDefinition,
  answers: Record<string, AnswerState | undefined>,
): string {
  const values = keyValues(lookup, answers);
  return JSON.stringify(lookup.keyQuestionIds.map((id) => [id, values[id]]));
}

/**
 * 古い応答を適用しないための番号札（仕様書 2 章「応答競合」）。
 *
 * **後から始めた検索が必ず勝つ。** 遅れて届いた古い応答、キーを変えた後の応答、
 * 無効にした後の応答は `isCurrent` が偽になる。
 */
export class LookupRequestTracker {
  private latest = 0;

  /** 新しい検索を始める。以前の要求はすべて古くなる。 */
  begin(): number {
    this.latest += 1;
    return this.latest;
  }

  /** いま進行中の要求を、すべて古いものにする（キー変更・画面の離脱など）。 */
  invalidate(): void {
    this.latest += 1;
  }

  /** この番号が最新で、要求時のキーが今のキーと同じか。 */
  isCurrent(id: number, requestedSnapshot: string, currentSnapshot: string): boolean {
    return id === this.latest && requestedSnapshot === currentSnapshot;
  }
}

/** 取得値として使える値か。**null・空・空白だけは「取得値なし」で、入力先を変えない。** */
function usable(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.trim() !== '';
}

/** 入力先に、すでに入力があるか。 */
export function hasExistingInput(answer: AnswerState | undefined): boolean {
  return (answer?.values ?? []).some((value) => value.trim() !== '');
}

/** 反映の計画。 */
export interface LookupApplyPlan {
  /** 実際に書き換える入力先と値。 */
  changes: { questionId: string; value: string }[];
  /** 変えると既存の入力を上書きする入力先（確認が要る）。 */
  overwrites: string[];
  /** 入れられなかった入力先と理由。 */
  warnings: { questionId: string; reason: 'choiceUnavailable' | 'valueInvalid' }[];
}

/**
 * 選んだ候補を入力先へ写す計画を立てる（部分反映）。
 *
 * - **空の取得値は変えない**（既存の入力を保持）
 * - **出ていない入力先へは入れない。** 通常の入力制御（表示条件）を迂回しない
 * - 定義の入力先でない設問へは入れない
 * - 選択肢のある設問は、値が `Choice.Value` と一致するときだけ（表示名と照合しない）。
 *   **「その他」へ逃がさず、選択肢も足さない。** 合わない項目は変えず警告し、他は反映する
 * - サーバが警告にした入力先（値が来ない）も警告として拾う
 * - すでに同じ値なら変えない（確認も要らない）
 */
export function planApply(
  lookup: LookupPublicDefinition,
  candidate: LookupCandidate,
  warnings: LookupWarning[],
  answers: Record<string, AnswerState | undefined>,
  questions: ReadonlyMap<string, Question>,
  visible: ReadonlySet<string>,
): LookupApplyPlan {
  const plan: LookupApplyPlan = { changes: [], overwrites: [], warnings: [] };
  const targets = new Set(lookup.targetQuestionIds);

  const warn = (questionId: string, question: Question | undefined) => {
    if (plan.warnings.some((item) => item.questionId === questionId)) {
      return;
    }
    const hasChoice = question?.type === 'Radio' || question?.type === 'Dropdown';
    plan.warnings.push({ questionId, reason: hasChoice ? 'choiceUnavailable' : 'valueInvalid' });
  };

  for (const warning of warnings) {
    if (warning.candidateKey === candidate.candidateKey && targets.has(warning.fieldId)) {
      if (visible.has(warning.fieldId)) {
        warn(warning.fieldId, questions.get(warning.fieldId));
      }
    }
  }

  for (const [questionId, value] of Object.entries(candidate.values)) {
    if (!targets.has(questionId) || !usable(value) || !visible.has(questionId)) {
      continue;
    }

    const question = questions.get(questionId);
    if (!question) {
      continue;
    }

    if (
      (question.type === 'Radio' || question.type === 'Dropdown') &&
      !question.choices.some((choice) => choice.value === value)
    ) {
      warn(questionId, question);
      continue;
    }

    const current = answers[questionId];
    if (firstValue(current) === value && (current?.values.length ?? 0) === 1) {
      continue;
    }

    plan.changes.push({ questionId, value });
    if (hasExistingInput(current)) {
      plan.overwrites.push(questionId);
    }
  }

  return plan;
}

/** 入力先 1 つへ値を入れた回答。**他の中身（「その他」の記述など）は持ち越さない。** */
export function answerWithValue(value: string): AnswerState {
  return { values: [value], otherText: '' };
}

/**
 * ダイアログを閉じた後のフォーカスの戻し先。
 * **起点のボタンが無くなっていれば（表示条件で隠れた）、本文の先頭へ。**
 * 戻し先が見つからなければ何もしない。
 */
export function restoreFocus(
  button: { isConnected: boolean; focus(): void } | null | undefined,
  fallback: { focus(): void } | null | undefined,
): boolean {
  if (button?.isConnected) {
    button.focus();
    return true;
  }
  if (fallback) {
    fallback.focus();
    return true;
  }
  return false;
}

/** 検索用トークンの有効期間（30 分）より少し短く持つ。 */
const SESSION_REUSE_MS = 25 * 60 * 1000;

/** 通信の待ち時間。**サーバの外部検索（5 秒）より長く、無限には待たない。** */
const REQUEST_TIMEOUT_MS = 20_000;

/** 検索用トークンを取得する。**失敗したら `null`。**（長期保存しない。メモリだけ） */
export async function fetchLookupSession(publicId: string): Promise<string | null> {
  try {
    const response = await fetch(
      appUrl(`/api/forms/${encodeURIComponent(publicId)}/lookups/session`),
      {
        method: 'POST',
        headers: { accept: 'application/json', ...framedHeaders() },
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      },
    );
    if (!response.ok) {
      return null;
    }
    const body = (await response.json()) as { lookupSession?: unknown };
    return typeof body.lookupSession === 'string' ? body.lookupSession : null;
  } catch {
    return null;
  }
}

/** トークンを使い回すための入れ物。画面ごとに 1 つ。**どこにも保存しない。** */
export class LookupSessionHolder {
  private token: string | null = null;
  private issuedAt = 0;

  constructor(
    private readonly publicId: string,
    private readonly now: () => number = () => Date.now(),
    private readonly fetchSession: (publicId: string) => Promise<string | null> = fetchLookupSession,
  ) {}

  /** 期限内ならそのトークン、なければ取り直す。取れなければ `null`。 */
  async get(): Promise<string | null> {
    if (this.token !== null && this.now() - this.issuedAt < SESSION_REUSE_MS) {
      return this.token;
    }
    this.token = await this.fetchSession(this.publicId);
    this.issuedAt = this.now();
    return this.token;
  }

  /** トークンが断られたとき。次の検索で取り直す。 */
  discard(): void {
    this.token = null;
  }
}

/** サーバの検索応答を読む。形が違う・件数と区分が食い違うものは `null`。管理プレビューも使う。 */
export function parseLookupResponse(raw: unknown): LookupResponse | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }
  const body = raw as Record<string, unknown>;
  if (
    typeof body.version !== 'number' ||
    (body.outcome !== 'notFound' && body.outcome !== 'single' && body.outcome !== 'multiple') ||
    !Array.isArray(body.candidates)
  ) {
    return null;
  }

  const candidates: LookupCandidate[] = [];
  for (const item of body.candidates as unknown[]) {
    const candidate = item as Record<string, unknown> | null;
    if (
      !candidate ||
      typeof candidate.candidateKey !== 'string' ||
      typeof candidate.display !== 'object' ||
      typeof candidate.values !== 'object'
    ) {
      return null;
    }
    candidates.push({
      candidateKey: candidate.candidateKey,
      display: (candidate.display ?? {}) as Record<string, string | null>,
      values: (candidate.values ?? {}) as Record<string, string | null>,
    });
  }

  // 件数と区分が食い違う応答は使わない（「1 件」として自動適用しない）
  const expected =
    body.outcome === 'notFound'
      ? candidates.length === 0
      : body.outcome === 'single'
        ? candidates.length === 1
        : candidates.length >= 2;
  if (!expected) {
    return null;
  }

  const warnings = Array.isArray(body.warnings)
    ? (body.warnings as unknown[]).flatMap((entry) => {
        const warning = entry as Record<string, unknown> | null;
        return warning &&
          typeof warning.candidateKey === 'string' &&
          typeof warning.fieldId === 'string' &&
          typeof warning.code === 'string'
          ? [{ candidateKey: warning.candidateKey, fieldId: warning.fieldId, code: warning.code }]
          : [];
      })
    : [];

  return { version: body.version, outcome: body.outcome, candidates, warnings };
}

/**
 * 検索する。**キー値は本文で送る**（URL・クエリへ載せない）。
 * 失敗は例外にせず、画面の扱いごとの種類で返す。
 */
export async function searchLookup(
  publicId: string,
  lookupId: string,
  version: number,
  keys: Record<string, string>,
  lookupSession: string,
): Promise<LookupSearchResult> {
  const failure = (
    kind: LookupFailure,
    fieldIds: string[] = [],
    retryAfterSeconds: number | null = null,
  ): LookupSearchResult => ({ ok: false, failure: kind, fieldIds, retryAfterSeconds });

  let response: Response;
  try {
    response = await fetch(
      appUrl(
        `/api/forms/${encodeURIComponent(publicId)}/lookups/${encodeURIComponent(lookupId)}/search`,
      ),
      {
        method: 'POST',
        headers: {
          accept: 'application/json',
          'content-type': 'application/json',
          ...framedHeaders(),
        },
        body: JSON.stringify({ version, keys, lookupSession }),
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      },
    );
  } catch (error) {
    return failure(error instanceof DOMException && error.name === 'TimeoutError' ? 'timeout' : 'network');
  }

  if (response.ok) {
    const parsed = parseLookupResponse(await response.json().catch(() => null));
    return parsed ? { ok: true, response: parsed } : failure('sourceInvalid');
  }

  const body = (await response.json().catch(() => ({}))) as { code?: string; fieldIds?: unknown };
  const retry = Number.parseInt(response.headers.get('retry-after') ?? '', 10);
  return failure(
    failureFromResponse(body.code, response.status),
    Array.isArray(body.fieldIds) ? body.fieldIds.filter((id): id is string => typeof id === 'string') : [],
    Number.isFinite(retry) && retry > 0 ? retry : null,
  );
}
