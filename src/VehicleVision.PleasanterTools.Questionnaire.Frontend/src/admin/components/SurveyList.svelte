<script lang="ts">
  import { createSurvey, listSurveys, resume, suspend } from '../lib/api';
  import { surveyStatusLabels, type SurveySummary } from '../lib/types';

  interface Props {
    onopen: (surveyId: string) => void;
  }

  let { onopen }: Props = $props();

  let surveys = $state<SurveySummary[]>([]);
  let loading = $state(true);
  let error = $state('');

  let creating = $state(false);
  let newTitle = $state('');
  let newSiteId = $state('');
  let newJsonColumn = $state('');

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
      error = 'Pleasanter のサイト ID を数字で入力してください。';
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

  async function toggle(survey: SurveySummary) {
    const result = survey.status === 1 ? await suspend(survey.surveyId) : await resume(survey.surveyId);
    if (!result.ok) {
      error = result.message;
      return;
    }

    await reload();
  }

  /** 回答用 URL。**公開用 ID しか出さない。** */
  function formUrl(publicId: string): string {
    return `${location.origin}/f/${publicId}`;
  }

  function formatDate(value: string): string {
    // **保存されているのは UTC。** 見る人の時間帯で出す
    const parsed = new Date(value.endsWith('Z') ? value : `${value}Z`);
    return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString('ja-JP');
  }
</script>

<header class="bar">
  <h1>アンケート</h1>
  <button type="button" onclick={() => (creating = !creating)}>
    {creating ? 'やめる' : '新しく作る'}
  </button>
</header>

{#if creating}
  <form class="create" onsubmit={create}>
    <label>
      題名
      <input type="text" bind:value={newTitle} required />
    </label>
    <label>
      Pleasanter のサイト ID
      <input type="text" inputmode="numeric" bind:value={newSiteId} required />
    </label>
    <label>
      回答 JSON を入れる列（任意）
      <input type="text" placeholder="DescriptionA など" bind:value={newJsonColumn} />
      <!-- **途中で変えると既存の回答が読めなくなる。** 先に決めておくのが安全 -->
      <span class="hint">正本を残す列です。後から変えると既存の回答を設問へ戻せなくなります。</span>
    </label>
    <button type="submit">作る</button>
  </form>
{/if}

{#if error}<p class="error" role="alert">{error}</p>{/if}

{#if loading}
  <p class="status">読み込んでいます…</p>
{:else if surveys.length === 0}
  <p class="status">まだアンケートがありません。</p>
{:else}
  <table>
    <thead>
      <tr>
        <th>題名</th>
        <th>状態</th>
        <th>公開中の版</th>
        <th>回答用 URL</th>
        <th>更新</th>
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
          <td><span class="status-{survey.status}">{surveyStatusLabels[survey.status] ?? '不明'}</span></td>
          <td>{survey.publishedVersion ?? '—'}</td>
          <td>
            {#if survey.publishedVersion !== null}
              <a href={formUrl(survey.publicId)} target="_blank" rel="noreferrer">
                {survey.publicId}
              </a>
            {:else}
              <span class="muted">未公開</span>
            {/if}
          </td>
          <td class="muted">{formatDate(survey.updatedAt)}</td>
          <td>
            {#if survey.publishedVersion !== null}
              <button type="button" class="secondary" onclick={() => toggle(survey)}>
                {survey.status === 1 ? '停止' : '再開'}
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
    justify-content: space-between;
    margin-bottom: 1.5rem;
  }

  h1 {
    font-size: 1.35rem;
    margin: 0;
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
</style>
