import type { AnswerState } from './types';

/**
 * 回答の下書きを、**端末の中だけ**に置く（Issue #59）。
 *
 * ⚠️ **サーバへは送らない**（2026-08-20 決定）。送ると、送信前の回答が
 * 完全匿名の前提のまま DB に溜まる。**下書きは「まだ受け付けていない回答」**で、
 * 回答者が消したくなったときに消せる場所へ置くのが筋。
 *
 * ⚠️ **アンケートごとに許可されたときだけ使う**（`FormResponse.allowsDraft`）。
 * 端末は共有され得る（店頭のタブレット、社内の共用 PC）。
 * **黙って残すと、次に使う人が前の人の回答を見る。**
 *
 * **添付は保存しない。** `File` は保存できないうえ、
 * 中身を端末へ写すのは残す量として重すぎる。
 */

/** 置き場所の接頭辞。**他の値と混ざらないように区切る。** */
const KEY_PREFIX = 'questionnaire.draft.';

/**
 * 下書きを残す日数。
 *
 * **7 日。** 「週をまたいで書き足す」までは拾い、
 * それ以上は本人も覚えていない。**古い回答を端末へ置き続けない。**
 */
export const DRAFT_MAX_AGE_DAYS = 7;

/** 保存する形。**添付は入らない。** */
interface StoredDraft {
  savedAt: string;
  answers: Record<string, { values: string[]; otherText: string; rows?: Record<string, string[]> }>;
}

function keyOf(publicId: string): string {
  return `${KEY_PREFIX}${publicId}`;
}

/**
 * Web Storage を使えるか。
 *
 * **使えないことがある。** プライベートウィンドウ、storage を切った設定、
 * 埋め込みの iframe。**そこで例外を投げると、回答そのものができなくなる。**
 */
function storage(): Storage | null {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

/**
 * 下書きを書く。
 *
 * **失敗しても黙って諦める。** 下書きは補助であって、
 * 書けないことを理由に回答を止めない（容量超過はここで起きる）。
 */
export function saveDraft(publicId: string, answers: Record<string, AnswerState>): void {
  const store = storage();
  if (store === null) return;

  const kept: StoredDraft['answers'] = {};

  for (const [questionId, answer] of Object.entries(answers)) {
    const values = answer.values.filter((value) => value.trim() !== '');
    const rows = compactRows(answer.rows);
    const otherText = answer.otherText.trim();

    // **空の設問は書かない。** 開いただけの状態を「下書きあり」にしない
    if (values.length === 0 && rows === undefined && otherText === '') {
      continue;
    }

    kept[questionId] = { values: answer.values, otherText: answer.otherText, rows };
  }

  if (Object.keys(kept).length === 0) {
    // **何も書いていないなら、置いてある下書きも消す。**
    // 全部消した人の端末に前の内容が残らないようにする
    clearDraft(publicId);
    return;
  }

  try {
    const draft: StoredDraft = { savedAt: new Date().toISOString(), answers: kept };
    store.setItem(keyOf(publicId), JSON.stringify(draft));
  } catch {
    // 容量超過など。**握り潰す**（上の注記の通り）
  }
}

/**
 * 下書きを読む。**無い・壊れている・期限切れなら `null`。**
 *
 * **期限切れはその場で消す。** 読めない値を端末へ残し続けない。
 */
export function readDraft(
  publicId: string,
  maxAgeDays: number = DRAFT_MAX_AGE_DAYS,
): Record<string, AnswerState> | null {
  const store = storage();
  if (store === null) return null;

  let raw: string | null = null;
  try {
    raw = store.getItem(keyOf(publicId));
  } catch {
    return null;
  }

  if (raw === null) return null;

  let draft: StoredDraft;
  try {
    draft = JSON.parse(raw) as StoredDraft;
  } catch {
    // **壊れた値は消す。** 読めないものを置いておく理由が無い
    clearDraft(publicId);
    return null;
  }

  const savedAt = Date.parse(draft?.savedAt ?? '');
  if (Number.isNaN(savedAt)) {
    clearDraft(publicId);
    return null;
  }

  if (Date.now() - savedAt > maxAgeDays * 24 * 60 * 60 * 1000) {
    clearDraft(publicId);
    return null;
  }

  const answers: Record<string, AnswerState> = {};
  for (const [questionId, answer] of Object.entries(draft.answers ?? {})) {
    answers[questionId] = {
      // **形が違う値は捨てる。** 端末の中身は誰でも書き換えられる
      values: Array.isArray(answer?.values) ? answer.values.filter(isText) : [],
      otherText: typeof answer?.otherText === 'string' ? answer.otherText : '',
      rows: readRows(answer?.rows),
    };
  }

  return Object.keys(answers).length === 0 ? null : answers;
}

/** 下書きがあるか。**中身は読まない。** */
export function hasDraft(publicId: string, maxAgeDays: number = DRAFT_MAX_AGE_DAYS): boolean {
  return readDraft(publicId, maxAgeDays) !== null;
}

/** 下書きを消す。**送信できたときと、回答者が消したときに呼ぶ。** */
export function clearDraft(publicId: string): void {
  try {
    storage()?.removeItem(keyOf(publicId));
  } catch {
    // 消せなくても続ける
  }
}

function isText(value: unknown): value is string {
  return typeof value === 'string';
}

/** 何も選んでいない行を落とす。**全部空なら行そのものを持たない。** */
function compactRows(
  rows: Record<string, string[]> | undefined,
): Record<string, string[]> | undefined {
  if (rows === undefined) return undefined;

  const kept = Object.entries(rows).filter(([, values]) =>
    values.some((value) => value.trim() !== ''),
  );

  return kept.length === 0 ? undefined : Object.fromEntries(kept);
}

/** 端末から読んだ行を、形を確かめて戻す。 */
function readRows(rows: unknown): Record<string, string[]> | undefined {
  if (rows === null || typeof rows !== 'object') return undefined;

  const kept: Record<string, string[]> = {};
  for (const [rowId, values] of Object.entries(rows as Record<string, unknown>)) {
    if (Array.isArray(values)) {
      kept[rowId] = values.filter(isText);
    }
  }

  return Object.keys(kept).length === 0 ? undefined : kept;
}
