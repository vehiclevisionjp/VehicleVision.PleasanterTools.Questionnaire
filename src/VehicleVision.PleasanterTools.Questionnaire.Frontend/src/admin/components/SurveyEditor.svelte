<script lang="ts">
  import { loadDraft, publish, saveDraft } from '../lib/api';
  import {
    problemKey,
    text,
    withText,
    type MappingDefinition,
    type MappingProblem,
    type Page,
    type Question,
    type SurveyDefinition,
  } from '../lib/types';
  import {
    DEFAULT_LANGUAGE,
    LANGUAGE_NAMES,
    SUPPORTED_LANGUAGES,
    type Language,
  } from '../../lib/i18n/language';
  import { language, t } from '../lib/i18n/state.svelte';
  import MappingEditor from './MappingEditor.svelte';
  import QuestionEditor from './QuestionEditor.svelte';

  interface Props {
    surveyId: string;
    onback: () => void;
  }

  let { surveyId, onback }: Props = $props();

  /**
    * 入力欄が書き込む言語。
    *
    * **管理画面の表示言語とは別。** 日本語の画面で英語の設問を書くことがある。
    * 初期値は表示言語に合わせる（たいていは同じ言語を編集する）。
    *
    * **`LocalizedText` の器は変えない。** 編集していない言語の文言はそのまま残る
    * （`_documents/多言語対応方針.md` 5 章）。
    */
  let editing = $state<Language>(language());

  let definition = $state<SurveyDefinition>();
  let mapping = $state<MappingDefinition>({ assignments: [] });
  let revision = $state(0);

  let loading = $state(true);
  let saving = $state(false);
  let error = $state('');
  let notice = $state('');
  let conflict = $state(false);
  let warnings = $state<MappingProblem[]>([]);

  const allQuestions = $derived(definition?.pages.flatMap((page) => page.questions) ?? []);

  $effect(() => {
    void load(surveyId);
  });

  async function load(id: string) {
    loading = true;
    conflict = false;
    const result = await loadDraft(id);
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    definition = result.value.definition;
    mapping = result.value.mapping;
    revision = result.value.revision;
  }

  /** その設問が書き込まれる列。 */
  function columnsFor(questionId: string): string[] {
    return mapping.assignments
      .filter((assignment) => assignment.sources.some((source) => source.questionId === questionId))
      .map((assignment) => assignment.targetColumn)
      .filter((column) => column !== '');
  }

  function updatePage(index: number, patch: Partial<Page>) {
    if (!definition) return;
    definition = {
      ...definition,
      pages: definition.pages.map((page, i) => (i === index ? { ...page, ...patch } : page)),
    };
  }

  function addPage() {
    if (!definition) return;
    definition = {
      ...definition,
      pages: [...definition.pages, { pageId: newId('page'), questions: [] }],
    };
  }

  function removePage(index: number) {
    if (!definition) return;
    definition = { ...definition, pages: definition.pages.filter((_, i) => i !== index) };
  }

  function addQuestion(pageIndex: number) {
    const page = definition?.pages[pageIndex];
    if (!page) return;

    const question: Question = {
      questionId: newId('q'),
      type: 'Text',
      // **空の器で作る。** 文言は編集中の言語へ入る
      title: {},
      isRequired: false,
      choices: [],
      settings: {},
    };

    updatePage(pageIndex, { questions: [...page.questions, question] });
  }

  function updateQuestion(pageIndex: number, questionIndex: number, next: Question) {
    const page = definition?.pages[pageIndex];
    if (!page) return;
    updatePage(pageIndex, {
      questions: page.questions.map((q, i) => (i === questionIndex ? next : q)),
    });
  }

  function removeQuestion(pageIndex: number, questionIndex: number) {
    const page = definition?.pages[pageIndex];
    if (!page) return;
    updatePage(pageIndex, { questions: page.questions.filter((_, i) => i !== questionIndex) });
  }

  function moveQuestion(pageIndex: number, questionIndex: number, direction: -1 | 1) {
    const page = definition?.pages[pageIndex];
    if (!page) return;

    const target = questionIndex + direction;
    if (target < 0 || target >= page.questions.length) return;

    const questions = [...page.questions];
    const moved = questions[questionIndex];
    const swapped = questions[target];
    if (!moved || !swapped) return;

    questions[questionIndex] = swapped;
    questions[target] = moved;
    updatePage(pageIndex, { questions });
  }

  /**
   * 新しい識別子。
   *
   * **アンケートの中でだけ一意であればよい**（DB の主キーはアンケートとの複合）。
   * それでも衝突しにくい値にしておく。
   */
  function newId(prefix: string): string {
    return `${prefix}-${crypto.randomUUID().slice(0, 8)}`;
  }

  async function save() {
    if (!definition) return;

    saving = true;
    error = '';
    notice = '';

    const result = await saveDraft(surveyId, definition, mapping, revision);
    saving = false;

    if (!result.ok) {
      if (result.status === 409) {
        // **黙って上書きしない。** 読み直させる
        conflict = true;
      }
      error = result.message;
      return;
    }

    revision = result.value.revision;
    notice = t('editor.saved');
  }

  async function doPublish() {
    saving = true;
    error = '';
    notice = '';
    warnings = [];

    const result = await publish(surveyId);
    saving = false;

    if (!result.ok) {
      error = result.message;
      const problems = (result.body as { problems?: MappingProblem[] } | undefined)?.problems;
      if (problems) {
        warnings = problems;
      }
      return;
    }

    warnings = result.value.warnings;
    notice = t('editor.published', { version: result.value.version });
    await load(surveyId);
  }

  function describe(problem: MappingProblem): string {
    // **知らない符号でも落とさない。** 符号そのものを出す
    const key = problemKey(problem.code);
    const base = key ? t(key) : problem.code;
    const where = problem.targetColumn ? `[${problem.targetColumn}] ` : '';
    const detail = problem.detail ? `（${problem.detail}）` : '';
    return `${where}${base}${detail}`;
  }
