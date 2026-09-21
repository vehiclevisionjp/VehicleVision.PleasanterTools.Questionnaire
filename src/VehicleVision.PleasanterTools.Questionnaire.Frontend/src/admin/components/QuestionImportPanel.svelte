<script lang="ts">
  import {
    importQuestions,
    listSurveys,
    listTemplates,
    loadQuestionImportSource,
  } from '../lib/api';
  import {
    displayText,
    questionTypeKey,
    type QuestionImportResult,
    type QuestionImportSource,
    type SurveySummary,
  } from '../lib/types';
  import type { Language } from '../../lib/i18n/language';
  import { t } from '../lib/i18n/state.svelte';

  interface Source {
    surveyId: string;
    title: string;
    template: boolean;
  }

  interface Props {
    surveyId: string;
    existingQuestionIds: string[];
    editing: Language;
    onimport: (result: QuestionImportResult) => void;
  }

  let { surveyId, existingQuestionIds, editing, onimport }: Props = $props();

  let open = $state(false);
  let loading = $state(false);
  let importing = $state(false);
  let error = $state('');
  let sources = $state<Source[]>([]);
  let sourceId = $state('');
  let source = $state<QuestionImportSource | null>(null);
  let selectedIds = $state<string[]>([]);

  async function openPanel() {
    open = true;
    loading = true;
    error = '';
    source = null;
    sourceId = '';
    selectedIds = [];

    const [surveyResult, templateResult] = await Promise.all([
      loadAllSurveys(),
      listTemplates(),
    ]);
    loading = false;

    if (!surveyResult.ok) {
      error = surveyResult.message;
      return;
    }

    if (!templateResult.ok) {
      error = templateResult.message;
      return;
    }

    sources = [
      ...surveyResult.value
        .filter((survey) => survey.surveyId !== surveyId)
        .map((survey) => ({ surveyId: survey.surveyId, title: survey.title, template: false })),
      ...templateResult.value.map((template) => ({
        surveyId: template.templateId,
        title: template.title,
        template: true,
      })),
    ];
  }

  async function loadAllSurveys() {
    const surveys: SurveySummary[] = [];
    let offset = 0;

    while (true) {
      const result = await listSurveys(offset, 200, '', null);
      if (!result.ok) return result;

      surveys.push(...result.value.items);
      if (!result.value.hasMore) {
        return { ok: true as const, value: surveys };
      }

      offset += result.value.items.length;
    }
  }

  async function selectSource(nextSourceId: string) {
    sourceId = nextSourceId;
    source = null;
    selectedIds = [];
    error = '';
    if (sourceId === '') return;

    loading = true;
    const result = await loadQuestionImportSource(surveyId, sourceId);
    loading = false;
    if (!result.ok) {
      error = result.message;
      return;
    }

    source = result.value;
  }

  function toggle(questionId: string, checked: boolean) {
    selectedIds = checked
      ? [...selectedIds, questionId]
      : selectedIds.filter((id) => id !== questionId);
  }

  async function submit() {
    if (sourceId === '' || selectedIds.length === 0 || importing) return;

    importing = true;
    error = '';
    const result = await importQuestions(
      surveyId,
      sourceId,
      selectedIds,
      existingQuestionIds,
    );
    importing = false;
    if (!result.ok) {
      error = result.message;
      return;
    }

    onimport(result.value);
    open = false;
  }
</script>

{#if open}
  <section class="import-panel">
    <div class="head">
      <h3>{t('questionImport.title')}</h3>
      <button type="button" class="secondary small" onclick={() => (open = false)}>
        {t('questionImport.close')}
      </button>
    </div>
    <p class="hint">{t('questionImport.lead')}</p>

    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <label>
      {t('questionImport.source')}
      <select
        value={sourceId}
        disabled={loading || importing}
        onchange={(event) => void selectSource(event.currentTarget.value)}
      >
        <option value="">{t('questionImport.chooseSource')}</option>
        {#each sources as item (item.surveyId)}
          <option value={item.surveyId}>
            {item.title}{item.template ? t('questionImport.templateSuffix') : ''}
          </option>
        {/each}
      </select>
    </label>

    {#if loading}
      <p class="status">{t('app.loading')}</p>
    {:else if source}
      {#if source.pages.every((page) => page.questions.length === 0)}
        <p class="status">{t('questionImport.empty')}</p>
      {:else}
        <div class="questions">
          {#each source.pages as page, pageIndex (page.pageId)}
            {#if page.questions.length > 0}
              <fieldset>
                <legend>
                  {displayText(page.title ?? undefined, editing)
                    || t('questionImport.pageNumber', { number: pageIndex + 1 })}
                </legend>
                {#each page.questions as question (question.questionId)}
                  <label class="question">
                    <input
                      type="checkbox"
                      checked={selectedIds.includes(question.questionId)}
                      onchange={(event) => toggle(question.questionId, event.currentTarget.checked)}
                    />
                    <span>
                      {displayText(question.title, editing) || question.questionId}
                      <small>{t(questionTypeKey(question.type))}</small>
                    </span>
                  </label>
                {/each}
              </fieldset>
            {/if}
          {/each}
        </div>
      {/if}
    {/if}

    <p class="warning">{t('questionImport.warning')}</p>
    <div class="actions">
      <button
        type="button"
        disabled={importing || selectedIds.length === 0}
        onclick={() => void submit()}
      >
        {importing ? t('editor.working') : t('questionImport.submit', { count: selectedIds.length })}
      </button>
      <button type="button" class="secondary" onclick={() => (open = false)}>
        {t('questionImport.cancel')}
      </button>
    </div>
  </section>
{:else}
  <button type="button" class="secondary small" onclick={() => void openPanel()}>
    {t('questionImport.open')}
  </button>
{/if}

<style lang="scss">
  .import-panel {
    margin-top: 0.75rem;
    padding: 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
  }

  .head,
  .actions {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  h3 {
    margin: 0;
    font-size: 0.95rem;
  }

  label {
    display: block;
    margin-bottom: 0.75rem;
    color: var(--muted);
    font-size: 0.85rem;
  }

  select {
    display: block;
    width: 100%;
    margin-top: 0.25rem;
    padding: 0.4rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: var(--surface);
    color: var(--text);
    font: inherit;
  }

  .questions {
    max-height: 22rem;
    overflow-y: auto;
  }

  fieldset {
    margin: 0 0 0.75rem;
    border: 1px solid var(--border);
  }

  .question {
    display: flex;
    align-items: flex-start;
    gap: 0.4rem;
  }

  small {
    display: block;
    color: var(--muted);
  }

  .hint,
  .status,
  .warning,
  .error {
    margin: 0.5rem 0;
    font-size: 0.82rem;
  }

  .hint,
  .status {
    color: var(--muted);
  }

  .warning {
    color: var(--text);
  }

  .error {
    color: var(--error);
  }
</style>
