<script lang="ts">
  import { listAuditLogs } from '../lib/api';
  import type { AuditLogEntry, AuditLogFilter } from '../lib/types';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';

  interface Props {
    onback: () => void;
  }

  let { onback }: Props = $props();

  /** 1 ページの件数。**サーバ側の上限（200）より小さくしてある。** */
  const pageSize = 50;

  let entries = $state<AuditLogEntry[]>([]);
  let hasMore = $state(false);
  let offset = $state(0);
  let loading = $state(true);
  let error = $state('');

  /** 入力中の条件。**「絞り込む」を押すまで反映しない**（1 文字ごとに投げない） */
  let draft = $state<AuditLogFilter>(emptyFilter());

  /** 実際に効いている条件。 */
  let applied = $state<AuditLogFilter>(emptyFilter());

  $effect(() => {
    void reload(applied, offset);
  });

  function emptyFilter(): AuditLogFilter {
    return { failedOnly: false, action: '', from: '', to: '' };
  }

  async function reload(filter: AuditLogFilter, from: number) {
    loading = true;
    const result = await listAuditLogs(filter, from, pageSize);
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    entries = result.value.entries;
    hasMore = result.value.hasMore;
  }

  function apply(event: SubmitEvent) {
    event.preventDefault();

    // **条件を変えたらページも先頭へ戻す。**
    // 戻さないと、絞った結果の 3 ページ目という空の画面が出る
    offset = 0;
    applied = { ...draft };
  }

  function clear() {
    offset = 0;
    draft = emptyFilter();
    applied = emptyFilter();
  }

  /**
   * 誰がやったか。**もう居ない管理者でも「誰か」は分かるようにする。**
   *
   * **値が無い項目は `undefined` で届く**（サーバは null を落として返す）。
   * `!== null` では守れないので、必ず `??` で受ける。
   */
  function who(entry: AuditLogEntry): string {
    const loginId = entry.adminLoginId ?? '';
    if (loginId !== '') {
      return loginId;
    }

    const adminUserId = entry.adminUserId ?? '';
    if (adminUserId !== '') {
      return t('audit.removedAdmin', { id: adminUserId.slice(0, 8) });
    }

    // 認証を通っていない試み（ログインの失敗など）
    return t('audit.unknownAdmin');
  }

  /**
   * 何を触ったか。
   *
   * **識別子は頭だけ出す。** GUID をまるごと並べると表が横に伸びるうえ、
   * 見分けるには頭の数文字で足りる（全体は補足に残っている）。
   */
  function target(entry: AuditLogEntry): string {
    const type = entry.targetType ?? '';
    if (type === '') {
      return '';
    }

    const id = entry.targetId ?? '';
    return id === '' ? type : `${type} ${id.slice(0, 8)}`;
  }

  /** 断られた操作か。**目で追えるように色を変える。** */
  function refused(entry: AuditLogEntry): boolean {
    return (entry.statusCode ?? 0) >= 400;
  }
</script>

