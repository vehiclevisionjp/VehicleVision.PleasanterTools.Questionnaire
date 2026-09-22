import type { Choice, Page, PageTransition, SurveyDefinition } from './types';
import type { FlowProblem } from './flow';

export type FlowchartEdgeKind = 'Default' | 'Page' | 'Choice';

export interface FlowchartNode {
  id: string;
  kind: 'Page' | 'Submit';
  pageId?: string;
  title: string;
  questionCount: number;
  conditionalQuestionCount: number;
  problems: FlowProblem[];
}

export interface FlowchartEdge {
  id: string;
  sourceId: string;
  targetId: string;
  kind: FlowchartEdgeKind;
  label: string | null;
  missingTargetId: string | null;
}

export interface Flowchart {
  nodes: FlowchartNode[];
  edges: FlowchartEdge[];
}

export interface FlowchartLabels {
  pageTitle: (page: Page, index: number) => string;
  choiceLabel: (choice: Choice) => string;
  submitTitle: string;
  defaultLabel: string;
  pageLabel: string;
}

/**
 * 分岐の全体図に載せるノードと辺を組み立てる。
 *
 * **不備はここで判定しない。** 公開時と編集時の不備を同じ結果で表示するため、
 * 呼び出し元が受け取った `flowProblems` だけを重ねる。
 */
export function buildFlowchart(
  definition: SurveyDefinition,
  flowProblems: readonly FlowProblem[],
  labels: FlowchartLabels,
): Flowchart {
  const pageIds = new Set(definition.pages.map((page) => page.pageId));
  const nodes: FlowchartNode[] = definition.pages.map((page, index) => ({
    id: page.pageId,
    kind: 'Page' as const,
    pageId: page.pageId,
    title: labels.pageTitle(page, index),
    questionCount: page.questions.length,
    conditionalQuestionCount: page.questions.filter(
      (question) => (question.visibleWhen?.rules.length ?? 0) > 0,
    ).length,
    problems: flowProblems.filter((problem) => problem.pageId === page.pageId),
  }));

  nodes.push({
    id: 'submit',
    kind: 'Submit',
    title: labels.submitTitle,
    questionCount: 0,
    conditionalQuestionCount: 0,
    problems: [],
  });

  const edges: FlowchartEdge[] = [];

  definition.pages.forEach((page, pageIndex) => {
    const branchingChoices = page.questions
      .filter((question) => question.choices.some((choice) => choice.next !== null && choice.next !== undefined))
      .flatMap((question) => question.choices);
    const choicesWithTransitions = branchingChoices.filter(
      (choice) => choice.next !== null && choice.next !== undefined,
    );

    for (const [choiceIndex, choice] of choicesWithTransitions.entries()) {
      edges.push(
        edgeFor(
          `${page.pageId}-choice-${choiceIndex}`,
          page.pageId,
          choice.next!,
          'Choice',
          labels.choiceLabel(choice),
          pageIndex,
          definition.pages,
          pageIds,
        ),
      );
    }

    // **行き先を持たない選択肢が 1 つでもあれば、ページ末尾の行き先へ落ちる。**
    // 選択肢が全て行き先を持つときは、ページ末尾の行き先は実際には通らない。
    const followsPageDestination =
      choicesWithTransitions.length === 0 ||
      choicesWithTransitions.length < branchingChoices.length;

    if (followsPageDestination) {
      const pageTransition = page.next ?? { kind: 'Next' as const };
      const kind = pageTransition.kind === 'Next' ? 'Default' : 'Page';
      edges.push(
        edgeFor(
          `${page.pageId}-page`,
          page.pageId,
          pageTransition,
          kind,
          kind === 'Default' ? labels.defaultLabel : labels.pageLabel,
          pageIndex,
          definition.pages,
          pageIds,
        ),
      );
    }
  });

  return { nodes, edges };
}

function edgeFor(
  id: string,
  sourceId: string,
  transition: PageTransition,
  kind: FlowchartEdgeKind,
  label: string,
  sourceIndex: number,
  pages: readonly Page[],
  pageIds: ReadonlySet<string>,
): FlowchartEdge {
  const targetId =
    transition.kind === 'Submit'
      ? 'submit'
      : transition.kind === 'Page'
        ? (transition.pageId ?? '')
        : (pages[sourceIndex + 1]?.pageId ?? 'submit');

  return {
    id,
    sourceId,
    targetId,
    kind,
    label,
    missingTargetId: targetId !== 'submit' && !pageIds.has(targetId) ? targetId : null,
  };
}
