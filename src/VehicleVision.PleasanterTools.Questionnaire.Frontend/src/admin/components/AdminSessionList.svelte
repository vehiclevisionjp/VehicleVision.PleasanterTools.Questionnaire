<script lang="ts">
  import {
    listAdminSessions,
    revokeAdminSession,
    revokeOtherOwnSessions,
  } from '../lib/api';
  import type { AdminSessionRow } from '../lib/types';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    /** 無ければ自分のセッションを扱う。 */
    adminUserId?: string;
    canRevoke?: boolean;
  }

  let { adminUserId, canRevoke = true }: Props = $props();
  let sessions = $state<AdminSessionRow[]>([]);
  let loading = $state(true);
  let error = $state('');
  let busy = $state(false);

  $effect(() => {
    void refresh(adminUserId);
  });

  async function refresh(userId = adminUserId) {
    loading = true;
    const result = await listAdminSessions(userId);
    loading = false;
    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    sessions = result.value.sessions;
  }

  async function revoke(session: AdminSessionRow) {
    if (
      !(await confirmAction({
        title: t('sessions.revoke'),
        message: t('sessions.confirmRevoke'),
        confirmLabel: t('sessions.revoke'),
        danger: true,
      }))
    ) {
      return;
    }

    busy = true;
    const result = await revokeAdminSession(session.adminSessionId, adminUserId);
    busy = false;
    if (!result.ok) {
      error = result.message;
      return;
    }
    await refresh();
  }

  async function revokeOthers() {
    if (
      !(await confirmAction({
        title: t('sessions.revokeOthers'),
        message: t('sessions.confirmRevokeOthers'),
        confirmLabel: t('sessions.revokeOthers'),
        danger: true,
      }))
    ) {
      return;
    }

    busy = true;
    const result = await revokeOtherOwnSessions();
    busy = false;
    if (!result.ok) {
      error = result.message;
      return;
    }
    await refresh();
  }

  function when(value: string): string {
    return new Date(value).toLocaleString();
  }
</script>

<div class="sessions">
  <div class="title-row">
    <h3>{t('sessions.title')}</h3>
    {#if adminUserId === undefined && sessions.some((session) => !session.current)}
      <button type="button" class="link danger" disabled={busy} onclick={revokeOthers}>
        {t('sessions.revokeOthers')}
      </button>
    {/if}
  </div>
  <p class="hint">{t('sessions.lead')}</p>

  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if loading}
    <p class="hint">{t('sessions.loading')}</p>
  {:else if sessions.length === 0}
    <p class="hint">{t('sessions.empty')}</p>
  {:else}
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>{t('sessions.device')}</th>
            <th>{t('sessions.ipAddress')}</th>
            <th>{t('sessions.signedInAt')}</th>
            <th>{t('sessions.expiresAt')}</th>
            <th>{t('sessions.action')}</th>
          </tr>
        </thead>
        <tbody>
          {#each sessions as session (session.adminSessionId)}
            <tr>
              <td class="device">
                {session.userAgent ?? t('sessions.unknown')}
                {#if session.current}<strong>{t('sessions.current')}</strong>{/if}
              </td>
              <td>{session.ipAddress ?? t('sessions.unknown')}</td>
              <td>{when(session.createdAt)}</td>
              <td>{when(session.expiresAt)}</td>
              <td>
                {#if canRevoke && !session.current}
                  <button type="button" class="link danger" disabled={busy} onclick={() => revoke(session)}>
                    {t('sessions.revoke')}
                  </button>
                {/if}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<style lang="scss">
  .sessions {
    margin-top: 0.75rem;
  }
  .title-row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 1rem;
  }
  h3 {
    margin: 0;
    font-size: 0.95rem;
  }
  .hint {
    color: var(--muted);
    font-size: 0.8rem;
    margin: 0.25rem 0 0.75rem;
  }
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
    padding: 0.4rem 0.5rem;
    border-bottom: 1px solid var(--border);
    text-align: left;
    vertical-align: top;
  }
  th {
    color: var(--muted);
    white-space: nowrap;
  }
  .device {
    max-width: 28rem;
    overflow-wrap: anywhere;
  }
  .device strong {
    display: block;
    color: var(--success);
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
  .link.danger,
  .error {
    color: var(--error);
  }
</style>
