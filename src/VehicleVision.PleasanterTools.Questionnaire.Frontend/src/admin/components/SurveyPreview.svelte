<script lang="ts">
  import QuestionField from '../../components/QuestionField.svelte';
  import { toSteps, tracePath } from '../../lib/flow';
  import { translator } from '../../lib/i18n/messages';
  import { LANGUAGE_NAMES, SUPPORTED_LANGUAGES, type Language } from '../../lib/i18n/language';
  import type { AnswerState, SurveyDefinition as AnswerDefinition } from '../../lib/types';
  import { text } from '../../lib/types';
  import { validatePage } from '../../lib/validation';
  import type { SurveyDefinition } from '../lib/types';
  import { language as adminLanguage, t } from '../lib/i18n/state.svelte';
  import { applyTheme } from '../../lib/theme';

  /**
   * 公開する前に、回答画面と同じ描き方で確かめる。
   *
   * **回答画面と同じ部品・同じ経路の求め方を使う**
   * （`components/QuestionField.svelte` と `lib/flow.ts`）。
   * 別に描くと「プレビューでは出たのに本番では出ない」が起きる
   * （`_documents/画面設計.md` 2 章「分岐も実際に辿れること」）。
   *
   * **保存も送信もしない。** 回答トークンも送信チケットも取らないので、
   * 送信待ちの行も監査ログも増えない。
   *
   * **下書きをそのまま見る。** 保存前の編集内容で確かめられないと、
   * 「保存して公開してから直す」ことになる。
   */
  interface Props {
    /** 編集中の定義。**保存前のものをそのまま受け取る。** */
    definition: SurveyDefinition;
    /**
     * ヘッダ画像の URL。**無ければ出さない。**
     *
     * **編集画面から渡してもらう。** 回答画面の口は公開中の版が指す画像しか
     * 返さないので、上げたばかりの画像はここからは見えない。
     */
    headerImageUrl?: string | null;
    onclose: () => void;
  }

  let { definition, headerImageUrl = null, onclose }: Props = $props();

  /**
   * テーマを写す枠（Issue #56）。
   *
   * **`:root` へは書かない。** 回答画面と違い、ここは管理画面の中なので、
   * 全体へ書くと編集画面そのものの色まで変わる。
   * **枠へ書けば、その中だけが変わる**（カスタムプロパティは下へ伝わる）。
   */
  let paper = $state<HTMLElement>();

  $effect(() => {
    applyTheme(paper, definition.theme);
  });

  /**
   * 見る言語。
   *
   * **管理画面の言語を初期値にする。** ただし切り替えられるようにする。
   * 訳を入れたつもりで入っていない箇所は、切り替えて見ないと気付けない。
   */
  let language = $state<Language>(adminLanguage());

  let stepIndex = $state(0);
  let answers = $state<Record<string, AnswerState>>({});
  let errors = $state<Record<string, string>>({});

  /**
   * 回答画面の型として見る。
   *
   * **管理画面と回答画面で同じ形の型を別に持っている**（束を分けているため。
   * `_documents/多言語対応方針.md` 4 章）。中身は同じなので、ここで読み替える。
   */
  const answerDefinition = $derived(definition as unknown as AnswerDefinition);

  const formText = $derived(translator(language));
  const path = $derived(tracePath(answerDefinition, answers));
  const steps = $derived(toSteps(path, definition.displayMode));
  const currentStep = $derived(steps[Math.min(stepIndex, Math.max(steps.length - 1, 0))]);
  const isLastStep = $derived(stepIndex >= steps.length - 1);

  const progress = $derived(
    steps.length === 0
      ? 0
      : Math.round(((Math.min(stepIndex, steps.length - 1) + 1) / steps.length) * 100),
  );

  $effect(() => {
    // **経路が縮んだら、行き過ぎた位置を戻す**
    if (steps.length > 0 && stepIndex > steps.length - 1) {
      stepIndex = steps.length - 1;
    }
  });

  $effect(() => {
    // **通らなくなった設問の入力を消す。** 回答画面と同じ振る舞いにする
    for (const [questionId, answer] of Object.entries(answers)) {
      if (path.visible.has(questionId)) {
        continue;
      }

      if (answer.values.length > 0 || answer.otherText !== '') {
        answers[questionId] = { values: [], otherText: '' };
      }
    }
  });

  function ensure(questionId: string): AnswerState {
    answers[questionId] ??= { values: [], otherText: '' };
    return answers[questionId];
  }

  function goNext() {
    if (currentStep) {
      errors = validatePage(currentStep.questions, answers, formText);
      if (Object.keys(errors).length > 0) {
        return;
      }
    }

    stepIndex = Math.min(stepIndex + 1, Math.max(steps.length - 1, 0));
    errors = {};
  }

  function goBack() {
    stepIndex = Math.max(stepIndex - 1, 0);
    errors = {};
  }

  function restart() {
    stepIndex = 0;
    answers = {};
    errors = {};
  }
