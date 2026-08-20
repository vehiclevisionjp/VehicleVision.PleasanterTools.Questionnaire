<script lang="ts">
  import { getOutboxStatus, listDeadLetters, requeueDeadLetter } from '../lib/api';
  import type { DeadLetterEntry, OutboxStatus } from '../lib/types';
  import { formatDateTime, formatElapsed, t } from '../lib/i18n/state.svelte';

  interface Props {
    onback: () => void;
  }

  let { onback }: Props = $props();

  /** 1 ページの件数。**サーバ側の上限（200）より小さくしてある。** */
  const pageSize = 25;

  let status = $state<OutboxStatus>();
  let entries = $state<DeadLetterEntry[]>([]);
  let hasMore = $state(false);
  let offset = $state(0);
  let loading = $state(true);
  let error = $state('');
  let notice = $state('');

  /** 戻している最中の回答。**二重に押させない。** */
  let requeuing = $state('');

  // **ページを変えたら読み直す。** 読み直しはここと、明示的に呼ぶ所だけ
  $effect(() => {
    void reload(offset);
  });

  async function reload(from: number) {
    loading = true;

    // **状況と一覧をまとめて取りに行く。** 片方ずつ待つ理由が無い
    const [statusResult, pageResult] = await Promise.all([
      getOutboxStatus(),
      listDeadLetters(from, pageSize),
    ]);

    loading = false;

    if (!statusResult.ok) {
      error = statusResult.message;
      return;
    }

    if (!pageResult.ok) {
      error = pageResult.message;
      return;
    }

    error = '';
    status = statusResult.value;
    entries = pageResult.value.entries;
    hasMore = pageResult.value.hasMore;
  }

  /**
   * 送信待ちへ戻す。
   *
   * **押した人の判断で 1 件ずつ戻す**（`_documents/画面設計.md` 2 章）。
   * まとめて戻す釦は置かない。原因が直っていなければ、同じだけ失敗するだけになる。
   */
  async function requeue(entry: DeadLetterEntry) {
    requeuing = entry.responseToken;
    const result = await requeueDeadLetter(entry.responseToken);
    requeuing = '';

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    notice = t('outbox.requeued');

    // **戻したら読み直す。** 件数も一覧も変わっている
    await reload(offset);
  }

  /** 滞留で受付を止めているか。**全体でも 1 本でも、止めていれば目立たせる。** */
  const blockedByBacklog = $derived(
    status?.backlog?.enabled === true &&
      (status.backlog.totalBlocked || status.backlog.blockedSurveyCount > 0),
  );

  /** 止めている間だけ読み上げさせる。**平常時に読み上げると慣れて聞き流される。** */
  const backlogRole = $derived(status?.backlog?.totalBlocked === true ? 'alert' : undefined);

  const sampledAt = $derived(at(status?.backlog?.sampledAt));

  /**
   * 値が届いていれば `Date` にする。
   *
   * **サーバは null のプロパティを落として返す**ので、`??` で受ける。
   */
  function at(value: string | null | undefined): Date | null {
    return value ? new Date(value) : null;
  }

  /** 時刻と、そこからの経過。**経過だけの方が異常に気付ける。** */
  function since(value: string | null | undefined): string {
    const date = at(value);
    if (date === null) {
      return t('outbox.none');
    }

    return t('outbox.since', {
      at: formatDateTime(date),
      elapsed: formatElapsed(date),
    });
  }

  /**
   * どのアンケートの回答か。
   *
   * **アンケートが消えていても「どれか」は分かるようにする。**
   * 識別子は頭だけ出す（並べると表が横に伸びる）。
   */
  function survey(entry: DeadLetterEntry): string {
    const title = entry.surveyTitle ?? '';
    return title !== '' ? title : t('outbox.removedSurvey', { id: entry.surveyId.slice(0, 8) });
  }
</script>

