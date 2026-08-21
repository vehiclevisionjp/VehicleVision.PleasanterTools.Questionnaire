<script lang="ts">
  import {
    createSurvey,
    duplicateSurvey,
    listSurveys,
    resume,
    saveAsTemplate,
    saveSurveySettings,
    suspend,
  } from '../lib/api';
  import {
    isPublished,
    suspendedReasonKey,
    surveyStatusKey,
    type SurveySummary,
  } from '../lib/types';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import SurveyQrCode from './SurveyQrCode.svelte';
  import TemplatePanel from './TemplatePanel.svelte';

  interface Props {
    onopen: (surveyId: string) => void;
    /**
     * 複製を出してよい相手か。**Administrator だけ。**
     *
     * **隠すだけでは守りにならない**ので、サーバ側でも同じ判定をしている
     * （`AdminSurveyEndpoints`）。ここで隠すのは、押せない釦を出さないため。
     */
    canDuplicate?: boolean;
    /**
     * テンプレートを出してよい相手か（Issue #58）。**Administrator だけ。**
     *
     * **複製と同じ理由。** テンプレートから作るのは書き込み先のサイトを
     * 新しく決める操作で、誤ると別の業務のサイトへ回答が流れ込む。
     * **サーバ側でも同じ判定をしている**（`AdminTemplateEndpoints`）。
     */
    canUseTemplates?: boolean;
  }

  let { onopen, canDuplicate = false, canUseTemplates = false }: Props = $props();

  let surveys = $state<SurveySummary[]>([]);
  let loading = $state(true);
  let error = $state('');

  /** 1 ページの件数。**サーバ側の上限（200）より小さくしてある。** */
  const pageSize = 50;

  /** 次のページがあるか。**総数はサーバも数えていない。** */
  let hasMore = $state(false);
  let offset = $state(0);

  /**
   * 絞り込み（Issue #79）。
   *
   * **入力欄そのものを条件にしない。** 1 文字打つたびに問い合わせると、
   * アンケートが多いほど無駄な問い合わせが増える。**押したときだけ確定する。**
   */
  let titleInput = $state('');
  let statusInput = $state('');
  let titleFilter = $state('');
  let statusFilter = $state<number | null>(null);

  let creating = $state(false);
  let newTitle = $state('');
  let newSiteId = $state('');
  let newJsonColumn = $state('');

  /** 複製を開いているアンケート。**1 度に 1 つだけ開く。** */
  let duplicating = $state<SurveySummary | null>(null);
  let copySiteId = $state('');
  let copyJsonColumn = $state('');
  /** 二重送信で 2 つ複製されないようにする */
  let duplicateBusy = $state(false);

  /**
   * QR コードを出しているアンケート（Issue #57）。**1 度に 1 つだけ。**
   *
   * **描くのは画面の中だけ。** サーバへ URL を送らない
   */
  let showingQr = $state<SurveySummary | null>(null);

  /** テンプレートの一覧を開いているか（Issue #58）。 */
  let showingTemplates = $state(false);

  /** テンプレートにしようとしているアンケート。**1 度確かめる。** */
  let templating = $state<SurveySummary | null>(null);
  let templateBusy = $state(false);
  /** テンプレートにできたことを伝える。**一覧の見た目は変わらないため。** */
  let templateSaved = $state(false);

  /** 公開設定を開いているアンケート。**1 度に 1 つだけ開く。** */
  let settingsFor = $state<SurveySummary | null>(null);
  /** 回答数の上限。**空欄は「上限なし」。** */
  let settingsLimit = $state('');
  /**
   * proof-of-work を課すか（Issue #66）。
   *
   * **サーバは null の項目を落として返す**ので `?? true` で受ける。
   * **既定は有効**なので、分からないときは有効側に倒す。
   */
  let settingsProofOfWork = $state(true);
  /**
   * 下書きを端末へ残すか（Issue #59）。
   *
   * **既定は無効**なので、分からないときは無効側に倒す。
   * 端末は共有され得るため、こちらは「分からないなら残さない」が安全側。
   */
  let settingsAllowDraft = $state(false);
  let settingsBusy = $state(false);

  // **絞り込みとページを変えたら読み直す。** $effect が依存を拾う
  $effect(() => {
    void reload(offset, titleFilter, statusFilter);
  });

  async function reload(from: number, title: string, status: number | null) {
    loading = true;
    const result = await listSurveys(from, pageSize, title, status);
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    surveys = result.value.items;
    hasMore = result.value.hasMore;
  }

  /**
   * 絞り込みを確定する。
   *
   * **ページ送りは先頭へ戻す。** 3 ページ目のまま絞り込むと、
   * 条件に合う行があっても「1 件も無い」と見える。
   */
  function applyFilter(event: SubmitEvent) {
    event.preventDefault();
    titleFilter = titleInput;
    statusFilter = statusInput === '' ? null : Number(statusInput);
    offset = 0;
  }

  function clearFilter() {
    titleInput = '';
    statusInput = '';
    titleFilter = '';
    statusFilter = null;
    offset = 0;
  }

  async function create(event: SubmitEvent) {
    event.preventDefault();
    error = '';

    const siteId = Number(newSiteId);
    if (!Number.isInteger(siteId) || siteId <= 0) {
      error = t('list.newSiteIdInvalid');
      return;
    }

    const result = await createSurvey(newTitle, siteId, newJsonColumn);
    if (!result.ok) {
      error = result.message;
      return;
    }

    newTitle = '';
    newSiteId = '';
    newJsonColumn = '';
    creating = false;
    onopen(result.value.surveyId);
  }

  function openDuplicate(survey: SurveySummary) {
    error = '';
    duplicating = survey;
    // **元のサイト ID を入れておかない。** 出したままだと、
    // そのまま押されて 2 つのアンケートが同じサイトへ書き込む
    copySiteId = '';
    copyJsonColumn = '';
  }

  async function duplicate(event: SubmitEvent) {
    event.preventDefault();
    const source = duplicating;
    if (source === null || duplicateBusy) {
      return;
    }

    error = '';

    const siteId = Number(copySiteId);
    if (!Number.isInteger(siteId) || siteId <= 0) {
      error = t('list.newSiteIdInvalid');
      return;
    }

    duplicateBusy = true;
    const result = await duplicateSurvey(source.surveyId, siteId, copyJsonColumn);
    duplicateBusy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    duplicating = null;
    // **複製したものをそのまま開く。** 直すために複製している
    onopen(result.value.surveyId);
  }

  function openTemplate(survey: SurveySummary) {
    error = '';
    templateSaved = false;
    templating = survey;
  }

  async function makeTemplate() {
    const source = templating;
    if (source === null || templateBusy) {
      return;
    }

    error = '';
    templateBusy = true;
    const result = await saveAsTemplate(source.surveyId);
    templateBusy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    templating = null;
    // **アンケートの一覧は変わらない**ので、できたことを言葉で伝える
    templateSaved = true;
    showingTemplates = true;
  }

  async function toggle(survey: SurveySummary) {
    const result = survey.status === 1 ? await suspend(survey.surveyId) : await resume(survey.surveyId);
    if (!result.ok) {
      // **上限に達したままの再開はサーバが断る。** その理由をそのまま出す
      error = result.message;
      return;
    }

    await reload(offset, titleFilter, statusFilter);
  }

  function toggleQr(survey: SurveySummary) {
    // **同じ行をもう一度押したら閉じる。** 開きっぱなしで表が押し下げられない
    showingQr = showingQr?.surveyId === survey.surveyId ? null : survey;
  }

  function openSettings(survey: SurveySummary) {
    error = '';
    settingsFor = survey;
    settingsLimit = survey.responseLimit == null ? '' : String(survey.responseLimit);
    settingsProofOfWork = survey.requireProofOfWork ?? true;
    settingsAllowDraft = survey.allowDraft ?? false;
  }

  async function saveSettings(event: SubmitEvent) {
    event.preventDefault();
    const target = settingsFor;
    if (target === null || settingsBusy) {
      return;
    }

    error = '';

    // **空欄は「上限なし」。** 0 を送らない（サーバも断るが、ここで伝える方が早い）
    const trimmed = settingsLimit.trim();
    let limit: number | null = null;
    if (trimmed !== '') {
      limit = Number(trimmed);
      if (!Number.isInteger(limit) || limit <= 0) {
        error = t('settings.limitInvalid');
        return;
      }
    }

    settingsBusy = true;
    const result = await saveSurveySettings(
      target.surveyId, limit, settingsProofOfWork, settingsAllowDraft);
    settingsBusy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    settingsFor = null;
    await reload(offset, titleFilter, statusFilter);
  }

  /** 受付数の表示。**上限があれば「/ 上限」を添える。** */
  function responses(survey: SurveySummary): string {
    return survey.responseLimit == null
      ? t('list.responseCount', { count: survey.responseCount })
      : t('list.responseCountOfLimit', {
          count: survey.responseCount,
          limit: survey.responseLimit,
        });
  }

  /** 回答用 URL。**公開用 ID しか出さない。** */
  function formUrl(publicId: string): string {
    return `${location.origin}/f/${publicId}`;
  }

  function formatDate(value: string): string {
    // **保存されているのは UTC。** 見る人の時間帯で、見る人の言語の書式で出す
    // （`_documents/多言語対応方針.md` 4 章）
    const parsed = new Date(value.endsWith('Z') ? value : `${value}Z`);
    return Number.isNaN(parsed.getTime()) ? value : formatDateTime(parsed);
  }
