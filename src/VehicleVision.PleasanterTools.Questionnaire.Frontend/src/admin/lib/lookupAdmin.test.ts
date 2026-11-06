import { describe, expect, it } from 'vitest';
import {
  deriveAllowedColumns,
  freeAnchorQuestions,
  inspectFailureKey,
  isCompatibleTarget,
  isKeyColumn,
  isValidDisplayId,
  keyKindOf,
  keyQuestionOptions,
  newLookup,
  nextDisplayId,
  previewDefinitions,
  problemMessageKey,
  targetQuestionOptions,
  toPreviewDefinition,
} from './lookupAdmin';
import type { LookupDefinition, LookupInspectColumn, Question, SurveyDefinition } from './types';

function q(questionId: string, type: Question['type']): Question {
  return { questionId, type, title: { ja: questionId }, isRequired: false, choices: [], settings: {} };
}

function survey(lookups: LookupDefinition[] | null = null): SurveyDefinition {
  return {
    surveyId: 's',
    version: 1,
    fallbackLanguage: 'ja',
    title: { ja: 't' },
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: false,
    lookups,
    pages: [
      { pageId: 'p1', questions: [q('a', 'Text'), q('k', 'Text'), q('r', 'Radio'), q('c', 'Checkbox')] },
      { pageId: 'p2', questions: [q('x', 'Text')] },
    ],
  };
}

const column = (over: Partial<LookupInspectColumn>): LookupInspectColumn => ({
  name: 'ClassA',
  kind: 'Text',
  hasChoices: false,
  nullable: false,
  ...over,
});

describe('設問の候補', () => {
  it('キーは同じページの 1 行テキストだけ', () => {
    expect(keyQuestionOptions(survey(), 'a').map((item) => item.questionId)).toEqual(['a', 'k']);
    expect(keyQuestionOptions(survey(), 'nope')).toEqual([]);
  });

  it('入力先は同じページのテキスト・単一選択・数値だけ（複数選択は対象外）', () => {
    expect(targetQuestionOptions(survey(), 'a').map((item) => item.questionId)).toEqual(['a', 'k', 'r']);
  });

  it('起点は使われていない設問だけ', () => {
    const used = survey([{ ...newLookup('a') }]);
    expect(freeAnchorQuestions(used).map((item) => item.questionId)).toEqual(['k', 'x']);
  });
});

describe('表示 ID と許可列', () => {
  it('表示 ID は自動採番で、使用済みを飛ばす', () => {
    expect(nextDisplayId([])).toBe('field1');
    expect(nextDisplayId(['field1', 'field3'])).toBe('field2');
    expect(isValidDisplayId(nextDisplayId(['field1']))).toBe(true);
  });

  it('表示 ID の形はサーバと同じ', () => {
    expect(isValidDisplayId('city')).toBe(true);
    expect(isValidDisplayId('1city')).toBe(false);
    expect(isValidDisplayId('a-b')).toBe(false);
    expect(isValidDisplayId('a'.repeat(33))).toBe(false);
  });

  it('許可列は入力元と表示列の和集合で、重複と空を除く', () => {
    const lookup: LookupDefinition = {
      ...newLookup('a'),
      targets: [
        { sourceColumn: 'ClassB', questionId: 'r' },
        { sourceColumn: 'ClassC', questionId: 'k' },
        { sourceColumn: '', questionId: 'x' },
      ],
      displayFields: [{ displayId: 'field1', sourceColumn: 'ClassB' }],
    };
    expect(deriveAllowedColumns(lookup)).toEqual(['ClassB', 'ClassC']);
  });
});

describe('列の見立て', () => {
  it('キーにできるのは選択肢のない文字列列と整数の数値列', () => {
    expect(isKeyColumn(column({}))).toBe(true);
    expect(isKeyColumn(column({ hasChoices: true }))).toBe(false);
    expect(isKeyColumn(column({ kind: 'Number', decimalPlaces: 0 }))).toBe(true);
    expect(isKeyColumn(column({ kind: 'Number', decimalPlaces: 2 }))).toBe(false);
    expect(isKeyColumn(column({ kind: 'Number' }))).toBe(false);
    expect(isKeyColumn(column({ kind: 'Date' }))).toBe(false);
    expect(keyKindOf(column({ kind: 'Number', decimalPlaces: 0 }))).toBe('Number');
    expect(keyKindOf(undefined)).toBeNull();
  });

  it('入力先の設問の形式に合う列', () => {
    expect(isCompatibleTarget('Text', column({ kind: 'Number' }))).toBe(true);
    expect(isCompatibleTarget('Radio', column({ kind: 'Number' }))).toBe(false);
    expect(isCompatibleTarget('Scale', column({ kind: 'Number' }))).toBe(true);
    expect(isCompatibleTarget('Checkbox', column({}))).toBe(false);
  });
});

describe('文言の鍵', () => {
  it('知らない不備・失敗は汎用の文言へ落ちる', () => {
    expect(problemMessageKey({ kind: 'column', code: 'ColumnNotFound' })).toBe('lookupAdmin.problem.ColumnNotFound');
    expect(problemMessageKey({ kind: 'column', code: 'Whatever' })).toBe('lookupAdmin.problem.unknown');
    expect(inspectFailureKey('Forbidden')).toBe('lookupAdmin.failure.forbidden');
    expect(inspectFailureKey('???')).toBe('lookupAdmin.failure.other');
  });
});

describe('管理プレビューの公開 DTO（回答画面のものとは別に下書きから作る）', () => {
  const lookup: LookupDefinition = {
    ...newLookup('a'),
    sourceSiteId: 5,
    credentialId: 'cred-secret-ref',
    buttonLabel: { ja: '検索' },
    keys: [{ questionId: 'k', sourceColumn: 'ClassA', sourceColumnKind: 'Text' }],
    targets: [{ sourceColumn: 'ClassB', questionId: 'r' }],
    displayFields: [{ displayId: 'city', sourceColumn: 'ClassB', heading: { ja: '市' } }],
  };

  it('操作に要る情報だけを持ち、参照元・資格情報・列名を持ち出さない', () => {
    const dto = toPreviewDefinition(lookup);
    const json = JSON.stringify(dto);

    expect(dto.keyQuestionIds).toEqual(['k']);
    expect(dto.targetQuestionIds).toEqual(['r']);
    expect(dto.displayFields).toEqual([{ displayId: 'city', heading: { ja: '市' } }]);
    expect(json).not.toContain('ClassA');
    expect(json).not.toContain('ClassB');
    expect(json).not.toContain('cred-secret-ref');
    expect(json).not.toContain('sourceSiteId');
  });

  it('キー・入力先がそろわない定義はプレビューに出さない', () => {
    expect(previewDefinitions(survey([lookup]))).toHaveLength(1);
    expect(previewDefinitions(survey([{ ...lookup, keys: [] }]))).toHaveLength(0);
    expect(previewDefinitions(survey([{ ...lookup, targets: [{ sourceColumn: 'B', questionId: 'gone' }] }]))).toHaveLength(0);
    expect(previewDefinitions(survey())).toEqual([]);
  });
});
