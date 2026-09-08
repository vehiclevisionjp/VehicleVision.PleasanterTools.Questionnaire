<script lang="ts">
  import AuditLogList from './components/AuditLogList.svelte';
  import EnrollPanel from './components/EnrollPanel.svelte';
  import NotificationList from './components/NotificationList.svelte';
  import OutboxStatusPanel from './components/OutboxStatusPanel.svelte';
  import SignInPanel from './components/SignInPanel.svelte';
  import SurveyEditor from './components/SurveyEditor.svelte';
  import SurveyList from './components/SurveyList.svelte';
  import { getSession, listNotifications, logout, saveLanguage } from './lib/api';
  import type { AdminSession } from './lib/types';
  import { LANGUAGE_NAMES, SUPPORTED_LANGUAGES, type Language } from '../lib/i18n/language';
  import { language, resolveLanguage, t } from './lib/i18n/state.svelte';

  let session = $state<AdminSession>();
  let loading = $state(true);
  let failed = $state(false);

  /** 開いているアンケート。**URL に出す**（再読み込みで戻れるように） */
  let openSurveyId = $state(readSurveyId());

  /** 操作の記録を開いているか。**これも URL に出す。** */
  let openAuditLog = $state(readAuditLog());

  /** 送信状況を開いているか。**これも URL に出す。** */
  let openOutbox = $state(readOutbox());

  /** お知らせを開いているか。**これも URL に出す。**（Issue #80） */
  let openNotifications = $state(readNotifications());

  /**
   * 未読の件数。**ヘッダのバッジに出す。**
   *
   * **管理画面を開いた時点で数える。** ログを見張っていなくても、
   * 何かあったことに気付けるようにするための仕組みなので、
   * お知らせの画面を開くまで分からないのでは意味が無い。
   */
  let unreadCount = $state(0);

  $effect(() => {
    void refresh();
  });

  $effect(() => {
    // **タブの題名も言語に合わせる**
    document.title = t('app.title');
  });

  $effect(() => {
    // 戻る・進むに追従する
    const onPop = () => {
      openSurveyId = readSurveyId();
      openAuditLog = readAuditLog();
      openOutbox = readOutbox();
      openNotifications = readNotifications();
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  });

  function readSurveyId(): string | null {
    const match = /^\/admin\/surveys\/([0-9a-f-]{36})/i.exec(location.pathname);
    return match?.[1] ?? null;
  }

  function readAuditLog(): boolean {
    return /^\/admin\/audit-logs\/?$/.test(location.pathname);
  }

  function readOutbox(): boolean {
    return /^\/admin\/outbox\/?$/.test(location.pathname);
  }

  function readNotifications(): boolean {
    return /^\/admin\/notifications\/?$/.test(location.pathname);
  }

  function open(surveyId: string) {
    openSurveyId = surveyId;
    openAuditLog = false;
    openOutbox = false;
    openNotifications = false;
    history.pushState(null, '', `/admin/surveys/${surveyId}`);
  }

  function openAudit() {
    openSurveyId = null;
    openOutbox = false;
    openNotifications = false;
    openAuditLog = true;
    history.pushState(null, '', '/admin/audit-logs');
  }

  function openDelivery() {
    openSurveyId = null;
    openAuditLog = false;
    openNotifications = false;
    openOutbox = true;
    history.pushState(null, '', '/admin/outbox');
  }

  function openNotificationList() {
    openSurveyId = null;
    openAuditLog = false;
    openOutbox = false;
    openNotifications = true;
    history.pushState(null, '', '/admin/notifications');
  }

  function back() {
    openSurveyId = null;
    openAuditLog = false;
    openOutbox = false;
    openNotifications = false;
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

    // **未読の件数だけ先に読む**（Issue #80）。
    // **失敗しても管理画面は使える。** 気付くための飾りであって、入口ではない
    if (session.role === 'Administrator') {
      const notifications = await listNotifications(0, 1);
      unreadCount = notifications.ok ? notifications.value.unreadCount : 0;
    }
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
    openAuditLog = false;
    openOutbox = false;
    openNotifications = false;
    unreadCount = 0;
    history.replaceState(null, '', '/admin');
    await refresh();
  }

  /**
   * 2 要素の登録が要るか。
   *
   * **パスワードを通した（＝途中状態の）相手にだけ出す。**
   * 未認証の相手に登録画面を見せない。
   */
  /**
   * Administrator か。
   *
   * **入口を隠すだけでは守りにならない**ので、サーバ側でも同じ判定をしている。
   * ここで隠すのは、押せない釦を出さないため。
   */
  const isAdministrator = $derived(session?.role === 'Administrator');

  /**
   * 操作の記録を見せてよい相手か。
   *
   * **Administrator だけ。** 誰が何をしたかは Editor へ見せる情報ではない
   * （`AdminAuditLogEndpoints`）。
   */
  const canSeeAuditLog = $derived(isAdministrator);

  /**
   * 送信状況を見せてよい相手か。
   *
   * **Administrator だけ。** 届いていない回答があることも、
   * 送り直すという操作も、Editor へ開く情報ではない。
   * **サーバ側でも同じ判定をしている**（`AdminOutboxEndpoints`）。
   */
  const canSeeOutbox = $derived(session?.role === 'Administrator');

  /**
   * お知らせを見せてよい相手か（Issue #80）。
   *
   * **Administrator だけ。** どのアンケートが詰まっているかは、
   * Editor へ開く情報ではない。
   * **サーバ側でも同じ判定をしている**（`AdminNotificationEndpoints`）。
   */
  const canSeeNotifications = $derived(session?.role === 'Administrator');

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

      {#if canSeeNotifications}
        <button type="button" class="link" onclick={openNotificationList}>
          {t('notifications.open')}
          {#if unreadCount > 0}
            <!-- **数字も出す。** 印だけだと「1 件」と「300 件」の区別が付かない -->
            <span class="badge">{unreadCount}</span>
          {/if}
        </button>
      {/if}

      {#if canSeeOutbox}
        <button type="button" class="link" onclick={openDelivery}>{t('outbox.open')}</button>
      {/if}

      {#if canSeeAuditLog}
        <button type="button" class="link" onclick={openAudit}>{t('audit.open')}</button>
      {/if}

      <button type="button" class="link" onclick={signOut}>{t('app.signOut')}</button>
    </header>

    <!--
      **表を出す画面だけ広く使う。** 列が多くて識別子も入るので、
      他の画面と同じ幅だと横に流さないと読めない
    -->
    <main
      class:wide={(openAuditLog && canSeeAuditLog) ||
        (openOutbox && canSeeOutbox) ||
        (openNotifications && canSeeNotifications)}
    >
      {#if openAuditLog && canSeeAuditLog}
        <AuditLogList onback={back} />
      {:else if openNotifications && canSeeNotifications}
        <NotificationList onback={back} onunread={(count) => (unreadCount = count)} />
      {:else if openOutbox && canSeeOutbox}
        <OutboxStatusPanel onback={back} />
      {:else if openSurveyId}
        <SurveyEditor surveyId={openSurveyId} onback={back} />
      {:else}
        <!--
          **複製は Administrator だけ**（Issue #46）。
          書き込み先のサイトを新しく決める操作であり、
          誤ると別の業務のサイトへ回答が流れ込む。サーバ側でも同じ判定をしている。

          **テンプレートも同じ**（Issue #58）。テンプレートから作るときに
          サイトを決めるので、複製と同じ危なさがある
        -->
        <SurveyList
          onopen={open}
          canDuplicate={isAdministrator}
          canUseTemplates={isAdministrator}
        />
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
    /* **書体は同梱している**（Issue #152）。等幅は日本語も等幅になる */
    --font-mono: 'M PLUS 1 Code Variable', ui-monospace, Consolas, monospace;
  }

  :global(body) {
    margin: 0;
    background: var(--bg);
    /* **同梱した書体を使う**（Issue #152）。端末の書体に依存させない */
    font-family: 'Noto Sans JP Variable', system-ui, sans-serif;
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

  /*
    **アイコンの下地**（Issue #152）。同梱した Material Icons を使うための最小限。
    使うときは <span class="material-icons" aria-hidden="true">save</span> のように書く。
    **意味は文字で書くこと。** アイコンだけの釦は読み上げで何も伝わらない
  */
  :global(.material-icons) {
    font-family: 'Material Icons';
    font-weight: normal;
    font-style: normal;
    font-size: 1.25em;
    line-height: 1;
    letter-spacing: normal;
    white-space: nowrap;
    vertical-align: -0.15em;
    font-feature-settings: 'liga';
    -webkit-font-smoothing: antialiased;
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

  /* **未読の件数。** 色だけに頼らず、数字そのものを出す（Issue #80） */
  .badge {
    display: inline-block;
    min-width: 1.25rem;
    padding: 0 0.35rem;
    margin-left: 0.25rem;
    border-radius: 999px;
    background: var(--error);
    color: #fff;
    font-size: 0.75rem;
    text-align: center;
  }

  main {
    max-width: 56rem;
    margin: 0 auto;
    padding: 2rem 1.5rem 4rem;
  }

  main.wide {
    max-width: 80rem;
  }

  .status {
    text-align: center;
    color: var(--muted);
    margin-top: 4rem;
  }
</style>
