<script lang="ts">
  import EnrollPanel from './components/EnrollPanel.svelte';
  import SignInPanel from './components/SignInPanel.svelte';
  import SurveyEditor from './components/SurveyEditor.svelte';
  import SurveyList from './components/SurveyList.svelte';
  import { getSession, logout, saveLanguage } from './lib/api';
  import type { AdminSession } from './lib/types';
  import { LANGUAGE_NAMES, SUPPORTED_LANGUAGES, type Language } from '../lib/i18n/language';
  import { language, resolveLanguage, t } from './lib/i18n/state.svelte';

  let session = $state<AdminSession>();
  let loading = $state(true);
  let failed = $state(false);

  /** 開いているアンケート。**URL に出す**（再読み込みで戻れるように） */
  let openSurveyId = $state(readSurveyId());

  $effect(() => {
    void refresh();
  });

  $effect(() => {
    // **タブの題名も言語に合わせる**
    document.title = t('app.title');
  });

  $effect(() => {
    // 戻る・進むに追従する
    const onPop = () => (openSurveyId = readSurveyId());
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  });

  function readSurveyId(): string | null {
    const match = /^\/admin\/surveys\/([0-9a-f-]{36})/i.exec(location.pathname);
    return match?.[1] ?? null;
  }

  function open(surveyId: string) {
    openSurveyId = surveyId;
    history.pushState(null, '', `/admin/surveys/${surveyId}`);
  }

  function back() {
    openSurveyId = null;
    history.pushState(null, '', '/admin');
  }

  async function refresh() {
    const result = await getSession();
    loading = false;

    if (!result.ok) {
      failed = true;
      return;
    }

    failed = false;
    session = result.value;

    // **利用者ごとの設定 → ブラウザの言語設定 → `ja`**
    // （`_documents/多言語対応方針.md` 2 章）
    resolveLanguage(session.language ?? null);
  }

  /**
    * 表示言語を切り替える。
    *
    * **画面を先に切り替えてから保存する。** 保存に失敗しても
    * その場の操作は続けられる方がよい（次に開いたときに戻るだけ）。
    */
  async function changeLanguage(next: Language) {
    resolveLanguage(next);
    await saveLanguage(next);
  }

  async function signOut() {
    await logout();
    openSurveyId = null;
    history.replaceState(null, '', '/admin');
    await refresh();
  }

  /**
   * 2 要素の登録が要るか。
   *
   * **合言葉を通した（＝途中状態の）相手にだけ出す。**
   * 未認証の相手に登録画面を見せない。
   */
  const needsEnrollment = $derived(
    session !== undefined &&
      !session.authenticated &&
      session.pending === true &&
      session.needsEnrollment === true,
  );
</script>

<div class="shell">
  {#if loading}
    <p class="status">{t('app.loading')}</p>
  {:else if failed}
    <p class="status">{t('app.loadFailed')}</p>
  {:else if session?.authenticated}
    <header class="top">
      <span class="brand">{t('app.title')}</span>
      <span class="who">{session.loginId}</span>

      <!-- **利用者ごとの設定として残す。** 端末を変えても付いてくる -->
      <label class="language">
        <span class="visually-hidden">{t('app.language')}</span>
        <select
          value={language()}
          onchange={(event) => changeLanguage(event.currentTarget.value as Language)}
        >
          {#each SUPPORTED_LANGUAGES as option (option)}
            <option value={option}>{LANGUAGE_NAMES[option]}</option>
          {/each}
        </select>
      </label>

      <button type="button" class="link" onclick={signOut}>{t('app.signOut')}</button>
    </header>

    <main>
      {#if openSurveyId}
        <SurveyEditor surveyId={openSurveyId} onback={back} />
      {:else}
        <SurveyList onopen={open} />
      {/if}
    </main>
  {:else if needsEnrollment}
    <EnrollPanel onadvance={refresh} />
  {:else if session}
    <SignInPanel {session} onadvance={refresh} />
  {/if}
</div>

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

  :global(button) {
    font: inherit;
    padding: 0.45rem 1rem;
    border-radius: 6px;
    border: 1px solid var(--accent);
    background: var(--accent);
    color: #fff;
    cursor: pointer;
  }

  :global(button:disabled) {
    opacity: 0.55;
    cursor: default;
  }

  :global(button.secondary) {
    background: #fff;
    color: var(--accent);
  }

  .top {
    display: flex;
    align-items: center;
    gap: 1rem;
    padding: 0.75rem 1.5rem;
    background: #fff;
    border-bottom: 1px solid var(--border);
  }

  .brand {
    font-weight: 600;
  }

  .who {
    margin-left: auto;
    color: var(--muted);
    font-size: 0.85rem;
  }

  .language select {
    font: inherit;
    font-size: 0.85rem;
    padding: 0.2rem 0.35rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: #fff;
    color: #101828;
  }

  /* **読み上げには残す。** 目で見れば分かるが、耳では分からない */
  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    padding: 0;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }

  .link {
    background: none;
    border: none;
    padding: 0;
    color: var(--accent);
    font-size: 0.85rem;
    cursor: pointer;
  }

  main {
    max-width: 56rem;
    margin: 0 auto;
    padding: 2rem 1.5rem 4rem;
  }

  .status {
    text-align: center;
    color: var(--muted);
    margin-top: 4rem;
  }
</style>
