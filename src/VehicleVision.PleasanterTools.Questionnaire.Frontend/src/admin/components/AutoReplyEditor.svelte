<script lang="ts">
  import { tick } from 'svelte';
  import {
    previewAutoReply,
    revokeEditLinks,
    sendAutoReplyTest,
    type AutoReplyPreview,
  } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import {
    text,
    withText,
    type AutoReplySettings,
    type Question,
    type SurveyDefinition,
  } from '../lib/types';
  import type { Language } from '../../lib/i18n/language';

  const PREVIEW_DELAY_MS = 300;
  const KEYWORDS = [
    'title',
    'submittedAt',
    'acceptTo',
    'answers',
    'formUrl',
    'editUrl',
    'editUrlExpiresAt',
    'assetsUrl',
    'assetsUrlExpiresAt',
  ] as const;

  /**
   * 回答者への自動返信メールを設定する（Issue #189）。
   *
   * ⚠️ **既定は送らない。** 完全匿名が前提のアプリで、回答者のメールアドレスを
   * 扱う唯一の機能なので、明示的に有効にしたときだけ送る。
   *
   * **宛先は「メールアドレス形式の設問への回答」からしか採れない。**
   * 勝手に集める経路を作らないため、選べる設問が無ければその旨を出すだけにする。
   *
   * **保存は編集画面がまとめて行う。** ここは定義の `autoReply` を書き換えるだけ。
   */
  interface Props {
    /** このアンケート。**再編集リンクの一括失効に要る**（Issue #202）。 */
    surveyId: string;
    /** 編集中の定義。**保存前の内容をプレビューへ渡す。** */
    definition: SurveyDefinition;
    /** 回答の編集を許しているか。**許していなければ再編集リンクは付けられない。** */
    allowEditing: boolean;
    /** 今の設定。**無ければ送らない。** */
    autoReply: AutoReplySettings | null | undefined;
    /** 全ページの設問。**宛先に選べるものを絞るのに使う。** */
    questions: Question[];
    /** 入力欄が書き込む言語。 */
    editing: Language;
    /** サーバ側でメールの送信が有効か。**無効でも設定は保存できる。** */
    mailEnabled: boolean;
    /** 試し送信の宛先。**ログイン中の管理者自身に固定する。** */
    testRecipient: string;
    /** ログイン ID をメールアドレスとして使えるか。 */
    testRecipientAvailable: boolean;
    onchange: (next: AutoReplySettings | null) => void;
  }

  let {
    surveyId,
    definition,
    allowEditing,
    autoReply,
    questions,
    editing,
    mailEnabled,
    testRecipient,
    testRecipientAvailable,
    onchange,
  }: Props = $props();

  /** 一括失効の結果。**押したことが分かるように出す。** */
  let revoked = $state('');
  let revoking = $state(false);
  let bodyInput = $state<HTMLTextAreaElement>();
  let preview = $state<AutoReplyPreview | null>(null);
  let previewError = $state('');
  let testSending = $state(false);
  let testResult = $state('');
  let testFailed = $state(false);

  async function revoke() {
    revoking = true;
    const result = await revokeEditLinks(surveyId);
    revoking = false;
    revoked = result.ok
      ? t('autoReply.revokeEditLinksDone', { count: result.value.revoked })
      : result.message;
  }

  async function sendTest() {
    testSending = true;
    testResult = '';
    testFailed = false;
    const result = await sendAutoReplyTest({ ...definition, autoReply }, editing);
    testSending = false;
    if (result.ok) {
      testResult = t('autoReply.testQueued', { recipient: testRecipient });
      return;
    }

    testFailed = true;
    testResult = result.message;
  }

  /**
   * 宛先に選べる設問。
   *
   * **記述式（1 行）で、形式がメールアドレスのものだけ。**
   * 何を書いても通る欄を宛先にすると、送信は必ず失敗してデッドレターが溜まる
   * （サーバ側の `AutoReplyValidator` と同じ判定）。
   */
  const addressQuestions = $derived(
    questions.filter((question) => question.type === 'Text' && question.settings?.format === 'Email'),
  );

  const enabled = $derived(autoReply?.enabled ?? false);
  const usesEditLink = $derived(
    usesKeyword('editUrl') || usesKeyword('editUrlExpiresAt'),
  );
  const previewSource = $derived(
    enabled ? JSON.stringify({ definition: { ...definition, autoReply }, language: editing }) : '',
  );

  $effect(() => {
    const source = previewSource;
    if (source === '') {
      preview = null;
      previewError = '';
      return;
    }

    const timer = setTimeout(async () => {
      const request = JSON.parse(source) as {
        definition: SurveyDefinition;
        language: Language;
      };
      const result = await previewAutoReply(request.definition, request.language);
      if (source !== previewSource) return;

      if (result.ok) {
        preview = result.value;
        previewError = '';
      } else {
        preview = null;
        previewError = result.message;
      }
    }, PREVIEW_DELAY_MS);

    return () => clearTimeout(timer);
  });

  /**
   * 自動返信を有効にできるか。
   *
   * ⚠️ **メールアドレスの欄があるアンケートでだけ有効にできる**（Issue #189）。
   * 宛先は回答からしか採らないので、欄が無ければ**有効にしても 1 通も出ない。**
   * 「設定したのに届かない」を作らないため、そもそも入れさせない。
   *
   * **既に有効なら触らせ続ける。** 有効にしたあとで欄を消した場合、
   * 釦を塞ぐと直すことも止めることもできなくなる。
   */
  const canEnable = $derived(addressQuestions.length > 0 || enabled);

  /**
   * 設定を書き換える。
   *
   * **無効に戻したら `null` にする。** 空の設定を持たせると、
   * 触っていないアンケートの定義にも `autoReply` が載り、公開のたびに版の JSON が変わる
   * （`ThemeEditor` と同じ理由）。
   */
  function update(patch: Partial<AutoReplySettings>) {
    const next: AutoReplySettings = { enabled: false, ...(autoReply ?? {}), ...patch };
    onchange(next.enabled ? next : null);
  }

  function usesKeyword(keyword: string): boolean {
    const pattern = new RegExp(`\\{\\{\\s*${keyword}\\s*\\}\\}`);
    return [autoReply?.subject, autoReply?.body].some(
      (localized) => localized && Object.values(localized).some((value) => pattern.test(value)),
    );
  }

  async function insertKeyword(keyword: string) {
    const marker = `{{${keyword}}}`;
    const current = text(autoReply?.body, editing);
    const start = bodyInput?.selectionStart ?? current.length;
    const end = bodyInput?.selectionEnd ?? start;
    update({ body: withText(autoReply?.body, current.slice(0, start) + marker + current.slice(end), editing) });

    await tick();
    bodyInput?.focus();
    bodyInput?.setSelectionRange(start + marker.length, start + marker.length);
  }

  function keywordDescription(keyword: (typeof KEYWORDS)[number]): string {
    switch (keyword) {
      case 'title': return t('autoReply.keyword.title');
      case 'submittedAt': return t('autoReply.keyword.submittedAt');
      case 'acceptTo': return t('autoReply.keyword.acceptTo');
      case 'answers': return t('autoReply.keyword.answers');
      case 'formUrl': return t('autoReply.keyword.formUrl');
      case 'editUrl': return t('autoReply.keyword.editUrl');
      case 'editUrlExpiresAt': return t('autoReply.keyword.editUrlExpiresAt');
      case 'assetsUrl': return t('autoReply.keyword.assetsUrl');
      case 'assetsUrlExpiresAt': return t('autoReply.keyword.assetsUrlExpiresAt');
    }
  }

  /**
   * 有効にしたときに、文面の雛形を入れる（Issue #209）。
   *
   * **一度書いたものを上書きしない。** 空の言語にだけ入れる。
   * **編集中の言語にだけ入れる**（`_documents/多言語対応方針.md` 5 章）。
   * 他の言語の文言は触らない。
   */
  function toggle(enabled: boolean) {
    if (!enabled) {
      update({ enabled: false });
      return;
    }

    const patch: Partial<AutoReplySettings> = { enabled: true };

    if (text(autoReply?.subject, editing) === '') {
      patch.subject = withText(autoReply?.subject, t('autoReply.defaultSubject'), editing);
    }

    if (text(autoReply?.body, editing) === '') {
      patch.body = withText(autoReply?.body, t('autoReply.defaultBody'), editing);
    }

    update(patch);
  }
