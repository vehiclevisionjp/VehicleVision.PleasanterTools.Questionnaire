<script lang="ts">
  import SurveyPreview from './SurveyPreview.svelte';
  import { loadDraft, publish, saveDraft } from '../lib/api';
  import {
    displayText,
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
    flowKey,
    hasChoiceTransitions,
    staleTargetId,
    toTransition,
    transitionValue,
    validateFlow,
    type FlowProblem,
  } from '../lib/flow';
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

  /** プレビューを開いているか。**保存前の下書きをそのまま見る。** */
  let previewing = $state(false);
  let mapping = $state<MappingDefinition>({ assignments: [] });
  let revision = $state(0);

  let loading = $state(true);
  let saving = $state(false);
  let error = $state('');
  let notice = $state('');
  let conflict = $state(false);
  let warnings = $state<MappingProblem[]>([]);

  /** 公開が断られたときにサーバが返した分岐の不備。**サーバが最後の判定者。** */
  let publishFlow = $state<FlowProblem[]>([]);

  const allQuestions = $derived(definition?.pages.flatMap((page) => page.questions) ?? []);

  /**
   * 編集中の分岐の不備。
   *
   * **公開して初めて弾かれると作り直しになる**ので、編集中にも出す（Issue #44）。
   * **サーバ側の検査を緩める代わりではない。** 公開の口が改めて同じことを見る。
   */
  const flowProblems = $derived(definition ? validateFlow(definition) : []);

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
    publishFlow = [];

    const result = await publish(surveyId);
    saving = false;

    if (!result.ok) {
      error = result.message;
      // **サーバが返した不備をそのまま出す。** 値が無い項目は落として返ってくる
      const body = result.body as
        | { problems?: MappingProblem[]; flow?: FlowProblem[] }
        | undefined;
      if (body?.problems) {
        warnings = body.problems;
      }

      if (body?.flow) {
        publishFlow = body.flow;
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

  // ---- 分岐 -----------------------------------------------------------------

  /** ページの見出し。**見出しが無ければ何ページ目かで呼ぶ。** */
  function pageLabel(index: number): string {
    const title = displayText(definition?.pages[index]?.title, editing);
    return title === ''
      ? t('branching.pageNumber', { number: index + 1 })
      : t('branching.pageWithTitle', { number: index + 1, title });
  }

  /**
   * そのページから飛べる先。
   *
   * **後ろのページだけを出す。** 前を向いた行き先は無限に回るアンケートになるので、
   * 選ばせてから公開で弾くのではなく、はじめから一覧に出さない。
   */
  function jumpTargets(pageIndex: number): { pageId: string; label: string }[] {
    return (definition?.pages ?? [])
      .map((page, index) => ({ page, index }))
      .filter((entry) => entry.index > pageIndex)
      .map((entry) => ({ pageId: entry.page.pageId, label: pageLabel(entry.index) }));
  }

  /** その設問より前にある設問。**表示条件で見に行けるのはここだけ。** */
  function priorQuestions(pageIndex: number, questionIndex: number): Question[] {
    const pages = definition?.pages ?? [];
    return [
      ...pages.slice(0, pageIndex).flatMap((page) => page.questions),
      ...(pages[pageIndex]?.questions.slice(0, questionIndex) ?? []),
    ];
  }

  /**
   * 同じページで既に行き先を持っている、別の設問の文言。無ければ `null`。
   *
   * **1 ページに行き先を持てる設問は 1 つだけ。** 2 つ目は付けさせない。
   */
  function branchTakenBy(pageIndex: number, questionIndex: number): string | null {
    const questions = definition?.pages[pageIndex]?.questions ?? [];
    const other = questions.find(
      (question, index) => index !== questionIndex && hasChoiceTransitions(question),
    );

    return other ? displayText(other.title, editing) || other.questionId : null;
  }

  function describeFlow(problem: FlowProblem): string {
    // **知らない符号でも落とさない。** 符号そのものを出す
    const key = flowKey(problem.code);
    const base = key ? t(key) : problem.code;
    const where = whereOf(problem);
    const detail = problem.detail ? `（${problem.detail}）` : '';
    return where === '' ? `${base}${detail}` : `[${where}] ${base}${detail}`;
  }

  /** その不備がどこのものか。 */
  function whereOf(problem: FlowProblem): string {
    const pageIndex = definition?.pages.findIndex((page) => page.pageId === problem.pageId) ?? -1;
    const page = pageIndex >= 0 ? pageLabel(pageIndex) : (problem.pageId ?? '');
    const question = problem.questionId ? flowQuestionLabel(problem.questionId) : '';

    if (page === '') return question;
    if (question === '') return page;
    return t('flow.where', { page, question });
  }

  function flowQuestionLabel(questionId: string): string {
    const question = allQuestions.find((entry) => entry.questionId === questionId);
    return question
      ? displayText(question.title, editing) || questionId
      : t('mapping.missingQuestion', { questionId });
  }
</script>

<header class="bar">
  <button type="button" class="link" onclick={onback}>{t('editor.back')}</button>

  <div class="right">
    <span class="revision">{t('editor.revision', { revision })}</span>
    <button
      type="button"
      class="secondary"
      onclick={() => (previewing = true)}
      disabled={loading || definition === undefined}
    >
      {t('preview.open')}
    </button>
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

<!-- **公開して初めて弾かれると作り直しになる。** 編集中にも同じことを出す -->
{#if flowProblems.length > 0}
  <section class="flow-problems" role="alert">
    <p><strong>{t('flow.title')}</strong> {t('flow.lead')}</p>
    <ul>
      {#each flowProblems as problem, index (index)}
        <li>{describeFlow(problem)}</li>
      {/each}
    </ul>
  </section>
{/if}

<!-- **最後に判定するのはサーバ。** 断られた理由をそのまま出す -->
{#if publishFlow.length > 0}
  <section class="flow-problems from-server" role="alert">
    <p><strong>{t('flow.fromServer')}</strong></p>
    <ul>
      {#each publishFlow as problem, index (index)}
        <li>{describeFlow(problem)}</li>
      {/each}
    </ul>
  </section>
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
    {@const targets = jumpTargets(pageIndex)}
    {@const stale = staleTargetId(page.next, targets.map((target) => target.pageId))}
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
          jumpTargets={targets}
          priorQuestions={priorQuestions(pageIndex, questionIndex)}
          branchTakenBy={branchTakenBy(pageIndex, questionIndex)}
          onchange={(next) => updateQuestion(pageIndex, questionIndex, next)}
          onremove={() => removeQuestion(pageIndex, questionIndex)}
          onmove={(direction) => moveQuestion(pageIndex, questionIndex, direction)}
        />
      {/each}

      <button type="button" class="secondary small" onclick={() => addQuestion(pageIndex)}>
        {t('editor.addQuestion')}
      </button>

      <!-- **選択肢の行き先が優先される。** そちらが無いときにここへ落ちる -->
      <div class="page-next">
        <label class="inline">
          {t('branching.pageNext')}
          <select
            value={transitionValue(page.next) || 'Next'}
            onchange={(event) => {
              const value = event.currentTarget.value;
              // **「次のページへ」は行き先を持たないことにする。** 既定と同じ意味
              updatePage(pageIndex, { next: value === 'Next' ? null : toTransition(value) });
            }}
          >
            <option value="Next">{t('branching.toNextPage')}</option>
            {#each targets as target, targetIndex (targetIndex)}
              <option value={`page:${target.pageId}`}>{target.label}</option>
            {/each}
            <option value="Submit">{t('branching.toSubmit')}</option>
            {#if stale !== null}
              <!-- **今は選べない飛び先も出す。** 黙って別の行き先に変えない -->
              <option value={`page:${stale}`}>{t('branching.staleTarget', { pageId: stale })}</option>
            {/if}
          </select>
        </label>

        {#if targets.length === 0}
          <span class="hint">{t('branching.noLaterPage')}</span>
        {/if}
      </div>
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

<!--
  **編集画面の上に重ねる。** 別の経路にすると保存前の下書きを渡せない。
  **開いている間は編集の続きを触らせない**（下敷きの入力に触れると
  見ているものと食い違う）
-->
{#if previewing && definition}
  <div class="overlay" role="dialog" aria-modal="true" aria-label={t('preview.title')}>
    <div class="sheet">
      <SurveyPreview {definition} onclose={() => (previewing = false)} />
    </div>
  </div>
{/if}

<style lang="scss">
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 10;
    overflow-y: auto;
    background: rgb(16 24 40 / 45%);
  }

  .sheet {
    max-width: 56rem;
    margin: 2rem auto;
    padding: 1.5rem;
    background: var(--bg);
    border-radius: 10px;
  }

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

  .flow-problems {
    margin: 0 0 1rem;
    padding: 0.75rem 1rem;
    background: #fef3f2;
    border: 1px solid var(--error);
    border-radius: 6px;
    font-size: 0.85rem;

    p {
      margin: 0 0 0.4rem;
    }

    ul {
      margin: 0;
      padding-left: 1.25rem;
    }

    &.from-server {
      background: #fffaeb;
      border-color: #fec84b;
    }
  }

  .page-next {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex-wrap: wrap;
    margin-top: 0.75rem;
    padding-top: 0.75rem;
    border-top: 1px dashed var(--border);
    font-size: 0.85rem;
    color: var(--muted);

    select {
      font: inherit;
      padding: 0.3rem 0.4rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: #fff;
      color: #101828;
    }

    .hint {
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
