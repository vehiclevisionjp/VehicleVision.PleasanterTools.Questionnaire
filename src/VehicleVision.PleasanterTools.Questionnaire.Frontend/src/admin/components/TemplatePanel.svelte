<script lang="ts">
  import { createSurveyFromTemplate, deleteTemplate, listTemplates } from '../lib/api';
  import type { SurveyTemplateSummary } from '../lib/types';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';

  /**
   * アンケートのテンプレート（Issue #58）。
   *
   * **テンプレートは Pleasanter のサイトを持たない。**
   * そこからアンケートを作るときに、書き込み先のサイトを指定させる。
   *
   * **Administrator だけに出す。** サイトを新しく決める操作なので、
   * 誤ると別の業務のサイトへ回答が流れ込む（複製と同じ理由）。
   * **隠すだけでは守りにならない**ので、サーバ側でも同じ判定をしている
   * （`AdminTemplateEndpoints`）。
   */
  interface Props {
    /** テンプレートからアンケートを作ったので、そのまま開いてほしい。 */
    oncreated: (surveyId: string) => void;
  }

  let { oncreated }: Props = $props();

  let templates = $state<SurveyTemplateSummary[]>([]);
  let loading = $state(true);
  let error = $state('');

  /** アンケートを作ろうとしているテンプレート。**1 度に 1 つだけ開く。** */
  let using = $state<SurveyTemplateSummary | null>(null);
  let siteId = $state('');
  let jsonColumn = $state('');
  /** 二重送信で 2 つできないようにする */
  let busy = $state(false);

  /** 消そうとしているテンプレート。**押し間違いで消えないよう 1 度確かめる。** */
  let deleting = $state<SurveyTemplateSummary | null>(null);

  $effect(() => {
    void reload();
  });

  async function reload() {
    loading = true;
    const result = await listTemplates();
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    templates = result.value;
  }

  function openUse(template: SurveyTemplateSummary) {
    error = '';
    deleting = null;
    using = template;
    // **サイト ID は毎回入れさせる。** テンプレートは持っていない
    siteId = '';
    jsonColumn = '';
  }

  async function create(event: SubmitEvent) {
    event.preventDefault();
    const template = using;
    if (template === null || busy) {
      return;
    }

    error = '';

    const site = Number(siteId);
    if (!Number.isInteger(site) || site <= 0) {
      error = t('list.newSiteIdInvalid');
      return;
    }

    busy = true;
    const result = await createSurveyFromTemplate(template.templateId, site, jsonColumn);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    using = null;
    // **作ったものをそのまま開く。** 直すために作っている
    oncreated(result.value.surveyId);
  }

  async function remove(template: SurveyTemplateSummary) {
    error = '';
    busy = true;
    const result = await deleteTemplate(template.templateId);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    deleting = null;
    await reload();
  }

  function formatDate(value: string): string {
    // **保存されているのは UTC。** 見る人の時間帯で、見る人の言語の書式で出す
    const parsed = new Date(value.endsWith('Z') ? value : `${value}Z`);
    return Number.isNaN(parsed.getTime()) ? value : formatDateTime(parsed);
  }
</script>

<section class="panel">
  <h2>{t('template.title')}</h2>
  <p class="hint">{t('template.description')}</p>

  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if using}
    <form class="use" onsubmit={create}>
      <h3>{t('template.useTitle', { title: using.title })}</h3>
      <label>
        {t('template.siteId')}
        <input type="text" inputmode="numeric" bind:value={siteId} required />
        <!-- **テンプレートはサイトを持たない。** ここで決める -->
        <span class="hint">{t('template.siteIdHint')}</span>
      </label>
      <label>
        {t('duplicate.jsonColumn')}
        <input
          type="text"
          placeholder={t('list.newJsonColumnPlaceholder')}
          bind:value={jsonColumn}
        />
      </label>
      <div class="actions">
        <button type="submit" disabled={busy}>{t('template.useSubmit')}</button>
        <button type="button" class="secondary" onclick={() => (using = null)}>
          {t('duplicate.cancel')}
        </button>
      </div>
    </form>
  {/if}

  {#if loading}
    <p class="status">{t('app.loading')}</p>
  {:else if templates.length === 0}
    <p class="status">{t('template.empty')}</p>
  {:else}
    <table>
      <thead>
        <tr>
          <th>{t('list.columnTitle')}</th>
          <th>{t('list.columnUpdated')}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {#each templates as template (template.templateId)}
          <tr>
            <td>{template.title}</td>
            <td class="muted">{formatDate(template.updatedAt)}</td>
            <td class="row-actions">
              <div class="row-actions-inner">
              <button type="button" onclick={() => openUse(template)}>
                {t('template.use')}
              </button>
              {#if deleting?.templateId === template.templateId}
                <!-- **1 度確かめる。** 消したテンプレートは戻せない -->
                <button type="button" class="secondary" disabled={busy} onclick={() => remove(template)}>
                  {t('template.deleteConfirm')}
                </button>
                <button type="button" class="secondary" onclick={() => (deleting = null)}>
                  {t('duplicate.cancel')}
                </button>
              {:else}
                <button type="button" class="secondary" onclick={() => (deleting = template)}>
                  {t('template.delete')}
                </button>
              {/if}
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/if}
</section>

<style lang="scss">
  .panel {
    padding: 1.25rem;
    margin-bottom: 1.5rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 0;
    font-size: 0.95rem;
  }

  .hint {
    display: block;
    margin: 0 0 1rem;
    color: var(--muted);
    font-size: 0.8rem;
  }

  .use {
    display: grid;
    gap: 0.75rem;
    padding: 1rem;
    margin-bottom: 1rem;
    background: var(--bg);
    border-radius: 6px;
  }

  .actions {
    display: flex;
    gap: 0.5rem;
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

  table {
    width: 100%;
    border-collapse: collapse;
  }

  th,
  td {
    text-align: left;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--border);
    font-size: 0.9rem;
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  /*
    停止と複製が並ぶ。**td は display: flex にしない**（表の桁が崩れる）ので、
    中に入れ物を 1 枚はさんでそこを flex にする。
    **横だけに余白を付けると、折り返した先の行が詰まる**（Issue #150）
  */
  .row-actions-inner {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .muted {
    color: var(--muted);
  }

  .status {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }
</style>
