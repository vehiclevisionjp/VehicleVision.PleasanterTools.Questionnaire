<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';
  import { text, withText, type AutoReplySettings, type Question } from '../lib/types';
  import type { Language } from '../../lib/i18n/language';

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
    /** 今の設定。**無ければ送らない。** */
    autoReply: AutoReplySettings | null | undefined;
    /** 全ページの設問。**宛先に選べるものを絞るのに使う。** */
    questions: Question[];
    /** 入力欄が書き込む言語。 */
    editing: Language;
    /** サーバ側でメールの送信が有効か。**無効でも設定は保存できる。** */
    mailEnabled: boolean;
    onchange: (next: AutoReplySettings | null) => void;
  }

  let { autoReply, questions, editing, mailEnabled, onchange }: Props = $props();

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
</script>

<section class="auto-reply">
  <h2>{t('autoReply.title')}</h2>
  <p class="hint">{t('autoReply.lead')}</p>

  <label class="toggle" class:unavailable={!canEnable}>
    <input
      type="checkbox"
      checked={enabled}
      disabled={!canEnable}
      onchange={(event) => update({ enabled: event.currentTarget.checked })}
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
      {t('autoReply.body')}
      <textarea
        rows="6"
        value={text(autoReply?.body, editing)}
        oninput={(event) =>
          update({ body: withText(autoReply?.body, event.currentTarget.value, editing) })}
      ></textarea>
    </label>
    <p class="hint">{t('autoReply.bodyHint')}</p>

    <label class="toggle">
      <input
        type="checkbox"
        checked={autoReply?.includeAnswers ?? false}
        onchange={(event) => update({ includeAnswers: event.currentTarget.checked })}
      />
      {t('autoReply.includeAnswers')}
    </label>
    <!-- ⚠️ **回答の中身がメールとして外へ出る。** 押す前に読めるところへ置く -->
    <p class="hint">{t('autoReply.includeAnswersHint')}</p>
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
</style>
