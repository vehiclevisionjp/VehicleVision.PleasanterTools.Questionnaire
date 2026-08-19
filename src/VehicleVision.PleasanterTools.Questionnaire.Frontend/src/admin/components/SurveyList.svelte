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
  let settingsBusy = $state(false);

  $effect(() => {
    void reload();
  });

  async function reload() {
    loading = true;
    const result = await listSurveys();
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    surveys = result.value;
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

    await reload();
  }

  function toggleQr(survey: SurveySummary) {
    // **同じ行をもう一度押したら閉じる。** 開きっぱなしで表が押し下げられない
    showingQr = showingQr?.surveyId === survey.surveyId ? null : survey;
  }

  function openSettings(survey: SurveySummary) {
    error = '';
    settingsFor = survey;
    settingsLimit = survey.responseLimit == null ? '' : String(survey.responseLimit);
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
    const result = await saveSurveySettings(target.surveyId, limit);
    settingsBusy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    settingsFor = null;
    await reload();
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
  <p class="status">{t('list.empty')}</p>
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
{/if}

<style lang="scss">
  .bar {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 1.5rem;
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
