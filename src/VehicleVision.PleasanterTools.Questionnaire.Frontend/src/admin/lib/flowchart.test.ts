import { describe, expect, it } from 'vitest';
import { buildFlowchart, type FlowchartLabels } from './flowchart';
import type { Choice, Page, Question, SurveyDefinition } from './types';
import type { FlowProblem } from './flow';

const labels: FlowchartLabels = {
  pageTitle: (page) => page.title?.ja ?? page.pageId,
  choiceLabel: (choice) => choice.label.ja ?? choice.value,
  submitTitle: '送信',
  defaultLabel: '次のページへ',
  pageLabel: 'ページ末尾の行き先',
};

function choice(value: string, next?: Choice['next']): Choice {
  return { value, label: { ja: value }, next };
}

function question(questionId: string, choices: Choice[] = [], visible = false): Question {
  return {
    questionId,
    type: 'Radio',
    title: { ja: questionId },
    isRequired: false,
    choices,
    settings: {},
    visibleWhen: visible ? { match: 'All', rules: [{ questionId: 'q0', operator: 'Answered' }] } : null,
  };
}

function page(pageId: string, overrides: Partial<Page> = {}): Page {
  return { pageId, title: { ja: pageId }, questions: [], ...overrides };
}

function definition(...pages: Page[]): SurveyDefinition {
  return {
    surveyId: 's1',
    version: 1,
    title: { ja: '見本' },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: false,
    pages,
  };
}

function chart(pages: Page[], problems: FlowProblem[] = []) {
  return buildFlowchart(definition(...pages), problems, labels);
}

describe('buildFlowchart', () => {
  it('ページを定義順と送信ノードへ並べる', () => {
    const result = chart([page('p1'), page('p2')]);

    expect(result.nodes.map((node) => node.id)).toEqual(['p1', 'p2', 'submit']);
    expect(result.edges.slice(0, 2)).toMatchObject([
      { sourceId: 'p1', targetId: 'p2', kind: 'Default', label: '次のページへ' },
      { sourceId: 'p2', targetId: 'submit', kind: 'Default', label: '次のページへ' },
    ]);
  });

  it('ページ末尾と選択肢の行き先を別々の辺にする', () => {
    const result = chart([
      page('p1', {
        next: { kind: 'Submit' },
        questions: [
          question('q1', [
            choice('はい', { kind: 'Page', pageId: 'p3' }),
            choice('いいえ'),
          ]),
        ],
      }),
      page('p2'),
      page('p3'),
    ]);

    expect(result.edges.slice(0, 2)).toMatchObject([
      { sourceId: 'p1', targetId: 'p3', kind: 'Choice', label: 'はい' },
      { sourceId: 'p1', targetId: 'submit', kind: 'Page', label: 'ページ末尾の行き先' },
    ]);
  });

  it('全ての選択肢が分岐するときは通らないページ末尾の辺を作らない', () => {
    const result = chart([
      page('p1', {
        next: { kind: 'Submit' },
        questions: [question('q1', [choice('はい', { kind: 'Page', pageId: 'p2' })])],
      }),
      page('p2'),
    ]);

    expect(result.edges.filter((edge) => edge.sourceId === 'p1')).toHaveLength(1);
    expect(result.edges[0]!).toMatchObject({ kind: 'Choice', targetId: 'p2' });
  });

  it('設問数、表示条件を持つ設問数、不備をページのノードに重ねる', () => {
    const problem: FlowProblem = { code: 'UnreachablePage', pageId: 'p2' };
    const result = chart(
      [page('p1', { questions: [question('q1'), question('q2', [], true)] }), page('p2')],
      [problem],
    );

    expect(result.nodes[0]!).toMatchObject({ questionCount: 2, conditionalQuestionCount: 1 });
    expect(result.nodes[1]!.problems).toEqual([problem]);
  });

  it('存在しない飛び先の辺を図のデータに残す', () => {
    const result = chart(
      [page('p1', { next: { kind: 'Page', pageId: 'p9' } })],
      [{ code: 'UnknownPage', pageId: 'p1', detail: 'p9' }],
    );

    expect(result.edges).toContainEqual(
      expect.objectContaining({
        sourceId: 'p1',
        targetId: 'p9',
        kind: 'Page',
        missingTargetId: 'p9',
      }),
    );
    expect(result.nodes[0]!.problems[0]!.code).toBe('UnknownPage');
  });
});
