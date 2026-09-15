import type { Choice, Page, Question } from './types';

/**
 * 選択肢と設問の並べ替え（Issue #103）。
 *
 * **先に出た選択肢ほど選ばれやすい**（順序効果）。それを均すための機能。
 *
 * ⚠️ **並び順は 1 回の回答の中で固定する。** 都度並べ替えると、前のページへ戻っただけで
 * 選択肢の位置が変わり、選び直しを誘発する。そのため**乱数を毎回引かず、
 * 回答ごとに 1 つ決めた種から決定的に並びを導く。**
 * 画面の再描画（Svelte の `$derived`）は何度でも起きるので、
 * 「同じ入力なら同じ並び」でないと成り立たない。
 */

/** 32 ビットの決定的な擬似乱数。**暗号用途ではない**（並び順にしか使わない）。 */
function mulberry32(seed: number): () => number {
  let state = seed >>> 0;

  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** 文字列から 32 ビットの値を作る（FNV-1a）。**衝突しても並びが偏るだけ。** */
function hash(value: string): number {
  let result = 0x811c9dc5;

  for (let index = 0; index < value.length; index += 1) {
    result ^= value.charCodeAt(index);
    result = Math.imul(result, 0x01000193);
  }

  return result >>> 0;
}

/**
 * この回答ぶんの種を作る。**回答を始めるときに 1 度だけ呼ぶこと。**
 *
 * 予測されても困らない（並び順が漏れて不利益になる情報ではない）ので、
 * `crypto` ではなく `Math.random` で足りる。
 */
export function createShuffleSeed(): number {
  return Math.floor(Math.random() * 0xffffffff) >>> 0;
}

/** Fisher-Yates。**渡された配列は変えない。** */
function shuffled<T>(items: readonly T[], seed: number): T[] {
  const result = [...items];
  const random = mulberry32(seed);

  for (let index = result.length - 1; index > 0; index -= 1) {
    const target = Math.floor(random() * (index + 1));
    // **添字が範囲内なのは上の条件から明らか。** `noUncheckedIndexedAccess` を通すための断言
    const swapped = result[index] as T;
    result[index] = result[target] as T;
    result[target] = swapped;
  }

  return result;
}

/**
 * 設問ごとに別の種を使う。
 *
 * **同じ種を使い回すと、選択肢の数が同じ設問が全部同じ並びになる。**
 * 「1 問目と 2 問目がいつも同じ順」では、均したことにならない。
 */
function seedFor(seed: number, key: string): number {
  return (seed ^ hash(key)) >>> 0;
}

/**
 * 選択肢を並べ替える。**指定が無ければ元のまま返す。**
 *
 * ⚠️ **「その他」は動かさず末尾へ置く。** 途中に混ざると、自由記述の欄が
 * 並びの真ん中に現れて読みにくい。
 */
export function orderChoices(question: Question, seed: number): Choice[] {
  if (!question.settings.shuffleChoices) return question.choices;
  if (question.choices.length < 2) return question.choices;

  const others = question.choices.filter((choice) => choice.isOther);
  const rest = question.choices.filter((choice) => !choice.isOther);

  return [...shuffled(rest, seedFor(seed, question.questionId)), ...others];
}

/**
 * ページの中の設問を並べ替える。**指定が無ければ元のまま返す。**
 *
 * ⚠️ **説明文ブロックは動かさない。**「以下の設問について」のような前置きが、
 * 説明する対象から離れてしまう。**元の位置に留め、その間だけを入れ替える。**
 *
 * ⚠️ **出し分けの条件を持つ設問があるページでは並べ替えない。**
 * 条件が参照できるのは自分より前の設問だけなので、順序を崩すと成立しなくなる。
 * 公開時にも弾いているが、**古い版の定義が流れてきても壊れないように**ここでも見る。
 */
export function orderQuestions(page: Page, questions: Question[], seed: number): Question[] {
  if (!page.shuffleQuestions) return questions;
  if (questions.length < 2) return questions;
  if (questions.some((question) => question.visibleWhen)) return questions;

  const movable = questions.filter((question) => question.type !== 'Note');
  if (movable.length < 2) return questions;

  const order = shuffled(movable, seedFor(seed, page.pageId));
  let next = 0;

  // **説明文は元の位置に留め、その間だけを詰め替える。**
  // `order` は `movable` と同じ長さなので、`??` は成り立たない側の保険
  return questions.map((question) =>
    question.type === 'Note' ? question : (order[next++] ?? question),
  );
}

/** ページ 1 枚ぶんの並べ替えをまとめて掛ける。 */
export function applyShuffle(page: Page, questions: Question[], seed: number): Question[] {
  return orderQuestions(page, questions, seed).map((question) => {
    const choices = orderChoices(question, seed);
    return choices === question.choices ? question : { ...question, choices };
  });
}
