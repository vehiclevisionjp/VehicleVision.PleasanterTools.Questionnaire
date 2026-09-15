import { describe, expect, it } from 'vitest';
import {
  canCarryTransitions,
  flowKey,
  hasChoiceTransitions,
  isUnknownChoiceValue,
  staleTargetId,
  toTransition,
  transitionValue,
  validateFlow,
} from './flow';
import type { Choice, Page, Question, QuestionType, SurveyDefinition } from './types';

function question(
  questionId: string,
  overrides: Partial<Question> = {},
): Question {
  return {
    questionId,
    type: 'Radio' as QuestionType,
    title: { ja: questionId },
    isRequired: false,
    choices: [],
    settings: {},
    ...overrides,
  };
}

function choice(value: string, overrides: Partial<Choice> = {}): Choice {
  return { value, label: { ja: value }, ...overrides };
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

/** 出た不備の符号だけを取り出す。**並びまでは問わない。** */
function codes(survey: SurveyDefinition): string[] {
  return validateFlow(survey).map((problem) => problem.code);
}

describe('flowKey', () => {
  it('文言のある符号は鍵を返す', () => {
    expect(flowKey('UnknownPage')).toBe('flow.UnknownPage');
  });

  // **サーバに符号が増えても落とさない。** 鍵が無ければ符号をそのまま出す
  it('文言の無い符号は null', () => {
    expect(flowKey('MadeUpCode')).toBeNull();
  });
});

describe('canCarryTransitions', () => {
  it('1 つだけ選ぶ設問は行き先を持てる', () => {
    expect(canCarryTransitions('Radio')).toBe(true);
    expect(canCarryTransitions('Dropdown')).toBe(true);
  });

  it('それ以外は持てない', () => {
    for (const type of ['Checkbox', 'Text', 'Grid', 'Ranking'] as QuestionType[]) {
      expect(canCarryTransitions(type)).toBe(false);
    }
  });
});

describe('hasChoiceTransitions', () => {
  it('行き先を持つ選択肢が 1 つでもあれば真', () => {
    const target = question('q1', {
      choices: [choice('a'), choice('b', { next: { kind: 'Submit' } })],
    });

    expect(hasChoiceTransitions(target)).toBe(true);
  });

  it('どの選択肢も持たなければ偽', () => {
    expect(hasChoiceTransitions(question('q1', { choices: [choice('a')] }))).toBe(false);
  });

  // **`null` は「持たない」。** サーバは値の無い項目を落として返す
  it('行き先が null の選択肢は持たないとみなす', () => {
    expect(
      hasChoiceTransitions(question('q1', { choices: [choice('a', { next: null })] })),
    ).toBe(false);
  });
});

describe('validateFlow の行き先', () => {
  it('素直な定義に不備は出ない', () => {
    expect(codes(definition(page('p1'), page('p2')))).toEqual([]);
  });

  it('無いページへの行き先を挙げる', () => {
    const survey = definition(page('p1', { next: { kind: 'Page', pageId: 'p9' } }), page('p2'));

    expect(codes(survey)).toContain('UnknownPage');
  });

  it('自分自身への行き先を挙げる', () => {
    const survey = definition(page('p1', { next: { kind: 'Page', pageId: 'p1' } }), page('p2'));

    expect(codes(survey)).toContain('SelfTransition');
  });

  // **前を向いた行き先を許すと、無限に回るアンケートが作れる**
  it('前のページへ戻る行き先を挙げる', () => {
    const survey = definition(page('p1'), page('p2', { next: { kind: 'Page', pageId: 'p1' } }));

    expect(codes(survey)).toContain('BackwardTransition');
  });

  it('次へ・送信の行き先は不備にしない', () => {
    expect(codes(definition(page('p1', { next: { kind: 'Next' } }), page('p2')))).toEqual([]);
    expect(codes(definition(page('p1', { next: { kind: 'Submit' } })))).toEqual([]);
  });

  // **どれが勝つのかを利用者が決められない形にしない**
  it('1 ページに分岐する設問が 2 つあれば挙げる', () => {
    const branching = (id: string) =>
      question(id, { choices: [choice('a', { next: { kind: 'Submit' } })] });

    const survey = definition(
      page('p1', { questions: [branching('q1'), branching('q2')] }),
      page('p2'),
    );

    expect(codes(survey)).toContain('MultipleBranchingQuestions');
  });

  it('行き先を持てない形式の設問に行き先があれば挙げる', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', {
            type: 'Checkbox',
            choices: [choice('a', { next: { kind: 'Submit' } })],
          }),
        ],
      }),
      page('p2'),
    );

    expect(codes(survey)).toContain('TransitionOnUnsupportedQuestion');
  });

  it('選択肢の行き先が無いページを見ていれば挙げる', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', { choices: [choice('a', { next: { kind: 'Page', pageId: 'p9' } })] }),
        ],
      }),
      page('p2'),
    );

    expect(codes(survey)).toContain('UnknownPage');
  });
});

