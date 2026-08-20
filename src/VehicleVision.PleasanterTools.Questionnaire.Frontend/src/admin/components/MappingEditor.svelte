<script lang="ts">
  import {
    displayText,
    isAttachmentColumn,
    rowPorts,
    type ColumnAssignment,
    type LocalizedText,
    type MappingDefinition,
    type Question,
    type QuestionPort,
  } from '../lib/types';
  import { measure } from '../lib/columnBudget';
  import type { Language } from '../../lib/i18n/language';
  import { t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';

  interface Props {
    mapping: MappingDefinition;
    questions: Question[];
    /** 設問の文言をどの言語で出すか。**設問エディタで選んでいる言語に揃える。** */
    editing: Language;
    onchange: (mapping: MappingDefinition) => void;
  }

  let { mapping, questions, editing, onchange }: Props = $props();

  /**
   * 変換の種類。**入力が複数なら必ずどれかが要る。**
   *
   * **値は変換の名前そのもの**（サーバへ渡る）。文言は鍵から引く
   */
  const converters: { value: string; key: MessageKey }[] = [
    { value: '', key: 'converter.none' },
    { value: 'join', key: 'converter.join' },
    { value: 'map', key: 'converter.map' },
    { value: 'toCheck', key: 'converter.toCheck' },
    { value: 'contains', key: 'converter.contains' },
    { value: 'constant', key: 'converter.constant' },
    { value: 'coalesce', key: 'converter.coalesce' },
    { value: 'when', key: 'converter.when' },
    { value: 'script', key: 'converter.script' },
  ];

  const ports: { value: QuestionPort; key: MessageKey }[] = [
    { value: 'Value', key: 'port.Value' },
    { value: 'OtherText', key: 'port.OtherText' },
    { value: 'FileNames', key: 'port.FileNames' },
  ];

  /**
   * 型ごとに、いくつ列を使っているか（Issue #74）。
   *
   * **グリッドは 1 設問で行数ぶんの列を食う。**
   * 保存や公開のときに初めて足りないと分かると、作り直しになる。
   */
  const usage = $derived(measure(mapping));

  /** 足りていない型。**あれば公開できない。** */
  const overflowing = $derived(usage.filter((entry) => !entry.fits));

  const answerable = $derived(questions.filter((question) => question.type !== 'Note'));
  const fileQuestions = $derived(questions.filter((question) => question.type === 'File'));

  function update(assignments: ColumnAssignment[]) {
    onchange({ assignments });
  }

  function add() {
    update([
      ...mapping.assignments,
      {
        targetColumn: '',
        sources: answerable[0] ? [{ questionId: answerable[0].questionId, port: 'Value' }] : [],
      },
    ]);
  }

  /**
   * 添付の割り当てを足す。
   *
   * **形は `1 : 0 : 1` に固定。** 入力は添付の設問 1 つ、変換なし。
   */
  function addAttachment() {
    const question = fileQuestions[0];
    if (!question) return;

    update([
      ...mapping.assignments,
      {
        targetColumn: nextAttachmentColumn(),
        sources: [{ questionId: question.questionId, port: 'Files' }],
        converter: null,
      },
    ]);
  }

  /** まだ使っていない添付列を返す。 */
  function nextAttachmentColumn(): string {
    const used = new Set(
      mapping.assignments.map((assignment) => assignment.targetColumn.toUpperCase()),
    );

    for (const letter of 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') {
      if (!used.has(`ATTACHMENTS${letter}`)) {
        return `Attachments${letter}`;
      }
    }

    // **列は型ごとに 26 本しか無い。** 空で出して、不備として見せる
    return '';
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

  /**
   * その設問が出す行（または項目）（Issue #74）。**出さない設問では空。**
   *
   * **サーバの `Question.RowPortIds` と同じ並び**にする。食い違うと、
   * 画面で選べた行がサーバで「その設問に無い行」として弾かれる。
   */
  function rowPortsOf(questionId: string): { rowId: string; label: LocalizedText }[] {
    const question = questions.find((candidate) => candidate.questionId === questionId);
    return question === undefined ? [] : rowPorts(question);
  }

  function questionLabel(questionId: string): string {
    const question = questions.find((q) => q.questionId === questionId);
    if (!question) {
      return t('mapping.missingQuestion', { questionId });
    }

    // **回答画面で出る文字列と同じ見え方にする。** 未翻訳なら日本語へ落ちる
    return displayText(question.title, editing) || questionId;
  }

  /** その割り当てが添付のものか。 */
  function isAttachment(assignment: ColumnAssignment): boolean {
    return (
      assignment.sources.some((source) => source.port === 'Files') ||
      isAttachmentColumn(assignment.targetColumn)
    );
  }
</script>

<section>
  <div class="bar">
    <h2>{t('mapping.title')}</h2>
    <div class="buttons">
      <button type="button" class="secondary small" onclick={add}>{t('mapping.addColumn')}</button>
      <button
        type="button"
        class="secondary small"
        disabled={fileQuestions.length === 0}
        title={fileQuestions.length === 0 ? t('mapping.noFileQuestion') : ''}
        onclick={addAttachment}>{t('mapping.addAttachmentColumn')}</button
      >
    </div>
  </div>

  <!-- **出力は必ず 1 本。** だから循環参照も合流の衝突も起こらない -->
  <p class="lead">
    {t('mapping.lead')}
    <strong>{t('mapping.leadStrong')}</strong>
    <br />
    <strong>{t('mapping.attachmentLeadStrong')}</strong>{t('mapping.attachmentLead')}
  </p>

  {#if mapping.assignments.length === 0}
    <p class="status">{t('mapping.empty')}</p>
  {:else}
    <!--
      **列は型ごとに 26 本しかない**（Issue #74）。
      グリッドは 1 設問で行数ぶんを食うので、作っている最中に見えないと手遅れになる
    -->
    <ul class="budget">
      {#each usage as entry (entry.prefix)}
        <li class:over={!entry.fits}>
          {t('mapping.budgetEntry', {
            prefix: entry.prefix,
            used: entry.used,
            available: entry.available,
          })}
        </li>
      {/each}
    </ul>

    {#if overflowing.length > 0}
      <p class="warn">
        {t('mapping.budgetOver', {
          prefixes: overflowing.map((entry) => entry.prefix).join(' / '),
        })}
      </p>
    {/if}
  {/if}

  {#each mapping.assignments as assignment, index (index)}
    {@const attachment = isAttachment(assignment)}
    <div class="assignment" class:attachment>
      <div class="row">
        <label class="target">
          {t('mapping.targetColumn')}
          <input
            type="text"
            placeholder={attachment
              ? t('mapping.attachmentColumnPlaceholder')
              : t('mapping.targetColumnPlaceholder')}
            value={assignment.targetColumn}
            oninput={(event) => patch(index, { targetColumn: event.currentTarget.value })}
          />
        </label>

        {#if attachment}
          <!-- **添付に変換は掛けられない。** 選ばせない -->
          <span class="fixed">{t('mapping.converterFixed')}</span>
        {:else}
          <label class="converter">
            {t('mapping.converter')}
            <select
              value={assignment.converter?.operation ?? ''}
              onchange={(event) => {
                const operation = event.currentTarget.value;
                patch(index, {
                  converter:
                    operation === ''
                      ? null
                      : { operation, config: assignment.converter?.config ?? {} },
                });
              }}
            >
              {#each converters as converter (converter.value)}
                <option value={converter.value}>{t(converter.key)}</option>
              {/each}
            </select>
          </label>
        {/if}

        <button
          type="button"
          class="icon danger"
          onclick={() => remove(index)}
          aria-label={t('mapping.removeAssignment')}>×</button
        >
      </div>

      {#if !attachment && assignment.converter == null && assignment.sources.length !== 1}
        <p class="warn">{t('mapping.needsSingleSource')}</p>
      {/if}

      {#if attachment && assignment.targetColumn === ''}
        <!-- **列は型ごとに 26 本しか無い** -->
        <p class="warn">{t('mapping.noAttachmentColumnLeft')}</p>
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
              {#each attachment ? fileQuestions : answerable as question (question.questionId)}
                <option value={question.questionId}>{questionLabel(question.questionId)}</option>
              {/each}
            </select>

            {#if attachment}
              <span class="fixed">{t('mapping.attachmentPort')}</span>
            {:else}
              <!--
                **グリッドとランキングは 1 設問が入力を複数出す**（Issue #74）。
                どの行かを選ばないと、行をまたいだ値がまとめて 1 列へ入る
              -->
              {@const ports2 = rowPortsOf(source.questionId)}
              {#if ports2.length > 0}
                <select
                  value={source.rowId ?? ''}
                  onchange={(event) =>
                    patch(index, {
                      sources: assignment.sources.map((s, i) =>
                        i === sourceIndex
                          ? { ...s, rowId: event.currentTarget.value || undefined }
                          : s,
                      ),
                    })}
                >
                  <!-- **選ばないままにもできるが、公開時に弾かれる。** 黙って通さない -->
                  <option value="">{t('mapping.rowUnset')}</option>
                  {#each ports2 as port (port.rowId)}
                    <option value={port.rowId}>
                      {displayText(port.label, editing) || port.rowId}
                    </option>
                  {/each}
                </select>
              {/if}

              <select
                value={source.port}
                onchange={(event) =>
                  patch(index, {
                    sources: assignment.sources.map((s, i) =>
                      i === sourceIndex
                        ? { ...s, port: event.currentTarget.value as QuestionPort }
                        : s,
                    ),
                  })}
              >
                {#each ports as port (port.value)}
                  <option value={port.value}>{t(port.key)}</option>
                {/each}
              </select>

              <button
                type="button"
                class="icon danger"
                onclick={() => removeSource(index, sourceIndex)}
                aria-label={t('mapping.removeSource')}>×</button
              >
            {/if}
          </div>
        {/each}

        {#if attachment}
          <!-- **送り直しは置き換え。** 前の添付は消える -->
          <p class="hint">{t('mapping.attachmentReplaceHint')}</p>
        {:else}
          <button type="button" class="secondary small" onclick={() => addSource(index)}>
            {t('mapping.addSource')}
          </button>

          {#if assignment.sources.length > 1}
            <!-- **並び順が変換へ渡す順。** 入れ替えると結果が変わる -->
            <p class="hint">{t('mapping.sourceOrderHint')}</p>
          {/if}
        {/if}
      </div>
    </div>
  {/each}
</section>

<style lang="scss">
  .budget {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem 1rem;
    list-style: none;
    margin: 0 0 0.75rem;
    padding: 0;
    color: var(--muted);
    font-size: 0.85rem;
  }

  /* **色だけに頼らない。** 文言そのものが「26 本中 30 本」と読める */
  .budget .over {
    color: var(--error);
    font-weight: 600;
  }

  .bar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin: 2rem 0 0.5rem;
  }

  .buttons {
    display: flex;
    gap: 0.5rem;
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

    &.attachment {
      border-left: 3px solid var(--accent);
    }
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

  .fixed {
    flex: 1;
    color: var(--muted);
    font-size: 0.82rem;
    padding-bottom: 0.5rem;
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

    .fixed {
      padding-bottom: 0;
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