</script>

<section class="preview">
  <header class="head">
    <button type="button" class="secondary" onclick={onclose}>{t('preview.close')}</button>
    <h1>{t('preview.title')}</h1>

    <label class="language">
      <span class="visually-hidden">{t('preview.language')}</span>
      <select value={language} onchange={(event) => (language = event.currentTarget.value as Language)}>
        {#each SUPPORTED_LANGUAGES as option (option)}
          <option value={option}>{LANGUAGE_NAMES[option]}</option>
        {/each}
      </select>
    </label>

    <button type="button" class="secondary" onclick={restart}>{t('preview.restart')}</button>
  </header>

  <!-- **保存されないことを画面に出す。** 本物と見分けが付かないと事故になる -->
  <p class="notice" role="status">{t('preview.notice')}</p>

  <div class="paper" bind:this={paper}>
    <!-- **飾り。** 回答画面と同じく `alt` は空にする -->
    {#if headerImageUrl}
      <img class="header-image" src={headerImageUrl} alt="" />
    {/if}
    <h2>{text(definition.title, language)}</h2>
    {#if definition.description}
      <p class="lead">{text(definition.description, language)}</p>
    {/if}

    {#if definition.showProgress && steps.length > 1}
      <div
        class="progress"
        role="progressbar"
        aria-valuenow={progress}
        aria-valuemin="0"
        aria-valuemax="100"
      >
        <div class="bar" style={`width:${progress}%`}></div>
      </div>
      <p class="progress-text">
        {t('preview.stepCount', {
          current: Math.min(stepIndex, steps.length - 1) + 1,
          total: steps.length,
        })}
      </p>
    {/if}

    {#if steps.length === 0}
      <p class="empty">{t('preview.empty')}</p>
    {:else}
      {#if currentStep?.page.title}
        <h3>{text(currentStep.page.title, language)}</h3>
      {/if}
      {#if currentStep?.page.description}
        <p class="lead">{text(currentStep.page.description, language)}</p>
      {/if}

      {#each currentStep?.questions ?? [] as question (question.questionId)}
        <QuestionField
          question={question as never}
          {language}
          bind:answer={
            () => ensure(question.questionId), (value) => (answers[question.questionId] = value)
          }
          error={errors[question.questionId]}
        />
      {/each}

      <nav class="actions">
        {#if stepIndex > 0}
          <button type="button" class="secondary" onclick={goBack}>{t('preview.back')}</button>
        {/if}
        {#if isLastStep}
          <!-- **送信の釦は出さない。** 押せる釦があると、押した人は送れたと思う -->
          <span class="end">{t('preview.end')}</span>
        {:else}
          <button type="button" onclick={goNext}>{t('preview.next')}</button>
        {/if}
      </nav>
    {/if}
  </div>
</section>

<style lang="scss">
  .head {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  h1 {
    font-size: 1.25rem;
    margin: 0;
  }

  .language {
    margin-left: auto;
  }

  .language select {
    font: inherit;
    font-size: 0.85rem;
    padding: 0.2rem 0.35rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: #fff;
  }

  /* **本物と見分けが付くようにする** */
  .notice {
    margin: 0.75rem 0 1rem;
    padding: 0.5rem 0.75rem;
    border-left: 3px solid var(--accent);
    background: #eef4ff;
    color: #1f2a44;
    font-size: 0.9rem;
  }

  .paper {
    /* **回答画面の既定値をここへ置き直す**（Issue #56）。
       テーマがあれば `applyTheme` が同じ名前をこの要素の style へ入れて上書きする
       （要素に直接書いた値が、この規則より強い）。
       **管理画面の `:root` の値をそのまま使わない。** 地の色が別物になる */
    --bg: #fff;
    --text: #101828;
    /* **釦の文字色と入力欄の地**（Issue #109）。回答画面の `:root` と同じ値 */
    --accent-text: #fff;
    --surface: #fff;
    --font: system-ui, sans-serif;

    max-width: 40rem;
    padding: 1.5rem;
    background: var(--bg);
    color: var(--text);
    font-family: var(--font);
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  .header-image {
    display: block;
    width: 100%;
    max-height: 10rem;
    object-fit: cover;
    border-radius: 6px;
    margin-bottom: 1rem;
  }

  h2 {
    margin-top: 0;
    font-size: 1.4rem;
  }

  h3 {
    margin-top: 1.5rem;
    font-size: 1.1rem;
  }

  .lead {
    color: var(--muted);
  }

  .progress {
    height: 6px;
    border-radius: 3px;
    background: #eaecf0;
    overflow: hidden;
  }

  .bar {
    height: 100%;
    background: var(--accent);
  }

  .progress-text {
    margin: 0.35rem 0 0;
    color: var(--muted);
    font-size: 0.8rem;
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-top: 1.5rem;
  }

  .end {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .empty {
    color: var(--muted);
  }

  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }
</style>
