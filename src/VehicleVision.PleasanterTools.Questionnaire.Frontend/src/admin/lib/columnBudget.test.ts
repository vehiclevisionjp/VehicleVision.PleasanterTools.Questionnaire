import { describe, expect, it } from 'vitest';
import { STANDARD_COLUMNS_PER_TYPE, measure, prefixOf, requiredPortCount } from './columnBudget';
import type {
  MappingDefinition,
  Page,
  Question,
  QuestionType,
  SurveyDefinition,
} from './types';

function assignment(targetColumn: string) {
  return {
    targetColumn,
    sources: [{ questionId: 'q1', port: 'Value' as const }],
  };
}

function mapping(...columns: string[]): MappingDefinition {
  return { assignments: columns.map(assignment) };
}

function question(questionId: string, type: QuestionType, rowIds: string[] = []): Question {
  return {
    questionId,
    type,
    title: { ja: questionId },
    isRequired: false,
    choices: [],
    settings: { rows: rowIds.map((rowId) => ({ rowId, label: { ja: rowId } })) },
  };
}

function definition(...questions: Question[]): SurveyDefinition {
  const page: Page = {
    pageId: 'p1',
    title: { ja: '1 ページ目' },
    questions,
  };

  return {
    surveyId: 's1',
    version: 1,
    title: { ja: '見本' },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: false,
    pages: [page],
  };
}

describe('prefixOf', () => {
  it('標準の列は末尾の 1 文字を落とす', () => {
    expect(prefixOf('ClassA')).toBe('Class');
    expect(prefixOf('NumZ')).toBe('Num');
  });

  it('項目拡張の列は末尾の数字を落とす', () => {
    expect(prefixOf('Class012')).toBe('Class');
    expect(prefixOf('Description9')).toBe('Description');
  });

  // **英字と数字の両方を落とさないと、`Class012` が `Clas` になって
  // `ClassA` と別の型として数えられる**（実装の警告のとおり）
  it('数字を落とした結果へさらに英字を落とさない', () => {
    expect(prefixOf('Class012')).toBe(prefixOf('ClassA'));
  });

  it('どちらでもなければそのまま返す', () => {
    expect(prefixOf('Title')).toBe('Title');
    expect(prefixOf('Body')).toBe('Body');
  });

  it('小文字 1 文字は落とさない', () => {
    expect(prefixOf('Classa')).toBe('Classa');
  });

  it('前後の空白は無視する', () => {
    expect(prefixOf('  ClassA  ')).toBe('Class');
  });

  it('空文字は空文字', () => {
    expect(prefixOf('')).toBe('');
    expect(prefixOf('   ')).toBe('');
  });

  // **1 文字だけの列名で空にしない。** 空の接頭辞で数えると全部が同じ型になる
  it('落とすと空になる場合は元の値を返す', () => {
    expect(prefixOf('A')).toBe('A');
    expect(prefixOf('12')).toBe('12');
  });
});

describe('measure', () => {
  it('型ごとに使っている本数を数える', () => {
    const usage = measure(mapping('ClassA', 'ClassB', 'NumA'));

    expect(usage).toEqual([
      { prefix: 'Class', used: 2, available: 26, remaining: 24, fits: true },
      { prefix: 'Num', used: 1, available: 26, remaining: 25, fits: true },
    ]);
  });

  it('同じ列を 2 回書いても 1 本として数える', () => {
    expect(measure(mapping('ClassA', 'ClassA')).at(0)?.used).toBe(1);
  });

  // **大文字と小文字は Pleasanter では同じ列。** 別に数えると足りるように見えてしまう
  it('大文字と小文字の違いは同じ列とみなす', () => {
    expect(measure(mapping('ClassA', 'classa')).at(0)?.used).toBe(1);
  });

  it('列名が空の割り当ては数えない', () => {
    expect(measure(mapping('', '   '))).toEqual([]);
  });

  it('接頭辞の順に並べる', () => {
    expect(measure(mapping('NumA', 'ClassA', 'DateA')).map((item) => item.prefix)).toEqual([
      'Class',
      'Date',
      'Num',
    ]);
  });

  it('上限ちょうどは収まっているとみなす', () => {
    const columns = [...Array(STANDARD_COLUMNS_PER_TYPE).keys()].map(
      (index) => `Class${index + 1}`,
    );

    const usage = measure(mapping(...columns)).at(0);
    expect(usage?.used).toBe(STANDARD_COLUMNS_PER_TYPE);
    expect(usage?.remaining).toBe(0);
    expect(usage?.fits).toBe(true);
  });

  it('上限を 1 本超えると収まっていないとみなす', () => {
    const columns = [...Array(STANDARD_COLUMNS_PER_TYPE + 1).keys()].map(
      (index) => `Class${index + 1}`,
    );

    const usage = measure(mapping(...columns)).at(0);
    expect(usage?.remaining).toBe(-1);
    expect(usage?.fits).toBe(false);
  });

  it('使える本数を指定できる', () => {
    const usage = measure(mapping('ClassA', 'ClassB'), 1).at(0);

    expect(usage?.available).toBe(1);
    expect(usage?.fits).toBe(false);
  });

  it('割り当てが無ければ空', () => {
    expect(measure(mapping())).toEqual([]);
  });
});

describe('requiredPortCount', () => {
  it('設問 1 つにつき 1 と数える', () => {
    expect(requiredPortCount(definition(question('q1', 'Text'), question('q2', 'Radio')))).toBe(2);
  });

  // **表示だけの要素は列を食わない**（保存先を持たない）
  it('表示専用の要素は数えない', () => {
    expect(requiredPortCount(definition(question('q1', 'Note'), question('q2', 'Text')))).toBe(1);
  });

  it('グリッドは行の数だけ数える', () => {
    expect(requiredPortCount(definition(question('q1', 'Grid', ['r1', 'r2', 'r3'])))).toBe(3);
  });

  // **行がまだ無くても 1 と数える。** これから足すものとして見せる
  it('行の無いグリッドも 1 と数える', () => {
    expect(requiredPortCount(definition(question('q1', 'Grid')))).toBe(1);
  });

  it('設問が無ければ 0', () => {
    expect(requiredPortCount(definition())).toBe(0);
  });
});