describe('validateFlow の表示条件', () => {
  it('無い設問を見ている条件を挙げる', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', {
            visibleWhen: { match: 'All', rules: [{ questionId: 'q9', operator: 'Answered' }] },
          }),
        ],
      }),
    );

    expect(codes(survey)).toContain('UnknownConditionQuestion');
  });

  // **後ろを見る条件は成立しようがない。** その時点でまだ答えていない
  it('自分より後ろの設問を見ている条件を挙げる', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', {
            visibleWhen: { match: 'All', rules: [{ questionId: 'q2', operator: 'Answered' }] },
          }),
          question('q2'),
        ],
      }),
    );

    expect(codes(survey)).toContain('ForwardConditionReference');
  });

  it('自分自身を見ている条件も挙げる', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', {
            visibleWhen: { match: 'All', rules: [{ questionId: 'q1', operator: 'Answered' }] },
          }),
        ],
      }),
    );

    expect(codes(survey)).toContain('ForwardConditionReference');
  });

  it('前の設問を見ている条件は不備にしない', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1'),
          question('q2', {
            visibleWhen: { match: 'All', rules: [{ questionId: 'q1', operator: 'Answered' }] },
          }),
        ],
      }),
    );

    expect(codes(survey)).toEqual([]);
  });

  it('無い選択肢を見ている条件を挙げる', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', { choices: [choice('a')] }),
          question('q2', {
            visibleWhen: {
              match: 'All',
              rules: [{ questionId: 'q1', operator: 'Equals', value: 'z' }],
            },
          }),
        ],
      }),
    );

    expect(codes(survey)).toContain('UnknownChoiceValue');
  });
});

describe('isUnknownChoiceValue', () => {
  const referenced = question('q1', { choices: [choice('a'), choice('b')] });

  it('無い値を見ていれば真', () => {
    expect(isUnknownChoiceValue(referenced, 'Equals', 'z')).toBe(true);
    expect(isUnknownChoiceValue(referenced, 'NotEquals', 'z')).toBe(true);
  });

  it('ある値なら偽', () => {
    expect(isUnknownChoiceValue(referenced, 'Equals', 'a')).toBe(false);
  });

  // **値を比べない演算子では、そもそも選択肢を見ていない**
  it('値を比べない演算子は偽', () => {
    expect(isUnknownChoiceValue(referenced, 'Answered', 'z')).toBe(false);
    expect(isUnknownChoiceValue(referenced, 'Contains', 'z')).toBe(false);
  });

  it('選択肢を持たない設問は偽', () => {
    expect(isUnknownChoiceValue(question('q1'), 'Equals', 'z')).toBe(false);
  });

  it('値が無ければ偽', () => {
    expect(isUnknownChoiceValue(referenced, 'Equals', null)).toBe(false);
    expect(isUnknownChoiceValue(referenced, 'Equals', undefined)).toBe(false);
  });
});

describe('validateFlow の到達性', () => {
  it('飛ばされたページを挙げる', () => {
    const survey = definition(
      page('p1', { next: { kind: 'Page', pageId: 'p3' } }),
      page('p2'),
      page('p3'),
    );

    expect(codes(survey)).toContain('UnreachablePage');
  });

  // **選択肢の一部だけが飛ぶなら、ページ末尾の行き先にも落ちる**
  it('一部の選択肢だけが飛ぶ場合は次のページも行き得るとみなす', () => {
    const survey = definition(
      page('p1', {
        questions: [
          question('q1', {
            choices: [choice('a', { next: { kind: 'Page', pageId: 'p3' } }), choice('b')],
          }),
        ],
      }),
      page('p2'),
      page('p3'),
    );

    expect(codes(survey)).toEqual([]);
  });

  it('ページが無ければ不備は出ない', () => {
    expect(codes(definition())).toEqual([]);
  });

  it('送信で終わる道しか無くても最初のページは到達できる', () => {
    expect(codes(definition(page('p1', { next: { kind: 'Submit' } })))).toEqual([]);
  });
});

describe('transitionValue と toTransition', () => {
  it('行き先が無ければ空文字', () => {
    expect(transitionValue(null)).toBe('');
    expect(transitionValue(undefined)).toBe('');
  });

  it('ページ指定は page: を付けた形', () => {
    expect(transitionValue({ kind: 'Page', pageId: 'p2' })).toBe('page:p2');
  });

  it('次へ・送信はその名前', () => {
    expect(transitionValue({ kind: 'Next' })).toBe('Next');
    expect(transitionValue({ kind: 'Submit' })).toBe('Submit');
  });

  it('空文字は行き先を持たない', () => {
    expect(toTransition('')).toBeNull();
  });

  it('往復しても同じ値になる', () => {
    for (const next of [
      { kind: 'Page' as const, pageId: 'p2' },
      { kind: 'Next' as const },
      { kind: 'Submit' as const },
    ]) {
      expect(toTransition(transitionValue(next))).toEqual(next);
    }
  });

  // **知らない値は「次へ」に倒す。** 保存済みの値が読めなくても画面を壊さない
  it('知らない値は次へとして読む', () => {
    expect(toTransition('なにか')).toEqual({ kind: 'Next' });
  });
});

describe('staleTargetId', () => {
  it('一覧に無い飛び先は識別子を返す', () => {
    expect(staleTargetId({ kind: 'Page', pageId: 'p9' }, ['p1', 'p2'])).toBe('p9');
  });

  it('一覧にある飛び先は null', () => {
    expect(staleTargetId({ kind: 'Page', pageId: 'p2' }, ['p1', 'p2'])).toBeNull();
  });

  it('ページ指定でなければ null', () => {
    expect(staleTargetId({ kind: 'Next' }, [])).toBeNull();
    expect(staleTargetId(null, [])).toBeNull();
    expect(staleTargetId(undefined, [])).toBeNull();
  });

  it('ページ指定なのに識別子が空なら null', () => {
    expect(staleTargetId({ kind: 'Page', pageId: '' }, ['p1'])).toBeNull();
  });
});
