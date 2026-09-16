import { describe, expect, it } from 'vitest';
import { addQuestionAssignment, includesQuestion } from './mappingSelection';
import type { MappingDefinition } from './types';

describe('addQuestionAssignment', () => {
  it('選んだ設問を入力にし、書き込み先を空にした割り当てを末尾へ足す', () => {
    const mapping: MappingDefinition = {
      assignments: [
        {
          targetColumn: 'Title',
          sources: [{ questionId: 'q1', port: 'Value' }],
        },
      ],
    };

    expect(addQuestionAssignment(mapping, 'q2')).toEqual({
      assignments: [
        {
          targetColumn: 'Title',
          sources: [{ questionId: 'q1', port: 'Value' }],
        },
        {
          targetColumn: '',
          sources: [{ questionId: 'q2', port: 'Value' }],
        },
      ],
    });
    expect(mapping.assignments).toHaveLength(1);
  });
});

describe('includesQuestion', () => {
  const assignment = {
    targetColumn: 'ClassA',
    sources: [
      { questionId: 'q1', port: 'Value' as const },
      { questionId: 'q2', port: 'Value' as const },
    ],
  };

  it('複数入力のどこかに選んだ設問があれば対応すると判定する', () => {
    expect(includesQuestion(assignment, 'q2')).toBe(true);
  });

  it('未選択または含まれない設問は対応しないと判定する', () => {
    expect(includesQuestion(assignment, null)).toBe(false);
    expect(includesQuestion(assignment, 'q3')).toBe(false);
  });
});
