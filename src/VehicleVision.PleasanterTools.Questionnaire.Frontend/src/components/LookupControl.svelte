<script lang="ts">
  import { tick } from 'svelte';
  import { DEFAULT_LANGUAGE, type Language } from '../lib/i18n/language';
  import { translator } from '../lib/i18n/messages';
  import {
    keySnapshot,
    keyValues,
    LOOKUP_FAILURE_MESSAGES,
    LookupRequestTracker,
    missingKeyQuestionIds,
    planApply,
    restoreFocus,
    searchLookup,
    type LookupApplyPlan,
    type LookupCandidate,
    type LookupPublicDefinition,
    type LookupResponse,
    type LookupSearchResult,
    type LookupSessionHolder,
  } from '../lib/lookup';
  import type { AnswerState, LocalizedText, Question } from '../lib/types';
  import { text as localizedText } from '../lib/types';

  /**
   * ルックアップの検索ボタンと、候補・上書き確認のモーダル（Issue #871・仕様書 2・2.1 章）。
   *
   * **取得値はすべて Svelte の文字列補間で描く**（HTML・記法として解釈しない）。
   * **何も選ばない・キャンセル・失敗では、入力を 1 つも変えない。**
   */
  interface Props {
    lookup: LookupPublicDefinition;
    /** 表示中の公開版の番号。サーバが現在の版と照合する。 */
    version: number;
    publicId: string;
    language: Language;
    fallbackLanguage?: Language;
    questions: ReadonlyMap<string, Question>;
    answers: Record<string, AnswerState | undefined>;
    /** いま出している設問。**出ていない入力先へは入れない。** */
    visible: ReadonlySet<string>;
    /** 検索用トークンの入れ物。`search` を渡すときは使わない。 */
    sessions?: LookupSessionHolder;
    /**
     * 検索の差し替え。**管理画面のプレビュー専用**（保存済みの下書きを引く別経路。Issue #880・仕様書 U9）。
     * 無ければ公開 API（トークン付き）を使う。
     */
    search?: (keys: Record<string, string>) => Promise<LookupSearchResult>;
    /** 入力先へ値を書き込む。呼び出し側が通常の入力と同じ扱いにする。 */
    onapply: (changes: { questionId: string; value: string }[]) => void;
  }

  let {
    lookup,
    version,
    publicId,
    language,
    fallbackLanguage = DEFAULT_LANGUAGE,
    questions,
    answers,
    visible,
    sessions,
    search,
    onapply,
  }: Props = $props();

  type DialogMode = 'choose' | 'confirm';
  interface DialogState {
    mode: DialogMode;
    response: LookupResponse;
    selected: string | null;
    /** 要求したときのキー。キーが変わったらこのモーダルは無効にする。 */
    snapshot: string;
  }

  const t = $derived(translator(language));
  const text = (value: LocalizedText | null | undefined) =>
    localizedText(value ?? undefined, language, fallbackLanguage);

  const tracker = new LookupRequestTracker();
  const baseId = $derived(`lookup-${lookup.lookupId}`);
  const titleId = $derived(`${baseId}-title`);
  const descriptionId = $derived(`${baseId}-description`);
  const hintId = $derived(`${baseId}-hint`);
  const selectHintId = $derived(`${baseId}-select-hint`);

  const missing = $derived(missingKeyQuestionIds(lookup, answers));
  const snapshot = $derived(keySnapshot(lookup, answers));

  let searching = $state(false);
  /** 進行状況・件数・0 件・反映完了。`role="status"` で読み上げる。 */
  let status = $state('');
  let error = $state('');
  let warnings = $state<string[]>([]);
  let dialog = $state<DialogState | null>(null);
  let button = $state<HTMLButtonElement | null>(null);
  let dialogElement = $state<HTMLDialogElement | null>(null);

  const label = $derived(text(lookup.buttonLabel) || t('lookup.search'));

  function questionTitle(questionId: string): string {
    const question = questions.get(questionId);
    return question ? text(question.title) : questionId;
  }

  const missingNames = $derived(missing.map(questionTitle).join(' / '));

  /** 閉じて、起点のボタンへフォーカスを戻す。起点が無ければ本文の先頭へ。 */
  async function closeDialog(restore: boolean) {
    if (dialogElement?.open) {
      dialogElement.close();
    }
    dialog = null;

    if (restore) {
      await tick();
      const main = document.querySelector<HTMLElement>('main');
      main?.setAttribute('tabindex', '-1');
      restoreFocus(button, main);
    }
  }

  $effect(() => {
    // **キーが変わったら、開いているモーダルを無効にする。** 古い候補を選ばせない
    if (dialog !== null && dialog.snapshot !== snapshot) {
      void closeDialog(false);
      status = t('lookup.stale');
    }
  });

  $effect(() => {
    // 開いた・モードが変わったら、フォーカスを移す（選択肢 → 最初のラジオ、確認 → 見出し）
    const mode = dialog?.mode;
    const element = dialogElement;
    if (!element || !mode) {
      return;
    }
    if (!element.open) {
      element.showModal();
    }
    void tick().then(() => {
      const target =
        mode === 'choose'
          ? element.querySelector<HTMLElement>('input[type="radio"]')
          : element.querySelector<HTMLElement>('[data-initial]');
      target?.focus();
    });
  });

  function failureMessage(result: Extract<LookupSearchResult, { ok: false }>): string {
    if (result.failure === 'rateLimited') {
      return result.retryAfterSeconds === null
        ? t('lookup.rateLimitedLater')
        : t('lookup.rateLimited', { seconds: result.retryAfterSeconds });
    }

    const message = t(LOOKUP_FAILURE_MESSAGES[result.failure]);
    if (result.failure === 'invalidKeys' && result.fieldIds.length > 0) {
      return `${message} (${result.fieldIds.map(questionTitle).join(' / ')})`;
    }
    return message;
  }

  function plan(response: LookupResponse, candidate: LookupCandidate): LookupApplyPlan {
    // **反映の直前の状態で計画を立てる。** 入力先の表示状態・既存の入力は今の値を見る
    return planApply(lookup, candidate, response.warnings, answers, questions, visible);
  }

  function describeWarnings(result: LookupApplyPlan): string[] {
    return result.warnings.map((warning) =>
      t(warning.reason === 'choiceUnavailable' ? 'lookup.choiceUnavailable' : 'lookup.valueInvalid', {
        question: questionTitle(warning.questionId),
      }),
    );
  }

  async function apply(result: LookupApplyPlan) {
    if (result.changes.length > 0) {
      onapply(result.changes);
    }
    warnings = describeWarnings(result);
    status = result.changes.length > 0 ? t('lookup.applied') : t('lookup.appliedNothing');
    await closeDialog(true);
  }

  /** 1 件を選んだあと。既存入力を上書きするなら確認へ、しないならそのまま反映する。 */
  async function proceed(response: LookupResponse, candidate: LookupCandidate, requested: string) {
    const result = plan(response, candidate);
    if (result.overwrites.length > 0) {
      dialog = { mode: 'confirm', response, selected: candidate.candidateKey, snapshot: requested };
      status = '';
      return;
    }
    await apply(result);
  }

  async function run() {
    if (searching || missing.length > 0) {
      return;
    }

    const requested = snapshot;
    const keys = keyValues(lookup, answers);
    const ticket = tracker.begin();

    searching = true;
    status = t('lookup.searching');
    error = '';
    warnings = [];
    await closeDialog(false);

    try {
      const token = search ? '' : ((await sessions?.get()) ?? null);
      const result: LookupSearchResult = search
        ? await search(keys)
        : token === null
          ? { ok: false, failure: 'network', fieldIds: [], retryAfterSeconds: null }
          : await searchLookup(publicId, lookup.lookupId, version, keys, token);

      // **古い応答を適用しない。** 要求のあとでキーが変わっていたら捨てる
      if (!tracker.isCurrent(ticket, requested, snapshot)) {
        status = t('lookup.stale');
        return;
      }

      if (!result.ok) {
        if (result.failure === 'sessionInvalid') {
          sessions?.discard();
        }
        status = '';
        error = failureMessage(result);
        return;
      }

      const { response } = result;
      if (response.outcome === 'notFound') {
        status = t('lookup.notFound');
      } else if (response.outcome === 'single') {
        const [only] = response.candidates;
        if (only) {
          await proceed(response, only, requested);
        }
      } else {
        status = t('lookup.candidateCount', { count: response.candidates.length });
        dialog = { mode: 'choose', response, selected: null, snapshot: requested };
      }
    } finally {
      searching = false;
    }
  }

  const selectedCandidate = $derived(
    dialog?.response.candidates.find((candidate) => candidate.candidateKey === dialog?.selected),
  );

  const overwriteNames = $derived(
    dialog && selectedCandidate
      ? plan(dialog.response, selectedCandidate).overwrites.map(questionTitle).join(' / ')
      : '',
  );

  async function confirmChoice() {
    if (!dialog || !selectedCandidate) {
      return;
    }

    const response = dialog.response;
    const result = plan(response, selectedCandidate);
    if (dialog.mode === 'choose' && result.overwrites.length > 0) {
      dialog = { ...dialog, mode: 'confirm' };
      return;
    }
    await apply(result);
  }

  function cancel() {
    // **何も変えずに閉じる。** 状態を読み上げに残す
    status = '';
    void closeDialog(true);
  }

  /** Tab / Shift+Tab をモーダルの中で循環させる。Escape は `oncancel` が扱う。 */
  function trapTab(event: KeyboardEvent) {
    if (event.key !== 'Tab' || !dialogElement) {
      return;
    }

    const focusable = [
      ...dialogElement.querySelectorAll<HTMLElement>(
        'input:not([disabled]), button:not([disabled]), [tabindex="0"]',
      ),
    ];
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (!first || !last) {
      return;
    }

    const active = document.activeElement;
    if (event.shiftKey && (active === first || active === dialogElement || active?.id === titleId)) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }

  function headingOf(field: { displayId: string; heading?: LocalizedText | null }): string {
    return text(field.heading) || field.displayId;
  }
