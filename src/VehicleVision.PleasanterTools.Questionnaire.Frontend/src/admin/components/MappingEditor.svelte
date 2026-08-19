<script lang="ts">
  import {
    displayText,
    isAttachmentColumn,
    type ColumnAssignment,
    type MappingDefinition,
    type Question,
    type QuestionPort,
  } from '../lib/types';
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
