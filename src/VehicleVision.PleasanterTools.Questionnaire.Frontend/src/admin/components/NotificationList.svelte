<script lang="ts">
  import { listNotifications, markNotificationsRead } from '../lib/api';
  import type { AdminNotification } from '../lib/types';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';

  interface Props {
    onback: () => void;
    /** 未読の件数が変わったことを親へ伝える。**ヘッダのバッジと数字を合わせる。** */
    onunread?: (count: number) => void;
  }

  let { onback, onunread }: Props = $props();

  /** 1 ページの件数。**サーバ側の上限（200）より小さくしてある。** */
  const pageSize = 50;

  /** 種類の名前と文言の対応。**サーバの `AdminNotificationKind` と揃える。** */
  const KIND_KEYS: Record<string, MessageKey | undefined> = {
    DeadLettered: 'notifications.kind.DeadLettered',
    BacklogBlockedSurvey: 'notifications.kind.BacklogBlockedSurvey',
    BacklogBlockedTotal: 'notifications.kind.BacklogBlockedTotal',
    PleasanterUnauthorized: 'notifications.kind.PleasanterUnauthorized',
    ResponseLimitReached: 'notifications.kind.ResponseLimitReached',
  };

  let items = $state<AdminNotification[]>([]);
  let hasMore = $state(false);
  let unreadCount = $state(0);
  let offset = $state(0);
  let loading = $state(true);
  let error = $state('');

  $effect(() => {
    void reload(offset);
  });

  async function reload(from: number) {
    loading = true;
    const result = await listNotifications(from, pageSize);
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    items = result.value.items;
    hasMore = result.value.hasMore;
    unreadCount = result.value.unreadCount;
    onunread?.(unreadCount);
  }

  async function markAllRead() {
    const result = await markNotificationsRead();

    if (!result.ok) {
      error = result.message;
      return;
    }

    // **既読にしたら読み直す。** 画面の「未読」の印も一緒に消える
    await reload(offset);
  }

  /**
   * 何が起きたか。
   *
   * **知らない種類でも一覧を壊さない。** 新しい種類の知らせを古い画面が読んでも、
   * 「不明なお知らせ」として並ぶだけで済むようにする（サーバ側も `Unknown` を返す）。
   * **文言の鍵を種類名から組み立てない。** 組み立てると、存在しない鍵を引いたときに
   * 画面ごと止まる。
   */
  function kind(entry: AdminNotification): string {
    const key = KIND_KEYS[entry.kind];

    return t(key ?? 'notifications.kind.Unknown');
  }

  /**
   * どのアンケートか。
   *
   * **紐づかない知らせでは `surveyId` が届かない**（サーバが落として返す）。
   * **消えたアンケートでも「あった」ことは見えるようにする。**
   */
  function survey(entry: AdminNotification): string {
    const surveyId = entry.surveyId ?? '';
    if (surveyId === '') {
      return t('notifications.noSurvey');
    }

    const title = entry.surveyTitle ?? '';
    return title === ''
      ? t('notifications.removedSurvey', { id: surveyId.slice(0, 8) })
      : title;
  }

  function unread(entry: AdminNotification): boolean {
    return (entry.readAt ?? '') === '';
  }
</script>

<section>
  <header class="head">
    <button type="button" class="secondary" onclick={onback}>{t('notifications.back')}</button>
    <h1>{t('notifications.title')}</h1>

    {#if unreadCount > 0}
      <span class="badge">{t('notifications.unread', { count: unreadCount })}</span>
      <button type="button" class="secondary" onclick={markAllRead}>
        {t('notifications.markAllRead')}
      </button>
    {/if}
  </header>

  <p class="lead">
    {t('notifications.lead')}
    <strong>{t('notifications.leadStrong')}</strong>
    {t('notifications.retention')}
  </p>

  {#if error !== ''}
    <p class="error">{error}</p>
  {/if}

  {#if loading}
    <p class="status">{t('notifications.loading')}</p>
  {:else if items.length === 0}
    <p class="status">{t('notifications.empty')}</p>
  {:else}
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>{t('notifications.occurredAt')}</th>
            <th>{t('notifications.kind')}</th>
            <th>{t('notifications.survey')}</th>
            <th>{t('notifications.count')}</th>
            <th>{t('notifications.firstOccurredAt')}</th>
            <th>{t('notifications.state')}</th>
          </tr>
        </thead>
        <tbody>
          <!-- **識別子を鍵にする。** 知らせは 1 行ずつ別の識別子を持っている -->
          {#each items as entry (entry.id)}
            <tr class:unread={unread(entry)}>
              <td class="nowrap">{formatDateTime(new Date(entry.lastOccurredAt))}</td>
              <td>{kind(entry)}</td>
              <td>{survey(entry)}</td>
              <td class="nowrap">{entry.count}</td>
              <td class="nowrap">{formatDateTime(new Date(entry.firstOccurredAt))}</td>
              <td class="nowrap">
                {unread(entry) ? t('notifications.isUnread') : t('notifications.isRead')}
              </td>
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
        {t('notifications.previous')}
      </button>

      <span class="range">
        {t('notifications.page', { from: offset + 1, to: offset + items.length })}
      </span>

      <button
        type="button"
        class="secondary"
        disabled={!hasMore}
        onclick={() => (offset = offset + pageSize)}
      >
        {t('notifications.next')}
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

  .badge {
    padding: 0.15rem 0.5rem;
    border-radius: 999px;
    background: var(--error);
    color: #fff;
    font-size: 0.8rem;
  }

  .lead {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .scroll {
    overflow-x: auto;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  table {
    width: 100%;
    min-width: 48rem;
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

  /* **未読は目で追えるようにする。** 色だけに頼らず、左端に印も付ける */
  tr.unread td:first-child {
    border-left: 3px solid var(--error);
  }

  tr.unread td {
    font-weight: 600;
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
