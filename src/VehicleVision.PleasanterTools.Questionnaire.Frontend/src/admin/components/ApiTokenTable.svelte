<script lang="ts">
  /**
   * API キー（個人トークン）の一覧の表（Issue #1025）。
   *
   * ⚠️ **値は出さない**（出せない）。識別は接頭辞だけ。
   * **失効の操作は呼び出し側**（本人の一覧と、管理者が他人のを見る一覧で呼び先が違う）。
   */
  import {
    daysRemaining,
    revokeReasonLabelKey,
    scopeLabelKey,
    statusLabelKey,
  } from '../lib/apiTokens';
  import type { ApiTokenRow } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    tokens: ApiTokenRow[];
    /** 失効の釦を出すか。 */
    canRevoke: boolean;
    busy: boolean;
    onrevoke: (token: ApiTokenRow) => void;
  }

  let { tokens, canRevoke, busy, onrevoke }: Props = $props();

  function when(value: string | null | undefined): string {
    return value === null || value === undefined ? '—' : new Date(value).toLocaleString();
  }

  function scopeLabel(scope: string): string {
    const key = scopeLabelKey(scope);
    return key === null ? scope : t(key);
  }
</script>

<div class="scroll">
  <table>
    <thead>
      <tr>
        <th>{t('apiTokens.column.name')}</th>
        <th>{t('apiTokens.column.prefix')}</th>
        <th>{t('apiTokens.column.scopes')}</th>
        <th>{t('apiTokens.column.status')}</th>
        <th>{t('apiTokens.column.expiresAt')}</th>
        <th>{t('apiTokens.column.lastUsed')}</th>
        <th>{t('apiTokens.column.ipAllowList')}</th>
        <th>{t('apiTokens.column.actions')}</th>
      </tr>
    </thead>
    <tbody>
      {#each tokens as token (token.adminApiTokenId)}
        <tr class:inactive={token.status !== 'active'}>
          <td class="name">{token.name}</td>
          <td><code>{token.prefix}</code></td>
          <td>
            <ul class="scopes">
              {#each token.scopes as scope (scope)}
                <li>{scopeLabel(scope)}</li>
              {/each}
            </ul>
          </td>
          <td>
            <span class="badge {token.status}">{t(statusLabelKey(token.status))}</span>
            {#if token.status === 'revoked'}
              {@const reason = revokeReasonLabelKey(token.revokeReason)}
              {#if reason !== null}<span class="sub">{t(reason)}</span>{/if}
              <span class="sub">{when(token.revokedAt)}</span>
            {/if}
          </td>
          <td>
            {when(token.expiresAt)}
            {#if token.status === 'active'}
              <span class="sub">
                {t('apiTokens.daysLeft', { days: daysRemaining(token.expiresAt, new Date()) })}
              </span>
            {/if}
          </td>
          <td>
            {#if token.lastUsedAt}
              {when(token.lastUsedAt)}
              {#if token.lastUsedIp}<span class="sub">{token.lastUsedIp}</span>{/if}
            {:else}
              {t('apiTokens.never')}
            {/if}
          </td>
          <td>
            {#if token.ipAllowList.length === 0}
              {t('apiTokens.anySource')}
            {:else}
              <ul class="scopes">
                {#each token.ipAllowList as entry (entry)}
                  <li><code>{entry}</code></li>
                {/each}
              </ul>
            {/if}
          </td>
          <td>
            {#if canRevoke && token.status === 'active'}
              <button type="button" class="link danger" disabled={busy} onclick={() => onrevoke(token)}>
                {t('apiTokens.revoke')}
              </button>
            {/if}
          </td>
        </tr>
      {/each}
    </tbody>
  </table>
</div>

<style lang="scss">
  .scroll {
    overflow-x: auto;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    background: var(--surface);
    font-size: 0.8rem;
  }

  th,
  td {
    padding: 0.4rem var(--space-2);
    border-bottom: 1px solid var(--border);
    text-align: left;
    vertical-align: top;
  }

  th {
    white-space: nowrap;
    /* **見出し行は鉄紺・白文字**（DesignReference の表の決まり） */
    background: var(--color-primary);
    color: var(--color-on-fill);
    font-weight: 700;
    border-bottom: 1px solid var(--color-primary-edge);
  }

  .inactive {
    color: var(--muted);
  }

  .name {
    overflow-wrap: anywhere;
    max-width: 14rem;
  }

  .scopes {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .sub {
    display: block;
    color: var(--muted);
    font-size: 0.75rem;
  }

  .badge {
    display: inline-block;
    padding: 0 var(--space-2);
    border-radius: var(--radius-sm);
    border: 1px solid var(--border-strong);
    font-size: 0.75rem;
  }

  .badge.active {
    color: var(--success);
    border-color: var(--success);
  }

  .badge.revoked {
    color: var(--error);
    border-color: var(--error);
  }

  .link {
    background: none;
    border: 0;
    padding: 0;
    color: var(--accent);
    font: inherit;
    cursor: pointer;
    text-decoration: underline;
  }

  .link.danger {
    color: var(--error);
  }
</style>
