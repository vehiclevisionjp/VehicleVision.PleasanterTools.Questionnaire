<script lang="ts">
  import { displayText, type SurveyDefinition } from '../lib/types';
  import { buildFlowchart } from '../lib/flowchart';
  import { flowKey, type FlowProblem } from '../lib/flow';
  import { t } from '../lib/i18n/state.svelte';
  import type { Language } from '../../lib/i18n/language';

  interface Props {
    definition: SurveyDefinition;
    flowProblems: readonly FlowProblem[];
    language: Language;
    onclose: () => void;
    onpage: (pageId: string) => void;
  }

  let { definition, flowProblems, language, onclose, onpage }: Props = $props();

  const nodeHeight = 120;
  const nodeWidth = 360;
  const nodeX = 110;
  const rowHeight = 180;
  const nodeY = 30;
  const edgeLabelX = nodeX + nodeWidth + 18;

  const chart = $derived(
    buildFlowchart(definition, flowProblems, {
      pageTitle: (page, index) =>
        displayText(page.title, language) || t('branching.pageNumber', { number: index + 1 }),
      choiceLabel: (choice) => displayText(choice.label, language) || choice.value,
      submitTitle: t('flowchart.submit'),
      defaultLabel: t('flowchart.defaultEdge'),
      pageLabel: t('flowchart.pageEdge'),
    }),
  );
  const nodeById = $derived(new Map(chart.nodes.map((node) => [node.id, node])));
  const diagramWidth = $derived(
    Math.max(
      580,
      edgeLabelX +
        Math.max(
          ...chart.edges.map((edge) =>
            edge.missingTargetId ? `! ${destination(edge.id)}`.length : short(edge.label ?? '').length,
          ),
          0,
        ) *
          12 +
        30,
    ),
  );
  const diagramHeight = $derived(Math.max(220, nodeY + chart.nodes.length * rowHeight));

  function position(nodeId: string) {
    const index = chart.nodes.findIndex((node) => node.id === nodeId);
    return { x: nodeX, y: nodeY + Math.max(index, 0) * rowHeight };
  }

  function edgePath(sourceId: string, targetId: string, edgeIndex: number): string {
    const source = position(sourceId);
    const target = position(targetId);
    const offset = ((edgeIndex % 5) - 2) * 12;
    const startX = source.x + nodeWidth / 2 + offset;
    const startY = source.y + nodeHeight;
    const endX = target.x + nodeWidth / 2 + offset;
    const endY = target.y;
    const bendY = Math.max(startY + 28, (startY + endY) / 2);
    return `M ${startX} ${startY} C ${startX} ${bendY}, ${endX} ${bendY}, ${endX} ${endY}`;
  }

  function missingEdgePath(sourceId: string, edgeIndex: number): string {
    const source = position(sourceId);
    const offset = edgeOffset(sourceId, edgeIndex);
    const startX = source.x + nodeWidth / 2 + offset;
    const startY = source.y + nodeHeight;
    const endX = nodeX + nodeWidth + 58;
    const endY = startY + 42;
    return `M ${startX} ${startY} C ${startX} ${endY}, ${endX - 24} ${endY}, ${endX} ${endY}`;
  }

  function edgeLabelY(sourceId: string, targetId: string, edgeIndex: number): number {
    const source = position(sourceId);
    const target = position(targetId);
    return (
      Math.max(source.y + nodeHeight + 16, (source.y + nodeHeight + target.y) / 2) +
      edgeOffset(sourceId, edgeIndex)
    );
  }

  function edgeOffset(sourceId: string, edgeIndex: number): number {
    const indexAtSource = chart.edges
      .slice(0, edgeIndex)
      .filter((edge) => edge.sourceId === sourceId).length;
    return (indexAtSource - 2) * 14;
  }

  function short(label: string): string {
    return label.length > 24 ? `${label.slice(0, 24)}…` : label;
  }

  function problemText(problem: FlowProblem): string {
    const key = flowKey(problem.code);
    return key ? t(key) : problem.code;
  }

  function destination(edgeId: string): string {
    const edge = chart.edges.find((candidate) => candidate.id === edgeId);
    if (!edge) return '';
    if (edge.missingTargetId) {
      return t('flowchart.missingDestination', { pageId: edge.missingTargetId });
    }
    return nodeById.get(edge.targetId)?.title ?? edge.targetId;
  }

  function activate(nodeId: string) {
    const node = nodeById.get(nodeId);
    if (node?.kind === 'Page' && node.pageId) {
      onpage(node.pageId);
    }
  }

</script>

