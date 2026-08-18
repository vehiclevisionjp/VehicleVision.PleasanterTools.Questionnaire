<script lang="ts">
  import QuestionField from './components/QuestionField.svelte';
  import {
    getOrCreateResponseToken,
    loadForm,
    loadPendingAnswers,
    submitAnswers,
  } from './lib/api';
  import type { AnswerState, PayloadAnswer, RejectionReason, SurveyDefinition } from './lib/types';
  import { isDisplayOnly, text } from './lib/types';
  import { validatePage } from './lib/validation';

  type Screen = 'loading' | 'answering' | 'completed' | 'rejected' | 'error';

  let screen = $state<Screen>('loading');
  let rejection = $state<RejectionReason>();
  let definition = $state<SurveyDefinition>();
  let publicId = $state('');
  let responseToken = $state('');
  let pageIndex = $state(0);
  let answers = $state<Record<string, AnswerState>>({});
  let errors = $state<Record<string, string>>({});
  let submitting = $state(false);
  let submitFailed = $state(false);

  const pages = $derived(definition?.pages ?? []);
  const currentPage = $derived(pages[pageIndex]);
  const isLastPage = $derived(pageIndex >= pages.length - 1);
  const progress = $derived(pages.length === 0 ? 0 : Math.round(((pageIndex + 1) / pages.length) * 100));

  /** URL の `/f/{publicId}` から公開 ID を取る。 */
  function readPublicId(): string {
    const match = /^\/f\/([^/]+)/.exec(location.pathname);
    return match?.[1] ?? '';
  }

  function emptyAnswer(): AnswerState {
    return { values: [], otherText: '' };
  }

  $effect(() => {
    void start();
  });

  async function start() {
    publicId = readPublicId();
    if (publicId === '') {
      screen = 'error';
      return;
    }

    const result = await loadForm(publicId);
    if (!result.form) {
      rejection = result.rejection ?? 'notFound';
      screen = 'rejected';
      return;
    }

    definition = result.form.definition;
    responseToken = getOrCreateResponseToken(publicId);

    for (const page of definition.pages) {
      for (const question of page.questions) {
        answers[question.questionId] = emptyAnswer();
      }
    }

    // **送信待ちを先に見る。** 未送信の回答は Pleasanter にまだ無い
    const pending = await loadPendingAnswers(publicId, responseToken);
    if (pending) {
      for (const answer of pending) {
        answers[answer.questionId] = {
          values: answer.values ?? [],
          otherText: answer.otherText ?? '',
        };
      }
    }

    screen = 'answering';
  }

  /** ページ遷移時に、そのページ分だけ見る。 */
  function checkCurrentPage(): boolean {
    if (!currentPage) return true;
    errors = validatePage(currentPage.questions, answers);
    return Object.keys(errors).length === 0;
  }

  function goNext() {
    if (!checkCurrentPage()) return;
    pageIndex = Math.min(pageIndex + 1, pages.length - 1);
    errors = {};
    window.scrollTo({ top: 0 });
  }

  function goBack() {
    pageIndex = Math.max(pageIndex - 1, 0);
    errors = {};
    window.scrollTo({ top: 0 });
  }

  function toPayload(): PayloadAnswer[] {
    return Object.entries(answers)
      .filter(([questionId]) => {
        const question = pages.flatMap((page) => page.questions).find((q) => q.questionId === questionId);
        return question !== undefined && !isDisplayOnly(question);
      })
      .map(([questionId, answer]) => ({
        questionId,
        values: answer.values,
        otherText: answer.otherText === '' ? undefined : answer.otherText,
      }));
  }

  async function submit() {
    if (!checkCurrentPage()) return;

    submitting = true;
    submitFailed = false;

    try {
      const result = await submitAnswers(publicId, responseToken, toPayload());
      if (result.accepted) {
        screen = 'completed';
        return;
      }

      if (result.rejection) {
        rejection = result.rejection;
        screen = 'rejected';
        return;
      }

      // **入力内容は消さない。** 失われたら再入力してもらう以外に手が無い
      submitFailed = true;
      if (result.errors) {
        const mapped: Record<string, string> = {};
        for (const [questionId, codes] of Object.entries(result.errors)) {
          mapped[questionId] = codes.join(' / ');
        }
        errors = mapped;
      }
    } catch {
      submitFailed = true;
    } finally {
      submitting = false;
    }
  }

  function answerAgain() {
    localStorage.removeItem(`questionnaire.token.${publicId}`);
    location.reload();
  }

  const rejectionMessage: Record<RejectionReason, string> = {
    notStarted: 'このアンケートはまだ受付を開始していません。',
    closed: 'このアンケートの受付は終了しました。',
    suspended: 'このアンケートは現在受付を停止しています。',
    notFound: 'このアンケートは見つかりませんでした。URL をご確認ください。',
  };
