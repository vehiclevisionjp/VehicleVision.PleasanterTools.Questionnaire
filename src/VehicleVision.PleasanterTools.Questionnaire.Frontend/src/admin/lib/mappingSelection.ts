import type { ColumnAssignment, MappingDefinition } from './types';

/** 書き込み先は利用者が決めるため、左で選んだ設問だけを入力へ設定する。 */
export function addQuestionAssignment(
  mapping: MappingDefinition,
  questionId: string,
): MappingDefinition {
  return {
    assignments: [
      ...mapping.assignments,
      {
        targetColumn: '',
        sources: [{ questionId, port: 'Value' }],
      },
    ],
  };
}

/** 左右の対応を色だけに頼らず示せるよう、選択した設問を入力から探す。 */
export function includesQuestion(assignment: ColumnAssignment, questionId: string | null): boolean {
  return (
    questionId !== null &&
    assignment.sources.some((source) => source.questionId === questionId)
  );
}
