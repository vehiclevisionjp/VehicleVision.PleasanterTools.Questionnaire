import { ja, type MessageKey } from './i18n/messages';
import type {
  ConditionOperator,
  PageTransition,
  Page,
  Question,
  QuestionType,
  SurveyDefinition,
} from './types';

/**
 * 分岐の不備 1 件。
 *
 * **サーバの `FlowProblem` と同じ形**（`Core/Flow/SurveyFlowValidator.cs`）。
 * 公開の口が `flow` として返すものと、編集中にこの画面が出すものの両方に使う。
 *
 * **値が無い項目は `null` ではなく「無い」。** サーバは null のプロパティを
 * 落として返すので（`DefaultIgnoreCondition`）、`!== null` では守れない。
 */
export interface FlowProblem {
  code: string;
  pageId?: string | null;
  questionId?: string | null;
  detail?: string | null;
}

/**
 * 分岐の不備の文言の鍵。
 *
 * **サーバ側に符号が増えたときに落とさない。**
 * 鍵が無ければ符号そのものを出す（`null` を返す）。
 */
export function flowKey(code: string): MessageKey | null {
  const key = `flow.${code}`;
  return key in ja ? (key as MessageKey) : null;
}

/** 行き先を持てる形式か。**単一選択だけ**（`Question.CanCarryTransitions`）。 */
export function canCarryTransitions(type: QuestionType): boolean {
  return type === 'Radio' || type === 'Dropdown';
}

/** 選択肢に行き先を持っているか（`Question.HasChoiceTransitions`）。 */
export function hasChoiceTransitions(question: Question): boolean {
  return question.choices.some((choice) => (choice.next ?? null) !== null);
}

/**
 * 編集中の定義から不備を挙げる。
 *
 * **サーバの `SurveyFlowValidator` の写し。** 公開して初めて弾かれると作り直しになるので、
 * 編集中にも同じことを見せる（Issue #44）。
 *
 * **こちらは緩めない。** 画面で防ぎきれなかったものを拾うためのもので、
 * 最後に拒否するのはサーバ（`POST /api/admin/surveys/{id}/publish`）。
 */
export function validateFlow(definition: SurveyDefinition): FlowProblem[] {
  const problems: FlowProblem[] = [];

  const order = new Map(definition.pages.map((page, index) => [page.pageId, index]));

  validateTransitions(definition, order, problems);
  validateConditions(definition, problems);
  validateReachability(definition, problems);

  return problems;
}

// ---- 行き先 -----------------------------------------------------------------

function validateTransitions(
  definition: SurveyDefinition,
  order: Map<string, number>,
  problems: FlowProblem[],
): void {
  for (const page of definition.pages) {
    checkTransition(page.next, order, page.pageId, null, problems);

    const branching = page.questions.filter(hasChoiceTransitions);

    // **どれが勝つのかを利用者が決められない形にしない**
    if (branching.length > 1) {
      problems.push({
        code: 'MultipleBranchingQuestions',
        pageId: page.pageId,
        detail: branching.map((question) => question.questionId).join(' / '),
      });
    }

    for (const question of branching) {
      if (!canCarryTransitions(question.type)) {
        // **複数選べる設問では、どの選択肢の行き先を使うのか決まらない**
        problems.push({
          code: 'TransitionOnUnsupportedQuestion',
          pageId: page.pageId,
          questionId: question.questionId,
          detail: question.type,
        });
      }

      for (const choice of question.choices) {
        checkTransition(choice.next, order, page.pageId, question.questionId, problems);
      }
    }
  }
}

function checkTransition(
  transition: PageTransition | null | undefined,
  order: Map<string, number>,
  pageId: string,
  questionId: string | null,
  problems: FlowProblem[],
): void {
  const target = transition?.kind === 'Page' ? (transition.pageId ?? null) : null;
  if (target === null) {
    return;
  }

  const targetIndex = order.get(target);
  if (targetIndex === undefined) {
    problems.push({ code: 'UnknownPage', pageId, questionId, detail: target });
    return;
  }

  if (target === pageId) {
    problems.push({ code: 'SelfTransition', pageId, questionId, detail: target });
    return;
  }

  // **前を向いた行き先を許すと、無限に回るアンケートが作れる**
  if (targetIndex <= (order.get(pageId) ?? 0)) {
    problems.push({ code: 'BackwardTransition', pageId, questionId, detail: target });
  }
}

// ---- 表示条件 ---------------------------------------------------------------

