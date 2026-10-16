import type { Question } from './types';

/**
 * 設問単位で保存済みの内容と異なるかを返す。
 *
 * 保存済みの設問が無ければ、新規追加された未保存の設問として扱う。
 */
export function hasUnsavedQuestion(
  question: Question,
  savedQuestion: Question | undefined,
): boolean {
  return savedQuestion === undefined || JSON.stringify(question) !== JSON.stringify(savedQuestion);
}