<section class="flowchart">
  <header class="head">
    <button type="button" class="secondary" onclick={onclose}>{t('flowchart.close')}</button>
    <h1>{t('flowchart.title')}</h1>
  </header>

  <p class="lead">{t('flowchart.lead')}</p>

  {#if definition.pages.length === 0}
    <p class="empty">{t('flowchart.empty')}</p>
  {:else}
    <div class="diagram-wrap">
      <svg
        class="diagram"
        viewBox={`0 0 ${diagramWidth} ${diagramHeight}`}
        role="img"
        aria-label={t('flowchart.diagramLabel')}
      >
        <defs>
          <marker id="flowchart-arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
            <path d="M 0 0 L 8 4 L 0 8 z" fill="#475467"></path>
          </marker>
        </defs>

        {#each chart.edges as edge, edgeIndex (edge.id)}
          {#if edge.missingTargetId === null}
            <path
              class:default-edge={edge.kind === 'Default'}
              class:page-edge={edge.kind === 'Page'}
              class:choice-edge={edge.kind === 'Choice'}
              class="edge"
              d={edgePath(edge.sourceId, edge.targetId, edgeIndex)}
              marker-end="url(#flowchart-arrow)"
            >
              <title>{`${edge.label ?? ''}: ${destination(edge.id)}`}</title>
            </path>
            <text class="edge-label" x={edgeLabelX} y={edgeLabelY(edge.sourceId, edge.targetId, edgeIndex)}>
              {short(edge.label ?? '')}
            </text>
          {:else}
            <path
              class="edge missing-edge"
              d={missingEdgePath(edge.sourceId, edgeIndex)}
            >
              <title>{`${edge.label ?? ''}: ${destination(edge.id)}`}</title>
            </path>
            <text
              class="missing-label"
              x={edgeLabelX}
              y={position(edge.sourceId).y + nodeHeight + 48 + edgeOffset(edge.sourceId, edgeIndex)}
            >
              ! {destination(edge.id)}
            </text>
          {/if}
        {/each}

        {#each chart.nodes as node (node.id)}
          {@const point = position(node.id)}
          {#if node.kind === 'Page'}
            <a
              class:problem-node={node.problems.length > 0}
              class:unreachable-node={node.problems.some((problem) => problem.code === 'UnreachablePage')}
              class="node page-node"
              href={`#page-${node.pageId}`}
              aria-label={t('flowchart.editPage', { page: node.title })}
              onclick={(event) => {
                event.preventDefault();
                activate(node.id);
              }}
            >
              <g transform={`translate(${point.x} ${point.y})`}>
                <rect width={nodeWidth} height={nodeHeight} rx="8"></rect>
                <text class="node-title" x="16" y="30">{short(node.title)}</text>
                <text class="node-detail" x="16" y="56">
                  {t('flowchart.questions', { count: node.questionCount })}
                </text>
                <text class="node-detail" x="16" y="78">
                  {t('flowchart.conditions', { count: node.conditionalQuestionCount })}
                </text>
                {#if node.problems.length > 0}
                  <text class="problem-mark" x="16" y="102">
                    ! {t('flowchart.problems', { count: node.problems.length })}
                  </text>
                {/if}
              </g>
            </a>
          {:else}
            <g class="node submit-node" transform={`translate(${point.x} ${point.y})`}>
              <rect width={nodeWidth} height={nodeHeight} rx="8"></rect>
              <text class="node-title" x="16" y="30">{short(node.title)}</text>
            </g>
          {/if}
        {/each}
      </svg>
    </div>

    <section class="destinations" aria-labelledby="flowchart-destinations">
      <h2 id="flowchart-destinations">{t('flowchart.destinations')}</h2>
      <ul>
        {#each chart.nodes.filter((node) => node.kind === 'Page') as node (node.id)}
          {@const outgoing = chart.edges.filter((edge) => edge.sourceId === node.id)}
          <li>
            <strong>{node.title}</strong>
            {#if outgoing.length === 0}
              <span> — {t('flowchart.noDestination')}</span>
            {:else}
              <ul>
                {#each outgoing as edge (edge.id)}
                  <li>{edge.label}: {destination(edge.id)}</li>
                {/each}
              </ul>
            {/if}
            {#if node.problems.length > 0}
              <p class="problem-text">
                ! {node.problems.map(problemText).join(' / ')}
              </p>
            {/if}
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</section>

<style lang="scss">
  .head {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  h1 {
    margin: 0;
    font-size: 1.25rem;
  }

  .lead,
  .empty {
    color: var(--muted);
  }

  .diagram-wrap {
    max-height: 70vh;
    overflow: auto;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: #f8fafc;
  }

  .diagram {
    display: block;
    min-width: 36rem;
    width: 100%;
  }

  .edge {
    fill: none;
    stroke-width: 2;
  }

  .default-edge {
    stroke: #475467;
  }

  .page-edge {
    stroke: #175cd3;
  }

  .choice-edge {
    stroke: #067647;
  }

  .missing-edge {
    stroke: #b42318;
    stroke-dasharray: 7 4;
  }

  .edge-label {
    fill: #344054;
    font-size: 12px;
  }

  .missing-label {
    fill: #b42318;
    font-size: 12px;
    font-weight: 700;
  }

  .node rect {
    fill: #fff;
    stroke: #98a2b3;
    stroke-width: 2;
  }

  .page-node {
    cursor: pointer;
  }

  .page-node:focus rect,
  .page-node:hover rect {
    stroke: var(--accent);
    stroke-width: 3;
  }

  .submit-node rect {
    fill: #ecfdf3;
    stroke: #067647;
  }

  .problem-node rect {
    stroke: #b42318;
  }

  .unreachable-node rect {
    stroke-dasharray: 7 4;
  }

  .node-title {
    fill: #101828;
    font-size: 16px;
    font-weight: 700;
  }

  .node-detail {
    fill: #475467;
    font-size: 13px;
  }

  .problem-mark {
    fill: #b42318;
    font-size: 13px;
    font-weight: 700;
  }

  .destinations {
    margin-top: 1.25rem;
  }

  .destinations h2 {
    margin: 0 0 0.5rem;
    font-size: 1rem;
  }

  .destinations ul {
    margin: 0;
    padding-left: 1.5rem;
  }

  .destinations li + li {
    margin-top: 0.35rem;
  }

  .problem-text {
    margin: 0.35rem 0 0;
    color: #b42318;
    font-weight: 600;
  }
</style>