</script>

<header class="bar">
  <button type="button" class="link" onclick={onback}>{t('editor.back')}</button>

  <div class="right">
    <span class="revision">{t('editor.revision', { revision })}</span>
    <button type="button" class="secondary" onclick={save} disabled={saving || loading}>
      {saving ? t('editor.working') : t('editor.saveDraft')}
    </button>
    <button type="button" onclick={doPublish} disabled={saving || loading}>
      {t('editor.publish')}
    </button>
  </div>
</header>

{#if conflict}
  <div class="conflict" role="alert">
    <p><strong>{t('editor.conflictTitle')}</strong> {t('editor.conflictLead')}</p>
    <p class="small">{t('editor.conflictDetail')}</p>
    <button type="button" class="secondary" onclick={() => load(surveyId)}>
      {t('editor.reload')}
    </button>
  </div>
{/if}

{#if error && !conflict}<p class="error" role="alert">{error}</p>{/if}
{#if notice}<p class="notice">{notice}</p>{/if}

{#if warnings.length > 0}
  <ul class="problems">
    {#each warnings as problem, index (index)}
      <li class:blocking={problem.isBlocking}>{describe(problem)}</li>
    {/each}
  </ul>
{/if}

{#if loading}
  <p class="status">{t('app.loading')}</p>
{:else if definition}
  <!-- **入力欄が書き込む言語を選ぶ。**
       他の言語の文言は触らない（`_documents/多言語対応方針.md` 5 章） -->
  <section class="editing-language">
    <label>
      {t('editor.editingLanguage')}
      <select
        value={editing}
        onchange={(event) => (editing = event.currentTarget.value as Language)}
      >
        {#each SUPPORTED_LANGUAGES as option (option)}
          <option value={option}>{LANGUAGE_NAMES[option]}</option>
        {/each}
      </select>
    </label>
    <p class="hint">{t('editor.editingLanguageHint')}</p>
    {#if editing !== DEFAULT_LANGUAGE}
      <!-- **未翻訳の落とし先は ja。** 空のまま公開しても画面は空にならない -->
      <p class="hint">{t('editor.fallbackNotice')}</p>
    {/if}
  </section>

  <section class="survey">
    <label class="big">
      {t('editor.title')}
      <input
        type="text"
        value={text(definition.title, editing)}
        oninput={(event) =>
          (definition = {
            ...definition!,
            title: withText(definition!.title, event.currentTarget.value, editing),
          })}
      />
    </label>

    <label>
      {t('editor.description')}
      <input
        type="text"
        value={text(definition.description, editing)}
        oninput={(event) =>
          (definition = {
            ...definition!,
            description: withText(definition!.description, event.currentTarget.value, editing),
          })}
      />
    </label>

    <label>
      {t('editor.confirmationMessage')}
      <input
        type="text"
        value={text(definition.confirmationMessage, editing)}
        oninput={(event) =>
          (definition = {
            ...definition!,
            confirmationMessage: withText(
              definition!.confirmationMessage,
              event.currentTarget.value,
              editing,
            ),
          })}
      />
    </label>

    <div class="toggles">
      <label class="inline">
        <input
          type="checkbox"
          checked={definition.showProgress}
          onchange={(event) => (definition = { ...definition!, showProgress: event.currentTarget.checked })}
        />
        {t('editor.showProgress')}
      </label>

      <label class="inline">
        <input
          type="checkbox"
          checked={definition.allowEditingAfterSubmit}
          onchange={(event) =>
            (definition = { ...definition!, allowEditingAfterSubmit: event.currentTarget.checked })}
        />
        {t('editor.allowEditingAfterSubmit')}
      </label>

      <span class="next-version">{t('editor.nextVersion', { version: definition.version })}</span>
    </div>
  </section>

  {#each definition.pages as page, pageIndex (page.pageId)}
    <section class="page">
      <div class="page-head">
        <!-- **ページの区切りがそのまま改ページになる** -->
        <input
          class="page-title"
          type="text"
          placeholder={t('editor.pageTitlePlaceholder', { number: pageIndex + 1 })}
          value={text(page.title, editing)}
          oninput={(event) =>
            updatePage(pageIndex, {
              title: withText(page.title, event.currentTarget.value, editing),
            })}
        />
        <button
          type="button"
          class="icon danger"
          onclick={() => removePage(pageIndex)}
          aria-label={t('editor.removePage')}
        >
          ×
        </button>
      </div>

      {#each page.questions as question, questionIndex (question.questionId)}
        <QuestionEditor
          {question}
          {editing}
          mappedColumns={columnsFor(question.questionId)}
          canMoveUp={questionIndex > 0}
          canMoveDown={questionIndex < page.questions.length - 1}
          onchange={(next) => updateQuestion(pageIndex, questionIndex, next)}
          onremove={() => removeQuestion(pageIndex, questionIndex)}
          onmove={(direction) => moveQuestion(pageIndex, questionIndex, direction)}
        />
      {/each}

      <button type="button" class="secondary small" onclick={() => addQuestion(pageIndex)}>
        {t('editor.addQuestion')}
      </button>
    </section>
  {/each}

  <button type="button" class="secondary" onclick={addPage}>{t('editor.addPage')}</button>

  <MappingEditor
    {mapping}
    questions={allQuestions}
    {editing}
    onchange={(next) => (mapping = next)}
  />
{/if}

<style lang="scss">
  .bar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 1.25rem;
  }

  .right {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .revision {
    color: var(--muted);
    font-size: 0.82rem;
  }

  .link {
    background: none;
    border: none;
    padding: 0;
    color: var(--accent);
    font: inherit;
    cursor: pointer;
  }

  .editing-language {
    display: flex;
    align-items: baseline;
    gap: 1rem;
    flex-wrap: wrap;
    padding: 0.75rem 1.25rem;
    margin-bottom: 1rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
    font-size: 0.85rem;

    label {
      display: inline-flex;
      align-items: center;
      gap: 0.5rem;
      margin: 0;
    }

    select {
      font: inherit;
      padding: 0.25rem 0.4rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: #fff;
      color: #101828;
    }

    .hint {
      margin: 0;
      color: var(--muted);
    }
  }

  .survey,
  .page {
    padding: 1.25rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
    margin-bottom: 1rem;
  }

  .page {
    background: var(--bg);
  }

  label {
    display: block;
    margin-bottom: 0.75rem;
    font-size: 0.85rem;
    color: var(--muted);
  }

  .big input {
    font-size: 1.1rem;
    font-weight: 600;
  }

  input[type='text'] {
    display: block;
    width: 100%;
    margin-top: 0.2rem;
    padding: 0.45rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    color: #101828;
    box-sizing: border-box;
  }

  .toggles {
    display: flex;
    align-items: center;
    gap: 1.25rem;
    flex-wrap: wrap;
    font-size: 0.85rem;
  }

  .inline {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    margin: 0;
  }

  .next-version {
    color: var(--muted);
    margin-left: auto;
  }

  .page-head {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    margin-bottom: 0.75rem;
  }

  .page-title {
    flex: 1;
    margin-top: 0;
    font-weight: 600;
  }

  .conflict {
    padding: 1rem;
    margin-bottom: 1rem;
    background: #fef3f2;
    border: 1px solid var(--error);
    border-radius: 6px;

    p {
      margin: 0 0 0.5rem;
    }

    .small {
      font-size: 0.85rem;
      color: var(--muted);
    }
  }

  .problems {
    margin: 0 0 1rem;
    padding: 0.75rem 1rem 0.75rem 2rem;
    background: #fffaeb;
    border: 1px solid #fec84b;
    border-radius: 6px;
    font-size: 0.85rem;

    .blocking {
      color: var(--error);
      font-weight: 600;
    }
  }

  .notice {
    color: #067647;
  }

  .error {
    color: var(--error);
  }

  .status {
    color: var(--muted);
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