<section>
  <header class="head">
    <button type="button" class="secondary" onclick={onback}>{t('audit.back')}</button>
    <h1>{t('audit.title')}</h1>
  </header>

  <p class="lead">
    {t('audit.lead')}
    <strong>{t('audit.leadStrong')}</strong>
    {t('audit.retention')}
  </p>

  <form class="filters" onsubmit={apply}>
    <label class="check">
      <input type="checkbox" bind:checked={draft.failedOnly} />
      <span>{t('audit.failedOnly')}</span>
    </label>

    <label>
      <span>{t('audit.filterAction')}</span>
      <input type="search" bind:value={draft.action} />
    </label>

    <label>
      <span>{t('audit.from')}</span>
      <input type="datetime-local" bind:value={draft.from} />
    </label>

    <label>
      <span>{t('audit.to')}</span>
      <input type="datetime-local" bind:value={draft.to} />
    </label>

    <div class="actions">
      <button type="submit">{t('audit.apply')}</button>
      <button type="button" class="secondary" onclick={clear}>{t('audit.clear')}</button>
    </div>
  </form>

  {#if error !== ''}
    <p class="error">{error}</p>
  {/if}

  {#if loading}
    <p class="status">{t('audit.loading')}</p>
  {:else if entries.length === 0}
    <p class="status">{t('audit.empty')}</p>
  {:else}
    <!-- **横に長い。** 画面を横に伸ばさず、表の中だけ流す -->
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>{t('audit.occurredAt')}</th>
            <th>{t('audit.who')}</th>
            <th>{t('audit.action')}</th>
            <th>{t('audit.result')}</th>
            <th>{t('audit.target')}</th>
            <th>{t('audit.detail')}</th>
            <th>{t('audit.ipAddress')}</th>
          </tr>
        </thead>
        <tbody>
          <!--
            **並び順そのものを鍵にする。** 日時は秒までしか残していないので
            （`DbTime.ForDb`）、同じ秒に同じ操作が 2 件並ぶことがある。
            **鍵がぶつかると Svelte は描画ごと止める。**
          -->
          {#each entries as entry, index (index)}
            <tr class:refused={refused(entry)}>
              <td class="nowrap">{formatDateTime(new Date(entry.occurredAt))}</td>
              <td class="nowrap">{who(entry)}</td>
              <td class="mono action">{entry.action}</td>
              <td class="nowrap">{entry.statusCode ?? t('audit.noResult')}</td>
              <td class="mono nowrap">{target(entry)}</td>
              <!--
                **必ず逃がして描く。** Svelte の `{}` は逃がすが、
                `{@html}` にしないことが約束（記録側でも HTML に効く文字は逃がしてある）
              -->
              <td class="mono detail">{entry.detail ?? ''}</td>
              <td class="nowrap">{entry.ipAddress ?? ''}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>

    <nav class="pager">
      <button
        type="button"
        class="secondary"
        disabled={offset === 0}
        onclick={() => (offset = Math.max(offset - pageSize, 0))}
      >
        {t('audit.previous')}
      </button>

      <span class="range">
        {t('audit.page', { from: offset + 1, to: offset + entries.length })}
      </span>

      <button
        type="button"
        class="secondary"
        disabled={!hasMore}
        onclick={() => (offset = offset + pageSize)}
      >
        {t('audit.next')}
      </button>
    </nav>
  {/if}
</section>

<style lang="scss">
  .head {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  h1 {
    font-size: 1.25rem;
    margin: 0;
  }

  .lead {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .filters {
    display: flex;
    flex-wrap: wrap;
    align-items: end;
    gap: 0.75rem 1rem;
    padding: 1rem;
    margin-bottom: 1rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  .filters label {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    font-size: 0.8rem;
    color: var(--muted);
  }

  .filters label.check {
    flex-direction: row;
    align-items: center;
    gap: 0.4rem;
  }

  .filters input {
    font: inherit;
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
  }

  .actions {
    display: flex;
    gap: 0.5rem;
    margin-left: auto;
  }

  .scroll {
    overflow-x: auto;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  table {
    width: 100%;
    /*
      **窮屈にしない。** 100% だけだと各列が限界まで縮み、
      補足の識別子が 1 文字ずつ折り返して行が縦に伸びる。
      **入れ物（.scroll）が横に流せるので、下限を決めてよい。**
    */
    min-width: 64rem;
    border-collapse: collapse;
    font-size: 0.85rem;
  }

  th,
  td {
    text-align: left;
    padding: 0.5rem 0.75rem;
    border-bottom: 1px solid var(--border);
    vertical-align: top;
  }

  th {
    color: var(--muted);
    font-weight: 600;
    white-space: nowrap;
  }

  .nowrap {
    white-space: nowrap;
  }

  .mono {
    font-family: var(--font-mono);
    font-size: 0.8rem;
  }

  /* **補足は長くなる。** 広がりすぎず、細くもなりすぎない幅にする */
  .detail {
    min-width: 16rem;
    max-width: 24rem;
    overflow-wrap: anywhere;
  }

  /* **操作名は折り返してよいが、途中で切らない。** 経路として読めなくなる */
  .action {
    min-width: 20rem;
  }

  /* **断られた操作は目で追えるようにする。** 色だけに頼らず、左端に印も付ける */
  tr.refused td:first-child {
    border-left: 3px solid var(--error);
  }

  tr.refused td {
    color: var(--error);
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

  .status {
    color: var(--muted);
  }

  .error {
    color: var(--error);
  }
</style>
