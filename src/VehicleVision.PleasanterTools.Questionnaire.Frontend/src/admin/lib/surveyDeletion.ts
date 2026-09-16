import type { SurveySummary } from './types';

/** 完全削除の確認入力が、現在のアンケートの題名と一致するか。 */
export function canConfirmSurveyDeletion(survey: SurveySummary, enteredTitle: string): boolean {
  return survey.archivedAt != null && enteredTitle === survey.title;
}

/** 完全削除で本アプリから失われる回答の総数。 */
export function deletionResponseCount(survey: SurveySummary): number {
  return survey.responseCount + survey.testResponseCount;
}