</script>

<div class="lookup">
  <button
    type="button"
    class="lookup-button"
    bind:this={button}
    disabled={searching || missing.length > 0}
    aria-describedby={missing.length > 0 ? hintId : undefined}
    onclick={run}
  >
    {searching ? t('lookup.searching') : label}
  </button>

  {#if missing.length > 0}
    <span class="hint" id={hintId}>{t('lookup.keysMissing', { fields: missingNames })}</span>
  {/if}

  <!-- **常に DOM に置く。** 後から現れる領域は読み上げに拾われないことがある -->
  <p class="status" role="status">{status}</p>

  {#if error}
    <p class="error" role="alert">{error}</p>
  {/if}

  {#if warnings.length > 0}
    <ul class="warnings" role="alert">
      {#each warnings as warning (warning)}
        <li>{warning}</li>
      {/each}
    </ul>
  {/if}
</div>

{#if dialog}
  <dialog
    bind:this={dialogElement}
    class="lookup-dialog"
    aria-labelledby={titleId}
    aria-describedby={descriptionId}
    oncancel={(event) => {
      event.preventDefault();
      cancel();
    }}
    onkeydown={trapTab}
  >
    {#if dialog.mode === 'choose'}
      <h3 id={titleId} tabindex="-1">{t('lookup.chooseTitle')}</h3>
      <p id={descriptionId} class="dialog-lead">
        {t('lookup.candidateCount', { count: dialog.response.candidates.length })}
      </p>
      <fieldset class="candidates">
        <legend class="visually-hidden">{t('lookup.chooseTitle')}</legend>
        {#each dialog.response.candidates as candidate, index (candidate.candidateKey)}
          <label class="candidate" class:selected={dialog.selected === candidate.candidateKey}>
            <input
              type="radio"
              name={`${baseId}-candidate`}
              value={candidate.candidateKey}
              checked={dialog.selected === candidate.candidateKey}
              onchange={() => {
                if (dialog) {
                  dialog = { ...dialog, selected: candidate.candidateKey };
                }
              }}
            />
            <span class="candidate-body">
              <span class="candidate-no">{t('lookup.candidateLabel', { number: index + 1 })}</span>
              {#each lookup.displayFields as field (field.displayId)}
                <span class="candidate-field">
                  <span class="candidate-heading">{headingOf(field)}</span>
                  <span class="candidate-value">{candidate.display[field.displayId] ?? ''}</span>
                </span>
              {/each}
            </span>
          </label>
        {/each}
      </fieldset>
      <div class="dialog-actions">
        <button type="button" class="secondary" onclick={cancel}>{t('lookup.cancel')}</button>
        <button
          type="button"
          disabled={selectedCandidate === undefined}
          aria-describedby={selectedCandidate === undefined ? selectHintId : undefined}
          onclick={confirmChoice}
        >
          {t('lookup.apply')}
        </button>
      </div>
      {#if selectedCandidate === undefined}
        <p class="hint dialog-hint" id={selectHintId}>{t('lookup.selectFirst')}</p>
      {/if}
    {:else}
      <h3 id={titleId} tabindex="-1" data-initial>{t('lookup.confirmTitle')}</h3>
      <p id={descriptionId} class="dialog-lead">{t('lookup.confirmOverwrite')}</p>
      {#if overwriteNames !== ''}
        <p class="dialog-lead">{t('lookup.confirmFields', { fields: overwriteNames })}</p>
      {/if}
      <div class="dialog-actions">
        <button type="button" class="secondary" onclick={cancel}>{t('lookup.cancel')}</button>
        <button type="button" onclick={confirmChoice}>{t('lookup.confirmApply')}</button>
      </div>
    {/if}
  </dialog>
{/if}

<style lang="scss">
  .lookup {
    margin-top: var(--space-3);
  }

  button {
    box-sizing: border-box;
    min-height: var(--control-h);
    font: inherit;
    font-weight: 700;
    padding: 0 var(--space-4);
    border-radius: var(--radius-md);
    border: 1px solid var(--accent);
    background: var(--accent);
    color: var(--accent-text);
    cursor: pointer;

    /* **無効は、色を落として枠だけにする**（透過にしない） */
    &:disabled {
      background: var(--bg);
      color: var(--muted);
      border-color: var(--border);
      cursor: not-allowed;
    }

    &.secondary {
      background: var(--surface);
      color: var(--accent);
    }

    &:focus-visible {
      outline: var(--focus-outline);
      outline-offset: 2px;
    }
  }

  .hint {
    display: block;
    margin-top: var(--space-1);
    color: var(--muted);
    font-size: 0.85rem;
  }

  .status {
    margin: var(--space-2) 0 0;
    color: var(--muted);
    font-size: 0.9rem;

    &:empty {
      margin: 0;
    }
  }

  .error {
    margin: var(--space-2) 0 0;
    color: var(--error);
    font-size: 0.9rem;
  }

  /* **色だけに頼らない。** 左端の太い線と文字で警告だと分かる */
  .warnings {
    margin: var(--space-2) 0 0;
    padding: var(--space-2) var(--space-3) var(--space-2) var(--space-6);
    border-left: 4px solid var(--warning-border);
    background: var(--warning-surface);
    color: var(--warning-text);
    font-size: 0.9rem;
  }

  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }

  .lookup-dialog {
    box-sizing: border-box;
    width: min(32rem, calc(100vw - 2rem));
    max-height: calc(100vh - 2rem);
    overflow-y: auto;
    padding: var(--space-6);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-md);
    background: var(--surface);
    color: var(--text);
    box-shadow: var(--shadow-lg);

    &::backdrop {
      background: var(--overlay-bg, rgb(0 0 0 / 50%));
    }
  }

  h3 {
    margin: 0;
    font-size: 1.15rem;

    &:focus-visible {
      outline: var(--focus-outline);
      outline-offset: 2px;
    }
  }

  .dialog-lead {
    margin: var(--space-2) 0 0;
  }

  .candidates {
    margin: var(--space-3) 0 0;
    padding: 0;
    border: none;
  }

  .candidate {
    display: flex;
    align-items: flex-start;
    gap: var(--space-3);
    margin-bottom: var(--space-2);
    padding: var(--space-3);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    cursor: pointer;

    /* **選択は枠線の太さと左端の線でも示す**（色だけにしない） */
    &.selected {
      border-color: var(--accent);
      border-left-width: 6px;
    }

    input {
      margin-top: 0.3rem;
    }

    &:focus-within {
      outline: var(--focus-outline);
      outline-offset: 2px;
    }
  }

  .candidate-body {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .candidate-no {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .candidate-field {
    display: flex;
    flex-wrap: wrap;
    gap: var(--space-1) var(--space-3);
  }

  .candidate-heading {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .dialog-actions {
    display: flex;
    flex-wrap: wrap;
    justify-content: flex-end;
    gap: var(--space-3);
    margin-top: var(--space-4);
  }

  .dialog-hint {
    text-align: right;
  }
</style>
