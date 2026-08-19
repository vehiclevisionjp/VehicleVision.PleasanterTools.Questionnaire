import type {
  AnswerState,
  ConditionRule,
  Page,
  Question,
  SurveyDefinition,
} from './types';

/**
 * 回答に応じて、辿るページと出す設問を決める。
 *
 * **正はサーバ側**（`Core/Flow/SurveyFlow.cs`）。ここはその写しで、
 * **画面を進めるためだけのもの**。受け付けるかどうかはサーバが決める。
 *
 * **写しである以上、規則を変えるときは両方直すこと。**
 * サーバだけ直すと、画面が出した設問がサーバに落とされて食い違う。
 * 逆に画面だけ直すと、隠したはずの設問の答えが保存される。
 *
 * **1 ページずつサーバへ聞きに行かないのは、入力を最後に 1 回だけ送るため**
 * （`_documents/画面設計.md` 1 章）。途中で送るとレコードが分裂する。
 */
export interface SurveyPath {
  /** 通るページ。**順番どおり。** */
  pages: Page[];
  /** 出す設問。 */
  visible: Set<string>;
}

/** 回答から経路を求める。 */
export function tracePath(
  definition: SurveyDefinition | undefined,
  answers: Record<string, AnswerState>,
): SurveyPath {
  const pages: Page[] = [];
  const visible = new Set<string>();

  if (!definition || definition.pages.length === 0) {
    return { pages, visible };
  }

  const byId = new Map(definition.pages.map((page) => [page.pageId, page]));
  const order = new Map(definition.pages.map((page, index) => [page.pageId, index]));

  // **同じページを 2 度通らない。** 検査を抜けた定義でも、ここで止まる
  const seen = new Set<string>();
  let current: Page | undefined = definition.pages[0];

  while (current && !seen.has(current.pageId)) {
    seen.add(current.pageId);
    pages.push(current);

    for (const question of current.questions) {
      if (isVisible(question, answers, visible)) {
        visible.add(question.questionId);
      }
    }

    current = nextPage(current, definition, byId, order, answers, visible);
  }

  return { pages, visible };
}

/** その設問を出すか。 */
function isVisible(
  question: Question,
  answers: Record<string, AnswerState>,
  visibleSoFar: Set<string>,
): boolean {
  const rules = question.visibleWhen?.rules ?? [];
  if (rules.length === 0) {
    return true;
  }

  const results = rules.map((rule) => matches(rule, answers, visibleSoFar));

  return question.visibleWhen?.match === 'Any'
    ? results.some((matched) => matched)
    : results.every((matched) => matched);
}

function matches(
  rule: ConditionRule,
  answers: Record<string, AnswerState>,
  visibleSoFar: Set<string>,
): boolean {
  // **通らなかったページ・出していない設問は「未回答」。**
  // 答えが残っていても、見せていない以上は無かったことにする
  const values = visibleSoFar.has(rule.questionId)
    ? (answers[rule.questionId]?.values ?? []).filter((value) => value.trim() !== '')
    : [];

  const answered = values.length > 0;

  switch (rule.operator) {
    case 'Answered':
      return answered;

    case 'NotAnswered':
      return !answered;

    case 'Equals':
      return answered && values.includes(rule.value ?? '');

    // **未回答は「等しくない」に含めない。** 含めると、
    // まだ答えていないだけの設問で条件が成立してしまう
    case 'NotEquals':
      return answered && !values.includes(rule.value ?? '');

    case 'Contains':
      return answered && values.some((value) => value.includes(rule.value ?? ''));

    case 'GreaterThan':
      return compare(values, rule.value) > 0;

    case 'LessThan':
      return compare(values, rule.value) < 0;

    default:
      return false;
  }
}

/** 数として比べる。**数で読めなければ成立しない。** */
function compare(values: string[], value: string | null | undefined): number {
  if (values.length === 0 || value === null || value === undefined) {
    return 0;
  }

  const left = Number(values[0]);
  const right = Number(value);

  if (!Number.isFinite(left) || !Number.isFinite(right)) {
    return 0;
  }

  return left === right ? 0 : left < right ? -1 : 1;
}

/** 次に進むページ。**無ければ送信へ。** */
function nextPage(
  current: Page,
  definition: SurveyDefinition,
  byId: Map<string, Page>,
  order: Map<string, number>,
  answers: Record<string, AnswerState>,
  visible: Set<string>,
): Page | undefined {
  const transition = choiceTransition(current, answers, visible) ?? current.next ?? undefined;

  if (transition?.kind === 'Submit') {
    return undefined;
  }

  if (transition?.kind === 'Page' && transition.pageId) {
    return byId.get(transition.pageId);
  }

  // **既定は次のページ。** 最後まで来たら送信へ
  const index = (order.get(current.pageId) ?? 0) + 1;
  return index < definition.pages.length ? definition.pages[index] : undefined;
}

/**
 * 選んだ選択肢が持つ行き先。
 *
 * **1 ページに行き先を持つ設問は 1 つだけ**（公開時に弾いている）。
 * 抜けた定義では、先に出てくるものを使う。
 */
function choiceTransition(
  page: Page,
  answers: Record<string, AnswerState>,
  visible: Set<string>,
) {
  for (const question of page.questions) {
    // **出していない設問の選択肢では飛ばさない**
    if (!visible.has(question.questionId)) {
      continue;
    }

    const value = (answers[question.questionId]?.values ?? [])[0];
    if (value === undefined || value === '') {
      continue;
    }

    const choice = question.choices.find((candidate) => candidate.value === value);
    if (choice?.next) {
      return choice.next;
    }
  }

  return undefined;
}

/**
 * 画面に出す 1 区切り。
 *
 * **1 問 1 ページ表示はアンケート全体の表示モード**
 * （`_documents/画面設計.md` 1 章）。設問側の設定ではない。
 * **分岐はページ単位のまま**で、その中を 1 問ずつ見せるだけ。
 */
export interface Step {
  page: Page;
  questions: Question[];
}

/** 経路を画面の区切りへ割る。 */
export function toSteps(
  path: SurveyPath,
  displayMode: SurveyDefinition['displayMode'],
): Step[] {
  const steps: Step[] = [];

  for (const page of path.pages) {
    const questions = page.questions.filter((question) => path.visible.has(question.questionId));

    if (displayMode !== 'OneQuestionPerPage') {
      // **設問が 1 つも出ないページは飛ばす。** 題名だけの空ページを見せない
      if (questions.length > 0) {
        steps.push({ page, questions });
      }

      continue;
    }

    for (const question of questions) {
      steps.push({ page, questions: [question] });
    }
  }

  return steps;
}
