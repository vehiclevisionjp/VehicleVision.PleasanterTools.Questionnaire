<script lang="ts">
  import AdminUserList from './components/AdminUserList.svelte';
  import AuditLogList from './components/AuditLogList.svelte';
  import EnrollPanel from './components/EnrollPanel.svelte';
  import MyAccountPanel from './components/MyAccountPanel.svelte';
  import NotificationList from './components/NotificationList.svelte';
  import OutboxStatusPanel from './components/OutboxStatusPanel.svelte';
  import SignInPanel from './components/SignInPanel.svelte';
  import SurveyEditor from './components/SurveyEditor.svelte';
  import SurveyList from './components/SurveyList.svelte';
  import {
    getApplicationVersion,
    getSession,
    listNotifications,
    logout,
    saveLanguage,
    type ApplicationVersion,
  } from './lib/api';
  import type { AdminPermission, AdminSession } from './lib/types';
  import { LANGUAGE_NAMES, SUPPORTED_LANGUAGES, type Language } from '../lib/i18n/language';
  import { language, resolveLanguage, t } from './lib/i18n/state.svelte';
  import {
    buildBreadcrumbs,
    truncateBreadcrumbTitle,
    type AdminPage,
  } from './lib/breadcrumbs';

  let session = $state<AdminSession>();
  let applicationVersion = $state<ApplicationVersion>();
  let loading = $state(true);
  let failed = $state(false);
  let surveyBreadcrumbTitle = $state<string | null>(null);
  let navigationGuard = $state<(() => boolean) | null>(null);

  /** 開いているアンケート。**URL に出す**（再読み込みで戻れるように） */
  let openSurveyId = $state(readSurveyId());

  /** 操作の記録を開いているか。**これも URL に出す。** */
  let openAuditLog = $state(readAuditLog());

  /** 送信状況を開いているか。**これも URL に出す。** */
  let openOutbox = $state(readOutbox());

  /** お知らせを開いているか。**これも URL に出す。**（Issue #80） */
  let openNotifications = $state(readNotifications());

  /** 管理者の管理を開いているか。**これも URL に出す。**（Issue #156） */
  let openUsers = $state(readUsers());

  /** 自分のアカウントを開いているか。**これも URL に出す。**（Issue #156） */
  let openAccount = $state(readAccount());

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
      openUsers = readUsers();
      openAccount = readAccount();
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

  function readUsers(): boolean {
    return /^\/admin\/users\/?$/.test(location.pathname);
  }

  function readAccount(): boolean {
    return /^\/admin\/me\/?$/.test(location.pathname);
  }

  /** 画面を 1 つだけ開く。**出し分けの取りこぼしを防ぐ。** */
  function only(path: string, flags: Partial<Record<string, boolean>> = {}) {
    openSurveyId = null;
    openAuditLog = false;
    openOutbox = false;
    openNotifications = false;
    openUsers = flags.users ?? false;
    openAccount = flags.account ?? false;
    history.pushState(null, '', path);
  }

  function navigate(path: string, flags: Partial<Record<string, boolean>> = {}) {
    if (navigationGuard && !navigationGuard()) {
      return false;
    }

    only(path, flags);
    return true;
  }

  function openUserList() {
    if (navigate('/admin/users', { users: true })) {
      openUsers = true;
    }
  }

  function openMyAccount() {
    if (navigate('/admin/me', { account: true })) {
      openAccount = true;
    }
  }

  function open(surveyId: string) {
    if (navigate(`/admin/surveys/${surveyId}`)) {
      openSurveyId = surveyId;
    }
  }

  function openAudit() {
    if (navigate('/admin/audit-logs')) {
      openAuditLog = true;
    }
  }

  function openDelivery() {
    if (navigate('/admin/outbox')) {
      openOutbox = true;
    }
  }

  function openNotificationList() {
    if (navigate('/admin/notifications')) {
      openNotifications = true;
    }
  }

  function back() {
    navigate('/admin');
  }

  const breadcrumbPage = $derived.by<AdminPage>(() => {
    if (openSurveyId) return 'survey-editor';
    if (openAuditLog && canSeeAuditLog) return 'audit-logs';
    if (openOutbox && canSeeOutbox) return 'outbox';
    if (openNotifications && canSeeNotifications) return 'notifications';
    if (openUsers && canSeeUsers) return 'users';
    if (openAccount) return 'account';
    return 'surveys';
  });

  function breadcrumbLabel(page: AdminPage): string {
    if (page === 'surveys') return t('list.title');
    if (page === 'survey-editor') {
      return t('breadcrumb.surveyEditor', {
        title: truncateBreadcrumbTitle(surveyBreadcrumbTitle ?? ''),
      });
    }
    if (page === 'audit-logs') return t('breadcrumb.auditLogs');
    if (page === 'outbox') return t('breadcrumb.outbox');
    if (page === 'notifications') return t('breadcrumb.notifications');
    if (page === 'users') return t('breadcrumb.users');
    return t('breadcrumb.account');
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
    applicationVersion = undefined;

    // **利用者ごとの設定 → ブラウザの言語設定 → `ja`**
    // （`_documents/多言語対応方針.md` 2 章）
    resolveLanguage(session.language ?? null);

    // **認証済みになってから読む。** 認証前の画面へ版を出さず、失敗してもログインを妨げない。
    if (session.authenticated) {
      const version = await getApplicationVersion();
      applicationVersion = version.ok ? version.value : undefined;
    }

    // **未読の件数だけ先に読む**（Issue #80）。
    // **失敗しても管理画面は使える。** 気付くための飾りであって、入口ではない
    if (session.permissions?.includes('notifications.read') ?? false) {
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

  /**
   * ログアウトする。
   *
   * ⚠️ **SAML で入った人は、IdP 側も落とす**（Issue #191）。
   * こちらの cookie だけ消すと、**IdP のセッションが残っているので釦を押し直すだけで
   * 入り直せてしまう。** 共用の端末だとログアウトしたつもりで座席を明け渡すことになる。
   *
   * **行き先はサーバが決める**（`samlSingleLogout`）。画面は行くだけ。
   */
  async function signOut() {
    if (session?.samlSingleLogout) {
      window.location.href = '/api/admin/saml/logout';
      return;
    }

    await logout();
    openSurveyId = null;
    openAuditLog = false;
    openOutbox = false;
    openNotifications = false;
    openUsers = false;
    openAccount = false;
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
  function can(permission: AdminPermission): boolean {
    return session?.permissions?.includes(permission) ?? false;
  }

  /**
   * 操作の記録を見せてよい相手か。
   *
   * **`audit.read` を持つ相手だけ**（`AdminAuditLogEndpoints` と同じ権限）。
   */
  const canSeeAuditLog = $derived(can('audit.read'));

  /**
   * 送信状況を見せてよい相手か。
   *
   * **`outbox.read` を持つ相手だけ。** 届いていない回答があることも、
   * 送り直すという操作も、誰にでも開く情報ではない。
   * **サーバ側でも同じ権限で判定している**（`AdminOutboxEndpoints`）。
   */
  const canSeeOutbox = $derived(can('outbox.read'));

  /**
   * お知らせを見せてよい相手か（Issue #80）。
   *
   * **`notifications.read` を持つ相手だけ。**
   * **サーバ側でも同じ権限で判定している**（`AdminNotificationEndpoints`）。
   */
  const canSeeNotifications = $derived(can('notifications.read'));

  /**
   * 管理者の管理を見せてよい相手か（Issue #156）。
   *
   * **`users.read` を持つ相手だけ。** 追加・役割変更・停止は `users.write`、
   * 2 要素の解除は `users.resetTwoFactor` で更に絞る。
   * **サーバ側でも同じ権限で判定している**（`AdminUserEndpoints`）。
   */
  const canSeeUsers = $derived(can('users.read'));

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

      {#if canSeeUsers}
        <button type="button" class="link" onclick={openUserList}>{t('users.open')}</button>
      {/if}

      <!-- **自分の設定は誰でも開ける。** 役割を問わない -->
      <button type="button" class="link" onclick={openMyAccount}>{t('account.open')}</button>

      <button type="button" class="link" onclick={signOut}>{t('app.signOut')}</button>
    </header>

    <nav class="breadcrumbs" aria-label={t('breadcrumb.label')}>
      <ol>
        {#each buildBreadcrumbs(breadcrumbPage) as breadcrumb, index (breadcrumb.page)}
          <li>
            {#if breadcrumb.path}
              <button type="button" class="breadcrumb-link" onclick={() => navigate(breadcrumb.path!)}>
                {breadcrumbLabel(breadcrumb.page)}
              </button>
            {:else}
              <span aria-current="page">{breadcrumbLabel(breadcrumb.page)}</span>
            {/if}
            {#if index < buildBreadcrumbs(breadcrumbPage).length - 1}
              <span class="breadcrumb-separator" aria-hidden="true">/</span>
            {/if}
          </li>
        {/each}
      </ol>
    </nav>

    {#if applicationVersion?.allowInsecure}
      <aside class="insecure-warning" role="alert">
        <span class="material-icons" aria-hidden="true">warning</span>
        <span>{t('app.insecureMode')}</span>
      </aside>
    {/if}

    <!--
      **表を出す画面だけ広く使う。** 列が多くて識別子も入るので、
      他の画面と同じ幅だと横に流さないと読めない
    -->
    <main
      class:wide={(openAuditLog && canSeeAuditLog) ||
        (openOutbox && canSeeOutbox) ||
        (openNotifications && canSeeNotifications) ||
        (openUsers && canSeeUsers)}
    >
      {#if openUsers && canSeeUsers}
        <AdminUserList
          ownAdminUserId={session.adminUserId ?? ''}
          canWrite={can('users.write')}
          canReset={can('users.resetTwoFactor')}
          onback={back}
        />
      {:else if openAccount}
        <MyAccountPanel {session} onchanged={refresh} onback={back} />
      {:else if openAuditLog && canSeeAuditLog}
        <AuditLogList onback={back} />
      {:else if openNotifications && canSeeNotifications}
        <NotificationList onback={back} onunread={(count) => (unreadCount = count)} />
      {:else if openOutbox && canSeeOutbox}
        <OutboxStatusPanel onback={back} />
      {:else if openSurveyId}
        <SurveyEditor
          surveyId={openSurveyId}
          mailEnabled={session?.mailEnabled ?? false}
          onback={back}
          onbreadcrumbchange={(title) => (surveyBreadcrumbTitle = title)}
          onnavigationguardchange={(guard) => (navigationGuard = guard)}
        />
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
          canDuplicate={can('surveys.publish')}
          canUseTemplates={can('templates.read')}
          canDelete={can('surveys.delete')}
        />
      {/if}
    </main>
    {#if applicationVersion}
      <footer class="version">
        {t('app.version', {
          version: applicationVersion.version,
          commit: applicationVersion.commit ? ` (${applicationVersion.commit})` : '',
        })}
      </footer>
    {/if}
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
    **アイコンの下地**（Issue #152）。同梱した Material Icons を使う。

      <span class="material-icons" aria-hidden="true">save</span>

    中身は**アイコンの名前**（リガチャで字形へ置き換わる）。
    **意味は文字でも書くこと。** アイコンだけの釦は読み上げで何も伝わらない。
    **`aria-hidden="true"` を必ず付ける。** 付けないと 'save' という英単語が読み上げられる。

    **表の中で使うときは `fixed` を足す**（下記）。桁が揃う
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

  /*
    **表の中で桁を揃えるための指定。**
    アイコンの字形は幅が同じでも、**名前が短い字形は詰まって見える**ことがある。
    箱の幅を決めて中央へ置けば、行ごとにずれない
  */
  :global(.material-icons.fixed) {
    display: inline-block;
    width: 1.5em;
    text-align: center;
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

  .breadcrumbs {
    padding: 0.5rem 1.5rem;
    background: #fff;
    border-bottom: 1px solid var(--border);
    color: var(--muted);
    font-size: 0.85rem;
  }

  .breadcrumbs ol {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    max-width: 80rem;
    padding: 0;
    margin: 0 auto;
    list-style: none;
  }

  .breadcrumbs li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
  }

  .insecure-warning {
    display: flex;
    align-items: flex-start;
    gap: 0.5rem;
    max-width: 77rem;
    margin: 1rem auto 0;
    padding: 0.75rem 1rem;
    border: 1px solid #f79009;
    border-radius: 6px;
    background: #fff4e5;
    color: #7a4b00;
  }

  .breadcrumb-link {
    padding: 0;
    border: 0;
    border-radius: 0;
    background: none;
    color: var(--accent);
    text-decoration: underline;
  }

  .breadcrumb-separator {
    color: var(--border);
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

  /*
    **桁の多い画面はここを使う**（アンケート一覧・2 カラムの編集）。
    ⚠️ **80rem では足りない。** 一覧は 7 桁あり、行ごとに釦が最大 7 つ並ぶので、
    **題名か回答用 URL のどちらかが 1 文字ずつ折り返す**（実測。2026-09-16）
  */
  main.wide {
    max-width: 100rem;
  }

  .status {
    text-align: center;
    color: var(--muted);
    margin-top: 4rem;
  }

  .version {
    padding: 0 1.5rem 1.5rem;
    color: var(--muted);
    font-family: var(--font-mono);
    font-size: 0.75rem;
    text-align: right;
  }
</style>
