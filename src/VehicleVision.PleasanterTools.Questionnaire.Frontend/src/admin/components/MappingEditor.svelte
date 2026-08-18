<script lang="ts">
  import { text, type ColumnAssignment, type MappingDefinition, type Question } from '../lib/types';

  interface Props {
    mapping: MappingDefinition;
    questions: Question[];
    onchange: (mapping: MappingDefinition) => void;
  }

  let { mapping, questions, onchange }: Props = $props();

  /** 変換の種類。**入力が複数なら必ずどれかが要る。** */
  const converters = [
    { value: '', label: '（変換なし）' },
    { value: 'join', label: 'つなぐ（join）' },
    { value: 'map', label: '値を置き換える（map）' },
    { value: 'toCheck', label: 'チェック列にする（toCheck）' },
    { value: 'contains', label: '含むか（contains）' },
    { value: 'constant', label: '固定値（constant）' },
    { value: 'coalesce', label: '最初の非空（coalesce）' },
    { value: 'when', label: '条件（when）' },
    { value: 'script', label: 'スクリプト（script）' },
  ];

  const ports = [
    { value: 'Value', label: '回答の値' },
    { value: 'OtherText', label: 'その他の自由記述' },
    { value: 'FileNames', label: '添付の名前' },
  ] as const;

  const answerable = $derived(questions.filter((question) => question.type !== 'Note'));

  function update(assignments: ColumnAssignment[]) {
    onchange({ assignments });
  }

  function add() {
    update([
      ...mapping.assignments,
      { targetColumn: '', sources: answerable[0] ? [{ questionId: answerable[0].questionId, port: 'Value' }] : [] },
    ]);
  }

  function patch(index: number, next: Partial<ColumnAssignment>) {
    update(mapping.assignments.map((a, i) => (i === index ? { ...a, ...next } : a)));
  }

  function remove(index: number) {
    update(mapping.assignments.filter((_, i) => i !== index));
  }

  function addSource(index: number) {
    const assignment = mapping.assignments[index];
    if (!assignment || !answerable[0]) return;

    patch(index, {
      sources: [...assignment.sources, { questionId: answerable[0].questionId, port: 'Value' }],
      // **入力が複数なら変換が要る。** どうまとめるかが決まらないため既定を入れる
      converter: assignment.converter ?? { operation: 'join', config: { separator: '、' } },
    });
  }

  function removeSource(index: number, sourceIndex: number) {
    const assignment = mapping.assignments[index];
    if (!assignment) return;
    patch(index, { sources: assignment.sources.filter((_, i) => i !== sourceIndex) });
  }

  function questionLabel(questionId: string): string {
    const question = questions.find((q) => q.questionId === questionId);
    return question ? text(question.title) || questionId : `${questionId}（存在しません）`;
  }
</script>

<section>
  <div class="bar">
    <h2>Pleasanter への割り当て</h2>
    <button type="button" class="secondary small" onclick={add}>列を足す</button>
  </div>

  <!-- **出力は必ず 1 本。** だから循環参照も合流の衝突も起こらない -->
  <p class="lead">
    設問（入力）を 1 つ以上選び、必要なら変換をはさんで、Pleasanter の列 1 本へ書きます。
    <strong>入力が複数のときは変換が必要です。</strong>
  </p>

  {#if mapping.assignments.length === 0}
    <p class="status">まだ割り当てがありません。このままでも公開できますが、回答は Pleasanter に残りません。</p>
  {/if}

  {#each mapping.assignments as assignment, index (index)}
    <div class="assignment">
      <div class="row">
        <label class="target">
          書き込み先の列
          <input
            type="text"
            placeholder="ClassA / NumA など"
            value={assignment.targetColumn}
            oninput={(event) => patch(index, { targetColumn: event.currentTarget.value })}
          />
        </label>

        <label class="converter">
          変換
          <select
            value={assignment.converter?.operation ?? ''}
            onchange={(event) => {
              const operation = event.currentTarget.value;
              patch(index, {
                converter: operation === '' ? null : { operation, config: assignment.converter?.config ?? {} },
              });
            }}
          >
            {#each converters as converter (converter.value)}
              <option value={converter.value}>{converter.label}</option>
            {/each}
          </select>
        </label>

        <button type="button" class="icon danger" onclick={() => remove(index)} aria-label="この割り当てを削除">
          ×
        </button>
      </div>

      {#if assignment.converter === null || assignment.converter === undefined}
        {#if assignment.sources.length !== 1}
          <p class="warn">変換が無いときは入力をちょうど 1 つにしてください。</p>
        {/if}
      {/if}

      <div class="sources">
        {#each assignment.sources as source, sourceIndex (sourceIndex)}
          <div class="source">
            <select
              value={source.questionId}
              onchange={(event) =>
                patch(index, {
                  sources: assignment.sources.map((s, i) =>
                    i === sourceIndex ? { ...s, questionId: event.currentTarget.value } : s,
                  ),
                })}
            >
              {#each answerable as question (question.questionId)}
                <option value={question.questionId}>{questionLabel(question.questionId)}</option>
              {/each}
            </select>

            <select
              value={source.port}
              onchange={(event) =>
                patch(index, {
                  sources: assignment.sources.map((s, i) =>
                    i === sourceIndex
                      ? { ...s, port: event.currentTarget.value as (typeof ports)[number]['value'] }
                      : s,
                  ),
                })}
            >
              {#each ports as port (port.value)}
                <option value={port.value}>{port.label}</option>
              {/each}
            </select>

            <button
              type="button"
              class="icon danger"
              onclick={() => removeSource(index, sourceIndex)}
              aria-label="入力を削除">×</button
            >
          </div>
        {/each}

        <button type="button" class="secondary small" onclick={() => addSource(index)}>入力を足す</button>

        {#if assignment.sources.length > 1}
          <!-- **並び順が変換へ渡す順。** 入れ替えると結果が変わる -->
          <p class="hint">上から順に変換へ渡します。</p>
        {/if}
      </div>
    </div>
  {/each}
</section>

<style lang="scss">
  .bar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin: 2rem 0 0.5rem;
  }

  h2 {
    font-size: 1.05rem;
    margin: 0;
  }

  .lead,
  .status {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 1rem;
  }

  .assignment {
    padding: 0.85rem 1rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 6px;
    margin-bottom: 0.75rem;
  }

  .row {
    display: flex;
    gap: 0.75rem;
    align-items: flex-end;
  }

  label {
    font-size: 0.82rem;
    color: var(--muted);
  }

  .target {
    flex: 1;
  }

  .converter {
    flex: 1;
  }

  input,
  select {
    display: block;
    width: 100%;
    margin-top: 0.2rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    color: #101828;
    box-sizing: border-box;
  }

  .sources {
    margin-top: 0.75rem;
    padding-top: 0.6rem;
    border-top: 1px dashed var(--border);
  }

  .source {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    margin-bottom: 0.4rem;

    select {
      margin-top: 0;
    }
  }

  .hint {
    color: var(--muted);
    font-size: 0.8rem;
    margin: 0.4rem 0 0;
  }

  .warn {
    color: #b54708;
    font-size: 0.82rem;
    margin: 0.5rem 0 0;
  }

  .icon {
    width: 1.9rem;
    height: 1.9rem;
    flex: none;
    padding: 0;
    line-height: 1;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 4px;
    cursor: pointer;

    &.danger {
      color: var(--error);
    }
  }

  .small {
    font-size: 0.85rem;
    padding: 0.35rem 0.75rem;
  }
</style>