</script>

<section class="auto-reply">
  <h2>{t('autoReply.title')}</h2>
  <p class="hint">{t('autoReply.lead')}</p>

  <label class="toggle" class:unavailable={!canEnable}>
    <input
      type="checkbox"
      checked={enabled}
      disabled={!canEnable}
      onchange={(event) => toggle(event.currentTarget.checked)}
    />
    {t('autoReply.enabled')}
  </label>

  <!-- ⚠️ **メールアドレスの欄が無ければ有効にできない。**
       宛先は回答からしか採らないので、欄が無ければ 1 通も出ない -->
  {#if !canEnable}
    <p class="hint">{t('autoReply.needsEmailQuestion')}</p>
  {/if}

  {#if enabled}
    <!-- **送れない状態を黙って隠さない。** 設定だけ済ませて「送っているつもり」に
         させないため、サーバ側が無効なら必ず出す -->
    {#if !mailEnabled}
      <p class="warning">{t('autoReply.serverDisabled')}</p>
    {/if}

    {#if addressQuestions.length === 0}
      <!-- **有効にしたあとで欄を消した。** 直すか止めるかができる状態は保つ -->
      <p class="warning">{t('autoReply.noEmailQuestion')}</p>
    {:else}
      <label>
        {t('autoReply.toQuestion')}
        <select
          value={autoReply?.toQuestionId ?? ''}
          onchange={(event) => update({ toQuestionId: event.currentTarget.value || null })}
        >
          <option value="">-</option>
          {#each addressQuestions as question (question.questionId)}
            <option value={question.questionId}>
              {text(question.title, editing) || question.questionId}
            </option>
          {/each}
        </select>
      </label>
      <p class="hint">{t('autoReply.toQuestionHint')}</p>
    {/if}

    <label>
      {t('autoReply.subject')}
      <input
        type="text"
        value={text(autoReply?.subject, editing)}
        oninput={(event) =>
          update({ subject: withText(autoReply?.subject, event.currentTarget.value, editing) })}
      />
    </label>

    <label>
      {t('autoReply.fromAddress')}
      <input type="text" value={preview?.fromAddress ?? ''} readonly />
    </label>
    <p class="hint">{t('autoReply.fromAddressHint')}</p>

    <label>
      {t('autoReply.fromName')}
      <input
        type="text"
        value={text(autoReply?.fromName, editing)}
        oninput={(event) =>
          update({ fromName: withText(autoReply?.fromName, event.currentTarget.value, editing) })}
      />
    </label>
    <p class="hint">{t('autoReply.fromNameHint')}</p>
    <!-- **踏むまで気付けない制約なので画面に出す。** ACS は送信ごとの表示名を変えられない -->
    <p class="hint">{t('autoReply.fromNameAcsNote')}</p>

    <label>
      {t('autoReply.replyToAddress')}
      <input
        type="email"
        value={autoReply?.replyToAddress ?? ''}
        oninput={(event) => update({ replyToAddress: event.currentTarget.value || null })}
      />
    </label>
    <p class="hint">{t('autoReply.replyToAddressHint')}</p>

    <label>
      {t('autoReply.bccAddress')}
      <input
        type="email"
        value={autoReply?.bccAddress ?? ''}
        oninput={(event) => update({ bccAddress: event.currentTarget.value || null })}
      />
    </label>
    <p class="warning">{t('autoReply.bccWarning')}</p>

    <label>
      {t('autoReply.body')}
      <textarea
        bind:this={bodyInput}
        rows="6"
        value={text(autoReply?.body, editing)}
        oninput={(event) =>
          update({ body: withText(autoReply?.body, event.currentTarget.value, editing) })}
      ></textarea>
    </label>
    <p class="hint">{t('autoReply.bodyHint')}</p>
    <p class="hint">{t('autoReply.placeholders')}</p>
    <div class="keywords" aria-label={t('autoReply.keywordList')}>
      {#each KEYWORDS as keyword (keyword)}
        <button type="button" class="keyword" onclick={() => insertKeyword(keyword)}>
          <code>{`{{${keyword}}}`}</code>
          <span>{keywordDescription(keyword)}</span>
        </button>
      {/each}
    </div>

    {#if usesEditLink}
      {#if !allowEditing}
        <!-- **既知だが使えないキーワードは公開のときにも弾かれる。** -->
        <p class="warning">{t('autoReply.editLinkNeedsEditing')}</p>
      {/if}

      <label>
        {t('autoReply.editLinkDays')}
        <input
          type="number"
          min="1"
          max="365"
          value={autoReply?.editLinkDays ?? 7}
          oninput={(event) => update({ editLinkDays: Number(event.currentTarget.value) || 7 })}
        />
      </label>
      <p class="hint">{t('autoReply.editLinkDaysHint')}</p>

      <!-- **漏れたときに、調べずに止められること** -->
      <button type="button" class="secondary" disabled={revoking} onclick={revoke}>
        {t('autoReply.revokeEditLinks')}
      </button>
      <p class="hint">{t('autoReply.revokeEditLinksHint')}</p>
      {#if revoked}<p class="hint">{revoked}</p>{/if}
    {/if}

    <section class="mail-preview">
      <h3>{t('autoReply.preview')}</h3>
      {#if previewError}
        <p class="warning">{previewError}</p>
      {:else if preview}
        {#if preview.unknownKeywords.length > 0}
          <p class="warning">
            {t('autoReply.unknownKeywords', {
              keywords: preview.unknownKeywords.map((keyword) => `{{${keyword}}}`).join(', '),
            })}
          </p>
        {/if}
        <p class="preview-label">{t('autoReply.previewFrom')}</p>
        <pre>{preview.fromName ? `${preview.fromName} <${preview.fromAddress}>` : preview.fromAddress}</pre>
        <p class="preview-label">{t('autoReply.previewReplyTo')}</p>
        <pre>{preview.replyToAddress ?? t('autoReply.previewNone')}</pre>
        <p class="preview-label">{t('autoReply.previewBcc')}</p>
        <pre>{preview.bccAddress ?? t('autoReply.previewNone')}</pre>
        <p class="preview-label">{t('autoReply.previewTo')}</p>
        <pre>{preview.toAddress}</pre>
        <p class="preview-label">{t('autoReply.previewSubject')}</p>
        <pre>{preview.subject}</pre>
        <p class="preview-label">{t('autoReply.previewBody')}</p>
        <pre>{preview.body}</pre>
      {/if}

      <div class="test-send">
        <button
          type="button"
          disabled={testSending || !mailEnabled || !testRecipientAvailable}
          onclick={sendTest}
        >
          {testSending ? t('autoReply.testSending') : t('autoReply.testSend')}
        </button>
        {#if testRecipientAvailable}
          <p class="hint">{t('autoReply.testRecipient', { recipient: testRecipient })}</p>
        {:else}
          <p class="warning">{t('autoReply.testRecipientUnavailable')}</p>
        {/if}
        {#if testResult}
          <p class:warning={testFailed} class="hint" role="status">{testResult}</p>
        {/if}
      </div>
    </section>
  {/if}
</section>

<style lang="scss">
  .auto-reply {
    margin-block: 1.5rem;
    padding: 1rem;
    border: 1px solid var(--border);
    border-radius: 0.5rem;
  }

  h2 {
    margin-block: 0 0.5rem;
    font-size: 1.1rem;
  }

  label {
    display: block;
    margin-block: 0.75rem;

    &.toggle {
      display: flex;
      gap: 0.5rem;
      align-items: center;
    }

    &.unavailable {
      color: var(--muted);
    }
  }

  input[type='text'],
  input[type='email'],
  textarea,
  select {
    display: block;
    width: 100%;
    margin-block-start: 0.25rem;
  }

  .hint {
    margin-block: 0.25rem;
    color: var(--muted);
    font-size: 0.85rem;
  }

  .warning {
    margin-block: 0.5rem;
    padding: 0.5rem 0.75rem;
    border-radius: 0.25rem;
    background: var(--warning-surface, #fff4e5);
    color: var(--warning-text, #7a4b00);
    font-size: 0.85rem;
  }

  .keywords {
    display: grid;
    gap: 0.35rem;
    margin-block: 0.75rem;
  }

  .keyword {
    display: flex;
    gap: 0.75rem;
    align-items: baseline;
    width: 100%;
    padding: 0.4rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 0.25rem;
    background: var(--surface, #fff);
    color: inherit;
    text-align: left;
  }

  .keyword span {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .mail-preview {
    margin-block-start: 1rem;
    padding-block-start: 1rem;
    border-top: 1px solid var(--border);
  }

  .mail-preview h3 {
    margin: 0 0 0.75rem;
    font-size: 1rem;
  }

  .preview-label {
    margin: 0.75rem 0 0.25rem;
    color: var(--muted);
    font-size: 0.8rem;
  }

  .mail-preview pre {
    min-height: 2rem;
    margin: 0;
    padding: 0.75rem;
    overflow-wrap: anywhere;
    white-space: pre-wrap;
    border: 1px solid var(--border);
    border-radius: 0.25rem;
    background: var(--surface, #fff);
    color: inherit;
    font: inherit;
  }

  .test-send {
    margin-block-start: 1rem;
  }
</style>