</script>

<header class="bar">
  <h1>{t('list.title')}</h1>
  {#if canUseTemplates}
    <button
      type="button"
      class="secondary"
      onclick={() => (showingTemplates = !showingTemplates)}
    >
      {t('template.open')}
    </button>
  {/if}
  <button type="button" onclick={() => (creating = !creating)}>
    {creating ? t('list.cancel') : t('list.create')}
  </button>
</header>

<!--
  **絞り込み**（Issue #79）。アンケートは消さずに溜まるので、
  一覧を上から眺めて探せるのは最初のうちだけ
-->
<form class="filter" onsubmit={applyFilter}>
  <label class="filter-title">
    {t('list.filterTitle')}
    <input type="search" bind:value={titleInput} placeholder={t('list.filterTitlePlaceholder')} />
  </label>
  <label class="filter-status">
    {t('list.filterStatus')}
    <select bind:value={statusInput}>
      <option value="">{t('list.filterStatusAll')}</option>
      <option value="0">{t('status.draft')}</option>
      <option value="1">{t('status.published')}</option>
      <option value="2">{t('status.suspended')}</option>
    </select>
  </label>
  <div class="actions">
    <button type="submit" class="secondary">{t('list.filterApply')}</button>
    <button type="button" class="secondary" onclick={clearFilter}>{t('list.filterClear')}</button>
  </div>
</form>

{#if creating}
  <form class="create" onsubmit={create}>
    <label>
      {t('list.newTitle')}
      <input type="text" bind:value={newTitle} required />
    </label>
    <label>
      {t('list.newSiteId')}
      <input type="text" inputmode="numeric" bind:value={newSiteId} required />
    </label>
    <label>
      {t('list.newJsonColumn')}
      <input
        type="text"
        placeholder={t('list.newJsonColumnPlaceholder')}
        bind:value={newJsonColumn}
      />
      <!-- **途中で変えると既存の回答が読めなくなる。** 先に決めておくのが安全 -->
      <span class="hint">{t('list.newJsonColumnHint')}</span>
    </label>
    <button type="submit">{t('list.submit')}</button>
  </form>
{/if}

{#if duplicating}
  <form class="create" onsubmit={duplicate}>
    <h2>{t('duplicate.title', { title: duplicating.title })}</h2>
    <p class="hint">{t('duplicate.description')}</p>
    <label>
      {t('duplicate.siteId')}
      <input type="text" inputmode="numeric" bind:value={copySiteId} required />
      <!-- **1 アンケート = 1 サイト。** 元と同じサイトはサーバが断る -->
      <span class="hint">{t('duplicate.siteIdHint')}</span>
    </label>
    <label>
      {t('duplicate.jsonColumn')}
      <input
        type="text"
        placeholder={t('list.newJsonColumnPlaceholder')}
        bind:value={copyJsonColumn}
      />
    </label>
    <div class="actions">
      <button type="submit" disabled={duplicateBusy}>{t('duplicate.submit')}</button>
      <button type="button" class="secondary" onclick={() => (duplicating = null)}>
        {t('duplicate.cancel')}
      </button>
    </div>
  </form>
{/if}

{#if showingQr}
  <SurveyQrCode
    url={formUrl(showingQr.publicId)}
    title={showingQr.title}
    publicId={showingQr.publicId}
    onclose={() => (showingQr = null)}
  />
{/if}

{#if templating}
  <div class="create">
    <h2>{t('template.saveTitle', { title: templating.title })}</h2>
    <p class="hint">{t('template.saveDescription')}</p>
    <div class="actions">
      <button type="button" disabled={templateBusy} onclick={makeTemplate}>
        {t('template.saveSubmit')}
      </button>
      <button type="button" class="secondary" onclick={() => (templating = null)}>
        {t('duplicate.cancel')}
      </button>
    </div>
  </div>
{/if}

{#if templateSaved}<p class="saved" role="status">{t('template.saved')}</p>{/if}

{#if showingTemplates && canUseTemplates}
  <TemplatePanel oncreated={onopen} />
{/if}

{#if settingsFor}
  <form class="create" onsubmit={saveSettings}>
    <h2>{t('settings.title', { title: settingsFor.title })}</h2>
    <label>
      {t('settings.responseLimit')}
      <input type="text" inputmode="numeric" bind:value={settingsLimit} />
      <span class="hint">{t('settings.responseLimitHint')}</span>
    </label>
    <!--
      **上限を引き上げても勝手には再開しない**（_documents/データモデル設計.md 2.1）。
      押す人がそれを知らないと、止まったままなのを不具合だと受け取る
    -->
    <p class="hint">{t('settings.noAutoResume')}</p>
    <!--
      **proof-of-work の要否**（Issue #66）。**公開し直さずに切り替えられる。**
      **切っても他の bot 対策は外れない**ことを併せて出す。
      出さないと「切る＝無防備」と受け取られ、切ってよい場面でも切れない
    -->
    <label class="check">
      <input type="checkbox" bind:checked={settingsProofOfWork} />
      {t('settings.proofOfWork')}
    </label>
    <p class="hint">{t('settings.proofOfWorkHint')}</p>

    <!--
      **既定は無効。** 端末は共有され得る（店頭のタブレット、共用 PC）。
      **入れると何が起きるかを、入れる前に読ませる**
    -->
    <label class="check">
      <input type="checkbox" bind:checked={settingsAllowDraft} />
      {t('settings.allowDraft')}
    </label>
    <p class="hint">{t('settings.allowDraftHint')}</p>
    {#if settingsAllowDraft}
      <p class="warn">{t('settings.allowDraftWarning')}</p>
    {/if}
    <p class="hint">{t('settings.proofOfWorkKeepsOthers')}</p>
    <div class="actions">
      <button type="submit" disabled={settingsBusy}>{t('settings.submit')}</button>
      <button type="button" class="secondary" onclick={() => (settingsFor = null)}>
        {t('settings.cancel')}
      </button>
    </div>
  </form>
{/if}

{#if error}<p class="error" role="alert">{error}</p>{/if}

{#if loading}
  <p class="status">{t('app.loading')}</p>
{:else if surveys.length === 0}
  <!-- **絞り込んだ結果 0 件なのか、1 件も無いのかを言い分ける** -->
  <p class="status">
    {titleFilter !== '' || statusFilter !== null ? t('list.emptyFiltered') : t('list.empty')}
  </p>
{:else}
  <table>
    <thead>
      <tr>
        <th>{t('list.columnTitle')}</th>
        <th>{t('list.columnStatus')}</th>
        <th>{t('list.columnVersion')}</th>
        <th>{t('list.columnResponses')}</th>
        <th>{t('list.columnUrl')}</th>
        <th>{t('list.columnUpdated')}</th>
        <th></th>
      </tr>
    </thead>
    <tbody>
      {#each surveys as survey (survey.surveyId)}
        <tr>
          <td>
            <button type="button" class="link" onclick={() => onopen(survey.surveyId)}>
              {survey.title}
            </button>
          </td>
          <td>
            <span class="status-{survey.status}">{t(surveyStatusKey(survey.status))}</span>
            <!--
              **なぜ止まっているのかが分かること**（_documents/データモデル設計.md 2.1）。
              理由の付いていない古い停止では、鍵が無いので何も出さない
            -->
            {#if survey.status === 2}
              {@const reasonKey = suspendedReasonKey(survey.suspendedReason)}
              {#if reasonKey}
                <span class="reason">{t(reasonKey)}</span>
              {/if}
            {/if}
          </td>
          <td>{survey.publishedVersion ?? '—'}</td>
          <td class="responses">{responses(survey)}</td>
          <td>
            {#if isPublished(survey)}
              <a href={formUrl(survey.publicId)} target="_blank" rel="noreferrer">
                {survey.publicId}
              </a>
            {:else}
              <span class="muted">{t('list.notPublished')}</span>
            {/if}
          </td>
          <td class="muted">{formatDate(survey.updatedAt)}</td>
          <td class="row-actions">
            {#if isPublished(survey)}
              <button type="button" class="secondary" onclick={() => toggle(survey)}>
                {survey.status === 1 ? t('list.suspend') : t('list.resume')}
              </button>
              <!-- **公開していないものには出さない。** 出しても読めない URL になる -->
              <button type="button" class="secondary" onclick={() => toggleQr(survey)}>
                {t('qr.open')}
              </button>
            {/if}
            <button type="button" class="secondary" onclick={() => openSettings(survey)}>
              {t('settings.open')}
            </button>
            {#if canDuplicate}
              <button type="button" class="secondary" onclick={() => openDuplicate(survey)}>
                {t('duplicate.open')}
              </button>
            {/if}
            {#if canUseTemplates}
              <button type="button" class="secondary" onclick={() => openTemplate(survey)}>
                {t('template.save')}
              </button>
            {/if}
          </td>
        </tr>
      {/each}
    </tbody>
  </table>

  <nav class="pager">
    <button
      type="button"
      class="secondary"
      disabled={offset === 0}
      onclick={() => (offset = Math.max(offset - pageSize, 0))}
    >
      {t('list.previous')}
    </button>

    <span class="range">
      {t('list.page', { from: offset + 1, to: offset + surveys.length })}
    </span>

    <button
      type="button"
      class="secondary"
      disabled={!hasMore}
      onclick={() => (offset = offset + pageSize)}
    >
      {t('list.next')}
    </button>
  </nav>
{/if}

<style lang="scss">
  .bar {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 1.5rem;
  }

  /* **絞り込みは 1 行に収める。** 狭い画面では折り返す */
  .filter {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-end;
    gap: 0.75rem;
    margin-bottom: 1rem;
  }

  .filter-title {
    flex: 1 1 16rem;
  }

  .filter-status {
    flex: 0 0 auto;
  }

  .filter select {
    display: block;
    margin-top: 0.25rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
  }

  .pager {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-top: 1rem;
  }

  .range {
    color: var(--muted);
    font-size: 0.85rem;
  }

  h1 {
    font-size: 1.35rem;
    margin: 0;
    /* **釦は右端へ寄せる。** 見出しと釦の間を空ける */
    margin-right: auto;
  }

  .create {
    display: grid;
    gap: 0.75rem;
    padding: 1.25rem;
    margin-bottom: 1.5rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  .create h2 {
    margin: 0;
    font-size: 1.05rem;
  }

  .actions {
    display: flex;
    gap: 0.5rem;
  }

  /*
    停止と複製が並ぶ。**`td` は `display: flex` にしない**（表の桁が崩れる）ので、
    釦どうしの間だけを空ける
  */
  .row-actions button + button {
    margin-left: 0.5rem;
  }

  label {
    display: block;
    font-size: 0.9rem;
  }

  input {
    display: block;
    width: 100%;
    margin-top: 0.25rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    box-sizing: border-box;
  }

  /*
    **入り切りの札は文字の横に置く。** ここの `input` は文字入力の想定で
    幅いっぱいに広がるので、そのままだと札が桁いっぱいの箱になる
  */
  label.check {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  label.check input {
    display: inline-block;
    width: auto;
    margin-top: 0;
  }

  .hint {
    display: block;
    margin-top: 0.25rem;
    color: var(--muted);
    font-size: 0.8rem;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
    overflow: hidden;
  }

  th,
  td {
    text-align: left;
    padding: 0.6rem 0.75rem;
    border-bottom: 1px solid var(--border);
    font-size: 0.9rem;
  }

  th {
    background: var(--bg);
    font-weight: 600;
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  .link {
    background: none;
    border: none;
    padding: 0;
    color: var(--accent);
    font: inherit;
    cursor: pointer;
    text-decoration: underline;
  }

  .muted {
    color: var(--muted);
  }

  /* 停止の理由は状態の下に小さく添える */
  .reason {
    display: block;
    margin-top: 0.15rem;
    color: var(--muted);
    font-size: 0.8rem;
    font-weight: normal;
  }

  .responses {
    white-space: nowrap;
  }

  .status-0 {
    color: var(--muted);
  }

  .status-1 {
    color: #067647;
    font-weight: 600;
  }

  .status-2 {
    color: var(--error);
    font-weight: 600;
  }

  .status {
    color: var(--muted);
  }

  .error {
    color: var(--error);
  }

  .saved {
    color: #067647;
    font-size: 0.9rem;
  }
</style>