</script>

<main>
  {#if screen === 'loading'}
    <p class="status">読み込んでいます…</p>
  {:else if screen === 'rejected'}
    <!-- **理由を明示する。「エラー」で済ませない** -->
    <h1>{rejection === 'notFound' ? 'アンケートが見つかりません' : '受付時間外です'}</h1>
    <p class="status">{rejectionMessage[rejection ?? 'notFound']}</p>
  {:else if screen === 'error'}
    <h1>URL が正しくありません</h1>
    <p class="status">アンケートの URL をご確認ください。</p>
  {:else if screen === 'completed' && definition}
    <h1>{text(definition.confirmationMessage) || '回答を受け付けました'}</h1>
    <p class="status">ご協力ありがとうございました。</p>
    {#if definition.allowEditingAfterSubmit}
      <button type="button" onclick={() => (screen = 'answering')}>回答を編集する</button>
    {/if}
    <button type="button" class="secondary" onclick={answerAgain}>別の回答を送信する</button>
  {:else if definition && currentPage}
    <header>
      <h1>{text(definition.title)}</h1>
      {#if definition.description}<p class="lead">{text(definition.description)}</p>{/if}

      {#if definition.showProgress && pages.length > 1}
        <div
          class="progress"
          role="progressbar"
          aria-valuenow={progress}
          aria-valuemin="0"
          aria-valuemax="100"
          aria-label="回答の進み具合"
        >
          <div class="bar" style={`width:${progress}%`}></div>
        </div>
        <p class="progress-text">{pageIndex + 1} / {pages.length} ページ</p>
      {/if}
    </header>

    {#if currentPage.title}<h2>{text(currentPage.title)}</h2>{/if}
    {#if currentPage.description}<p class="lead">{text(currentPage.description)}</p>{/if}

    <form onsubmit={(event) => event.preventDefault()}>
      {#each currentPage.questions as question (question.questionId)}
        <QuestionField
          {question}
          bind:answer={answers[question.questionId]}
          error={errors[question.questionId]}
        />
      {/each}

      {#if submitFailed}
        <p class="error" role="alert">
          送信できませんでした。入力内容はそのままです。少し時間を置いてもう一度お試しください。
        </p>
      {/if}

      <nav class="actions">
        {#if pageIndex > 0}
          <button type="button" class="secondary" onclick={goBack}>戻る</button>
        {/if}
        {#if isLastPage}
          <button type="button" onclick={submit} disabled={submitting}>
            {submitting ? '送信しています…' : '送信する'}
          </button>
        {:else}
          <button type="button" onclick={goNext}>次へ</button>
        {/if}
      </nav>
    </form>
  {/if}
</main>

<style lang="scss">
  :global(:root) {
    --border: #d0d5dd;
    --muted: #667085;
    --error: #b42318;
    --accent: #175cd3;
    --bg: #f9fafb;
  }

  :global(body) {
    margin: 0;
    background: var(--bg);
    font-family: system-ui, sans-serif;
    color: #101828;
    line-height: 1.6;
  }

  main {
    max-width: 46rem;
    margin: 0 auto;
    padding: 2rem 1rem 4rem;
  }

  h1 {
    font-size: 1.5rem;
    margin: 0 0 0.5rem;
  }

  h2 {
    font-size: 1.15rem;
    margin: 1.5rem 0 0.5rem;
  }

  .lead {
    color: var(--muted);
    margin: 0 0 1rem;
  }

  .status {
    color: var(--muted);
  }

  .progress {
    height: 6px;
    background: var(--border);
    border-radius: 3px;
    overflow: hidden;
    margin: 1rem 0 0.25rem;
  }

  .bar {
    height: 100%;
    background: var(--accent);
    transition: width 0.2s ease;
  }

  .progress-text {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 1.5rem;
  }

  .actions {
    display: flex;
    gap: 0.75rem;
    justify-content: flex-end;
    margin-top: 1.5rem;
  }

  button {
    font: inherit;
    padding: 0.6rem 1.25rem;
    border-radius: 6px;
    border: 1px solid var(--accent);
    background: var(--accent);
    color: #fff;
    cursor: pointer;

    &:disabled {
      opacity: 0.6;
      cursor: progress;
    }

    &.secondary {
      background: #fff;
      color: var(--accent);
    }
  }

  .error {
    color: var(--error);
  }
</style>