<section>
  <header class="head">
    <button type="button" class="secondary" onclick={onback}>{t('outbox.back')}</button>
    <h1>{t('outbox.title')}</h1>
    <button
      type="button"
      class="secondary reload"
      disabled={loading}
      onclick={() => void reload(offset)}
    >
      {t('outbox.reload')}
    </button>
  </header>

  <p class="lead">
    {t('outbox.lead')}
    <strong>{t('outbox.leadStrong')}</strong>
  </p>

  {#if error !== ''}
    <p class="error" role="alert">{error}</p>
  {/if}

  {#if notice !== ''}
    <p class="notice" role="status">{notice}</p>
  {/if}

  {#if loading && status === undefined}
    <p class="status">{t('outbox.loading')}</p>
  {:else if status}
    <!--
      **数字だけでは気付けない。** 「何件あるか」より
      「いつから詰まっているか」の方が異常を表す
    -->
    <dl class="cards">
      <div class="card" class:warn={status.pendingCount > 0}>
        <dt>{t('outbox.pendingCount')}</dt>
        <dd class="figure">{t('outbox.count', { count: status.pendingCount })}</dd>
        <dd class="sub">
          {t('outbox.oldestPending')}: {since(status.oldestPendingAt)}
        </dd>
      </div>

      <div class="card" class:bad={status.deadLetterCount > 0}>
        <dt>{t('outbox.deadLetterCount')}</dt>
        <dd class="figure">{t('outbox.count', { count: status.deadLetterCount })}</dd>
        <dd class="sub">
          {t('outbox.oldestDeadLetter')}: {since(status.oldestDeadLetterAt)}
        </dd>
      </div>
    </dl>

    {#if status.pendingCount === 0 && status.deadLetterCount === 0}
      <p class="status">{t('outbox.healthy')}</p>
    {/if}

    <!--
      **止めているかどうかは件数から読み取れない**（Issue #72）。
      「送信待ちが多い」と「そのせいで受付を止めている」は別のこと
    -->
    <section class="backlog" class:bad={blockedByBacklog}>
      <h2>{t('outbox.backlogTitle')}</h2>

      {#if status.backlog?.enabled !== true}
        <p class="status">{t('outbox.backlogOff')}</p>
      {:else}
        <p class:alarm={status.backlog.totalBlocked} role={backlogRole}>
          {status.backlog.totalBlocked
            ? t('outbox.backlogTotalBlocked', {
                total: status.backlog.total,
                limit: status.backlog.totalLimit,
              })
            : t('outbox.backlogOk', {
                total: status.backlog.total,
                limit: status.backlog.totalLimit,
              })}
        </p>

        {#if status.backlog.blockedSurveyCount > 0}
          <p class="alarm">
            {t('outbox.backlogSurveys', {
              count: status.backlog.blockedSurveyCount,
              limit: status.backlog.perSurveyLimit,
            })}
          </p>
        {/if}

        <!-- **手で止めた・回答数の上限とは別物だと明記する。** 取り違えると復旧の手が変わる -->
        <p class="sub">{t('outbox.backlogAuto')}</p>

        <p class="sub">
          {sampledAt === null
            ? t('outbox.backlogNeverSampled')
            : t('outbox.backlogSampledAt', { at: formatDateTime(sampledAt) })}
        </p>
      {/if}
    </section>
  {/if}

  <h2>{t('outbox.deadLetterTitle')}</h2>

  <p class="lead">
    {t('outbox.deadLetterLead')}
    <strong>{t('outbox.deadLetterLeadStrong')}</strong>
  </p>

  {#if loading && entries.length === 0}
    <p class="status">{t('outbox.loading')}</p>
  {:else if entries.length === 0}
    <p class="status">{t('outbox.deadLetterEmpty')}</p>
  {:else}
    <!-- **横に長い。** 画面を横に伸ばさず、表の中だけ流す -->
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>{t('outbox.survey')}</th>
            <th>{t('outbox.version')}</th>
            <th>{t('outbox.responseToken')}</th>
            <th>{t('outbox.receivedAt')}</th>
            <th>{t('outbox.lastAttemptAt')}</th>
            <th>{t('outbox.retryCount')}</th>
            <th>{t('outbox.lastError')}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <!-- **回答トークンが鍵。** 表の中で一意 -->
          {#each entries as entry (entry.responseToken)}
            <tr>
              <td>{survey(entry)}</td>
              <td class="nowrap">{entry.surveyVersion}</td>
              <!--
                **回答本文は出さない。** 出せるのはトークンまで。
                問い合わせを受けたときに回答者と突き合わせる手掛かりになる
              -->
              <td class="mono token">{entry.responseToken}</td>
              <td class="nowrap">{formatDateTime(new Date(entry.receivedAt))}</td>
              <td class="nowrap">{formatDateTime(new Date(entry.lastAttemptAt))}</td>
              <td class="nowrap">{t('outbox.retryTimes', { count: entry.retryCount })}</td>
              <!--
                **必ず逃がして描く。** Svelte の `{}` は逃がすが、
                `{@html}` にしないことが約束（理由は Pleasanter が返した文字列を含み得るため）
              -->
              <td class="reason">{entry.lastError ?? t('outbox.noError')}</td>
              <td>
                <button
                  type="button"
                  class="secondary"
                  disabled={requeuing !== ''}
                  onclick={() => requeue(entry)}
                >
                  {requeuing === entry.responseToken
                    ? t('outbox.requeuing')
                    : t('outbox.requeue')}
                </button>
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
        {t('outbox.previous')}
      </button>

      <span class="range">
        {t('outbox.page', { from: offset + 1, to: offset + entries.length })}
      </span>

      <button
        type="button"
        class="secondary"
        disabled={!hasMore}
        onclick={() => (offset = offset + pageSize)}
      >
        {t('outbox.next')}
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

  .head .reload {
    margin-left: auto;
  }

  h1 {
    font-size: 1.25rem;
    margin: 0;
  }

  h2 {
    font-size: 1rem;
    margin: 2rem 0 0.25rem;
  }

  .lead {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .cards {
    display: flex;
    flex-wrap: wrap;
    gap: 1rem;
    margin: 1rem 0 0;
  }

  .card {
    flex: 1 1 16rem;
    padding: 1rem;
    background: #fff;
    border: 1px solid var(--border);
    /* **色だけに頼らない。** 左端の太い線でも状態が分かるようにする */
    border-left-width: 4px;
    border-radius: 8px;
  }

  .card.warn {
    border-left-color: var(--accent);
  }

  .card.bad {
    border-left-color: var(--error);
  }

  .card dt {
    color: var(--muted);
    font-size: 0.8rem;
  }

  .card dd {
    margin: 0;
  }

  .figure {
    font-size: 1.5rem;
    font-weight: 600;
  }

  .sub {
    color: var(--muted);
    font-size: 0.8rem;
  }

  .scroll {
    overflow-x: auto;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  table {
    width: 100%;
    /* **窮屈にしない。** 入れ物が横に流せるので下限を決めてよい */
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
    font-family: ui-monospace, monospace;
    font-size: 0.8rem;
  }

  .token {
    overflow-wrap: anywhere;
    min-width: 12rem;
  }

  /* **理由は長くなる。** 広がりすぎず、細くもなりすぎない幅にする */
  .reason {
    min-width: 16rem;
    max-width: 28rem;
    overflow-wrap: anywhere;
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

  .backlog {
    margin-top: 1.5rem;
    padding: 1rem;
    background: #fff;
    border: 1px solid var(--border);
    /* **色だけに頼らない。** 左端の太い線でも状態が分かるようにする */
    border-left: 4px solid var(--border);
    border-radius: 8px;
  }

  .backlog.bad {
    border-left-color: var(--error);
  }

  .backlog h2 {
    margin-top: 0;
  }

  .backlog p {
    margin: 0.25rem 0 0;
    font-size: 0.9rem;
  }

  .alarm {
    color: var(--error);
    font-weight: 600;
  }

  .status {
    color: var(--muted);
  }

  .notice {
    color: var(--accent);
  }

  .error {
    color: var(--error);
  }
</style>