function validateConditions(definition: SurveyDefinition, problems: FlowProblem[]): void {
  const byId = new Map<string, Question>();
  const position = new Map<string, number>();

  let index = 0;
  for (const page of definition.pages) {
    for (const question of page.questions) {
      if (!byId.has(question.questionId)) {
        byId.set(question.questionId, question);
      }

      position.set(question.questionId, index);
      index += 1;
    }
  }

  for (const page of definition.pages) {
    for (const question of page.questions) {
      for (const rule of question.visibleWhen?.rules ?? []) {
        const referenced = byId.get(rule.questionId);
        if (!referenced) {
          problems.push({
            code: 'UnknownConditionQuestion',
            pageId: page.pageId,
            questionId: question.questionId,
            detail: rule.questionId,
          });
          continue;
        }

        // **後ろを見る条件は成立しようがない。** その時点でまだ答えていない
        if ((position.get(rule.questionId) ?? 0) >= (position.get(question.questionId) ?? 0)) {
          problems.push({
            code: 'ForwardConditionReference',
            pageId: page.pageId,
            questionId: question.questionId,
            detail: rule.questionId,
          });
          continue;
        }

        // **無い選択肢を見ている条件は、永久に成立しない。**
        // 選択肢の値を変えたときの直し忘れがここで見つかる
        if (isUnknownChoiceValue(referenced, rule.operator, rule.value)) {
          problems.push({
            code: 'UnknownChoiceValue',
            pageId: page.pageId,
            questionId: question.questionId,
            detail: rule.value,
          });
        }
      }
    }
  }
}

/**
 * その条件が、参照先に無い選択肢を見ているか。
 *
 * **条件の行の脇にも同じことを出す**ので、判定を 1 か所にまとめてある。
 */
export function isUnknownChoiceValue(
  referenced: Question,
  operator: ConditionOperator,
  value: string | null | undefined,
): boolean {
  if (operator !== 'Equals' && operator !== 'NotEquals') {
    return false;
  }

  if (referenced.choices.length === 0) {
    return false;
  }

  return (
    (value ?? null) !== null && !referenced.choices.some((choice) => choice.value === value)
  );
}

// ---- 到達できるか -----------------------------------------------------------

function validateReachability(definition: SurveyDefinition, problems: FlowProblem[]): void {
  const first = definition.pages[0];
  if (!first) {
    return;
  }

  // **答え方に関わらず「行き得る」ページを広げる。**
  // 実際に行けるかは回答次第なので、**行き得ないものだけを不備とする**
  const reachable = new Set<string>([first.pageId]);
  const queue: Page[] = [first];

  const byId = new Map(definition.pages.map((page) => [page.pageId, page]));
  const order = new Map(definition.pages.map((page, index) => [page.pageId, index]));

  while (queue.length > 0) {
    const page = queue.shift()!;

    for (const next of destinations(page, definition, byId, order)) {
      if (!reachable.has(next.pageId)) {
        reachable.add(next.pageId);
        queue.push(next);
      }
    }
  }

  for (const page of definition.pages) {
    if (!reachable.has(page.pageId)) {
      problems.push({ code: 'UnreachablePage', pageId: page.pageId });
    }
  }
}

/** そのページから行き得る先。 */
function destinations(
  page: Page,
  definition: SurveyDefinition,
  byId: Map<string, Page>,
  order: Map<string, number>,
): Page[] {
  const transitions: (PageTransition | null)[] = page.questions
    .filter(hasChoiceTransitions)
    .flatMap((question) => question.choices)
    .map((choice) => choice.next ?? null);

  // **選択肢に行き先が無い道も残る。** そのときはページ末尾の行き先に落ちる
  const everyChoiceJumps =
    transitions.length > 0 && transitions.every((next) => next !== null);
  if (!everyChoiceJumps) {
    transitions.push(page.next ?? null);
  }

  const found: Page[] = [];

  for (const transition of transitions) {
    if (transition?.kind === 'Submit') {
      continue;
    }

    if (transition?.kind === 'Page' && (transition.pageId ?? null) !== null) {
      const target = byId.get(transition.pageId!);
      if (target) {
        found.push(target);
      }

      continue;
    }

    const index = (order.get(page.pageId) ?? 0) + 1;
    const following = definition.pages[index];
    if (following) {
      found.push(following);
    }
  }

  return found;
}

// ---- 選択欄との橋渡し -------------------------------------------------------

/**
 * 行き先を選択欄の値へ。
 *
 * **空文字は「行き先を持たない」。** 選択肢ではページ末尾の行き先に従い、
 * ページでは次のページへ進む。
 */
export function transitionValue(next: PageTransition | null | undefined): string {
  const value = next ?? null;
  if (value === null) {
    return '';
  }

  return value.kind === 'Page' ? `page:${value.pageId ?? ''}` : value.kind;
}

/** 選択欄の値から行き先へ。**空文字は「持たない」。** */
export function toTransition(value: string): PageTransition | null {
  if (value === '') {
    return null;
  }

  if (value.startsWith('page:')) {
    return { kind: 'Page', pageId: value.slice('page:'.length) };
  }

  return { kind: value === 'Submit' ? 'Submit' : 'Next' };
}

/**
 * 今は一覧に出せない飛び先の ID。無ければ `null`。
 *
 * **黙って消さない。** ページを消したり並べ替えたりすると、
 * 保存済みの飛び先が選べる一覧から外れる。そのまま選択欄を空に見せると、
 * 直したつもりが無いのに別の行き先へ変わってしまう。
 */
export function staleTargetId(
  next: PageTransition | null | undefined,
  allowed: readonly string[],
): string | null {
  if (next?.kind !== 'Page') {
    return null;
  }

  const pageId = next.pageId ?? '';
  return pageId !== '' && !allowed.includes(pageId) ? pageId : null;
}
