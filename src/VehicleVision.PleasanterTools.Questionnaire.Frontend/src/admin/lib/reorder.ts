import type { SurveyDefinition } from './types';

/** 設問を挿入する位置。**設問を取り出した後のページ内の添字。** */
export interface QuestionDestination {
  pageId: string;
  questionIndex: number;
}

/**
 * 設問を別のページまたはページ内の位置へ移す。
 *
 * **ID と設問の内容は書き換えない。** 割り当て・表示条件は設問 ID を参照するため、
 * 並び順だけを替える。
 */
export function moveQuestionTo(
  definition: SurveyDefinition,
  questionId: string,
  destination: QuestionDestination,
): SurveyDefinition {
  const source = definition.pages
    .flatMap((page) => page.questions)
    .find((question) => question.questionId === questionId);
  if (!source || !definition.pages.some((page) => page.pageId === destination.pageId)) {
    return definition;
  }

  const withoutSource = definition.pages.map((page) => ({
    ...page,
    questions: page.questions.filter((question) => question.questionId !== questionId),
  }));

  return {
    ...definition,
    pages: withoutSource.map((page) =>
      page.pageId === destination.pageId
        ? {
            ...page,
            questions: insertAt(page.questions, source, destination.questionIndex),
          }
        : page,
    ),
  };
}

/** ページを指定した位置へ移す。**ページ ID と中の設問はそのまま保つ。** */
export function movePageTo(
  definition: SurveyDefinition,
  pageId: string,
  pageIndex: number,
): SurveyDefinition {
  const source = definition.pages.find((page) => page.pageId === pageId);
  if (!source) {
    return definition;
  }

  return {
    ...definition,
    pages: insertAt(
      definition.pages.filter((page) => page.pageId !== pageId),
      source,
      pageIndex,
    ),
  };
}

function insertAt<T>(items: readonly T[], item: T, index: number): T[] {
  const insertionIndex = Math.max(0, Math.min(index, items.length));
  return [...items.slice(0, insertionIndex), item, ...items.slice(insertionIndex)];
}
