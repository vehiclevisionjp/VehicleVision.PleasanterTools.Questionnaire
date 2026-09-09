<script lang="ts">
  /**
   * 管理者の一覧と管理（Issue #156）。
   *
   * **押せない釦は出さない。** 決まり（最後の Administrator を止めない・自分は触らない）は
   * `lib/adminUsers.ts` に置き、**サーバ側の判定と同じ形**にしてある。
   * ⚠️ **入口を隠すのは守りではない。** 守りはサーバ側にある。
   */
  import {
    inviteAdminUser,
    listAdminUsers,
    reissueInvitation,
    resetAdminUserTwoFactor,
    setAdminUserDisabled,
    setAdminUserRole,
  } from '../lib/api';
  import {
    ADMIN_ROLES,
    canChangeRole,
    canDisable,
    canReissueInvitation,
    canResetTwoFactor,
    invitationUrl,
    roleLabel,
  } from '../lib/adminUsers';
  import type { AdminUserRow, IssuedInvitation } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    /** 自分の管理者 ID。**自分自身への操作を止めるために要る。** */
    ownAdminUserId: string;
    /** 追加・役割変更・停止ができるか（`users.write`）。 */
    canWrite: boolean;
    /** 他人の 2 要素を解除できるか（`users.resetTwoFactor`）。 */
    canReset: boolean;
    onback: () => void;
  }

  let { ownAdminUserId, canWrite, canReset, onback }: Props = $props();

  let users = $state<AdminUserRow[]>([]);
  let loading = $state(true);
  let error = $state('');
  let busy = $state('');

  /** 追加する相手。 */
  let newLoginId = $state('');
  let newRole = $state<string>('Editor');

  /**
   * 出したばかりの招待。
   *
   * ⚠️ **トークンを見せられるのはこの時だけ。** 保存しているのはハッシュのみで、
   * 画面を閉じると二度と出せない（出し直しはできる）。
   */
  let issued = $state<IssuedInvitation | null>(null);

  $effect(() => {
    void refresh();
  });

  async function refresh() {
    const result = await listAdminUsers();
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    users = result.value.users;
  }

  /** 1 つの操作を回す。**押している間は二度押させない。** */
  async function run(key: string, action: () => Promise<{ ok: boolean; message?: string }>) {
    busy = key;
    error = '';
    const result = await action();
    busy = '';

    if (!result.ok) {
      error = result.message ?? t('users.failed');
      return;
    }

    await refresh();
  }

  async function invite(event: SubmitEvent) {
    event.preventDefault();
    issued = null;

    busy = 'invite';
    error = '';
    const result = await inviteAdminUser(newLoginId.trim(), newRole);
    busy = '';

    if (!result.ok) {
      error = result.message;
      return;
    }

    // **URL はこの場で渡す。** メールを送る仕組みは持っていない
    issued = result.value;
    newLoginId = '';
    await refresh();
  }

  function loginIdOf(adminUserId: string): string {
    return users.find((user) => user.adminUserId === adminUserId)?.loginId ?? '';
  }

  /** 日時を読める形にする。**一度も入っていなければ「—」。** */
  function when(value: string | null): string {
    return value === null ? '—' : new Date(value).toLocaleString();
  }
</script>

<section>
  <header class="head">
    <button type="button" class="link" onclick={onback}>{t('users.back')}</button>
    <h1>{t('users.title')}</h1>
  </header>

  <p class="lead">{t('users.lead')}</p>

  {#if canWrite}
    <form class="invite" onsubmit={invite}>
      <label>
        {t('users.newLoginId')}
        <input type="text" bind:value={newLoginId} required autocomplete="off" />
      </label>

      <label>
        {t('users.newRole')}
        <select bind:value={newRole}>
          {#each ADMIN_ROLES as role (role)}
            <option value={role}>{roleLabel(role)}</option>
          {/each}
        </select>
      </label>

      <button type="submit" disabled={busy !== '' || newLoginId.trim() === ''}>
        {busy === 'invite' ? t('users.inviting') : t('users.invite')}
      </button>
    </form>

    {#if issued}
      <!-- ⚠️ **ここでしか出せない。** 保存しているのはハッシュのみ -->
      <div class="issued">
        <p>
          <strong>{t('users.invitationIssued', { loginId: loginIdOf(issued.adminUserId) })}</strong>
        </p>
        <p class="url"><code>{invitationUrl(issued.invitationToken, location.origin)}</code></p>
        <p class="note">
          {t('users.invitationNote', { expiresAt: when(issued.expiresAt) })}
        </p>
      </div>
    {/if}
  {/if}

  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if loading}
    <p class="status">{t('users.loading')}</p>
  {:else if users.length === 0}
    <p class="status">{t('users.empty')}</p>
  {:else}
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>{t('users.column.loginId')}</th>
            <th>{t('users.column.role')}</th>
            <th>{t('users.column.state')}</th>
            <th>{t('users.column.twoFactor')}</th>
            <th>{t('users.column.lastLogin')}</th>
            <th>{t('users.column.actions')}</th>
          </tr>
        </thead>
        <tbody>
          {#each users as user (user.adminUserId)}
            <tr class:disabled={user.isDisabled}>
              <td>
                {user.loginId}
                {#if user.adminUserId === ownAdminUserId}
                  <span class="self">{t('users.you')}</span>
                {/if}
              </td>

              <td>
                {#if canWrite}
                  <select
                    value={user.role}
                    disabled={busy !== ''}
                    onchange={(event) => {
                      const next = event.currentTarget.value;
                      void run(`role:${user.adminUserId}`, () =>
                        setAdminUserRole(user.adminUserId, next),
                      );
                    }}
                  >
                    {#each ADMIN_ROLES as role (role)}
                      <!-- **押せない選択肢は出さない**（最後の Administrator は降ろせない） -->
                      <option
                        value={role}
                        disabled={role !== user.role &&
                          !canChangeRole(users, user, ownAdminUserId, role)}
                      >
                        {roleLabel(role)}
                      </option>
                    {/each}
                  </select>
                {:else}
                  {roleLabel(user.role)}
                {/if}
              </td>

              <td>
                {#if user.isDisabled}
                  <span class="badge stopped">{t('users.state.disabled')}</span>
                {:else if user.invitationPending}
                  <span class="badge pending">{t('users.state.invited')}</span>
                {:else}
                  <span class="badge active">{t('users.state.active')}</span>
                {/if}
              </td>

              <td>{user.hasTotp ? t('users.twoFactor.on') : t('users.twoFactor.off')}</td>
              <td class="when">{when(user.lastLoginAt)}</td>

              <td class="actions">
                {#if canWrite && canReissueInvitation(user)}
                  <button
                    type="button"
                    class="link"
                    disabled={busy !== ''}
                    onclick={() => {
                      issued = null;
                      void run(`invitation:${user.adminUserId}`, async () => {
                        const result = await reissueInvitation(user.adminUserId);
                        if (result.ok) {
                          issued = result.value;
                        }
                        return result;
                      });
                    }}
                  >
                    {t('users.reissue')}
                  </button>
                {/if}

                {#if canWrite && canDisable(users, user, ownAdminUserId)}
                  <button
                    type="button"
                    class="link danger"
                    disabled={busy !== ''}
                    onclick={() => {
                      if (!confirm(t('users.confirmDisable', { loginId: user.loginId }))) {
                        return;
                      }
                      void run(`disable:${user.adminUserId}`, () =>
                        setAdminUserDisabled(user.adminUserId, true),
                      );
                    }}
                  >
                    {t('users.disable')}
                  </button>
                {/if}

                {#if canWrite && user.isDisabled}
                  <button
                    type="button"
                    class="link"
                    disabled={busy !== ''}
                    onclick={() =>
                      void run(`enable:${user.adminUserId}`, () =>
                        setAdminUserDisabled(user.adminUserId, false),
                      )}
                  >
                    {t('users.enable')}
                  </button>
                {/if}

                {#if canReset && canResetTwoFactor(user, ownAdminUserId)}
                  <!-- ⚠️ **保護を外す操作。** 押す前に確かめ、記録に残ることも伝える -->
                  <button
                    type="button"
                    class="link danger"
                    disabled={busy !== ''}
                    onclick={() => {
                      if (!confirm(t('users.confirmResetTwoFactor', { loginId: user.loginId }))) {
                        return;
                      }
                      void run(`totp:${user.adminUserId}`, () =>
                        resetAdminUserTwoFactor(user.adminUserId),
                      );
                    }}
                  >
                    {t('users.resetTwoFactor')}
                  </button>
                {/if}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</section>

<style lang="scss">
  .head {
    display: flex;
    align-items: baseline;
    gap: 1rem;
    margin-bottom: 0.5rem;
  }

  h1 {
    font-size: 1.15rem;
    margin: 0;
  }

  .lead {
    color: var(--muted);
    font-size: 0.9rem;
    margin: 0 0 1.25rem;
  }

  .invite {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-end;
    gap: 0.75rem;
    padding: 1rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 6px;
    margin-bottom: 1rem;
  }

  label {
    display: block;
    font-size: 0.85rem;
    color: var(--muted);
  }

  input,
  select {
    display: block;
    margin-top: 0.25rem;
    padding: 0.4rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    color: initial;
  }

  .issued {
    padding: 0.75rem 1rem;
    border: 1px solid var(--accent);
    border-radius: 6px;
    background: #f5f9ff;
    margin-bottom: 1rem;
  }

  .issued p {
    margin: 0 0 0.4rem;
  }

  .url code {
    /* **長い URL を折り返す。** 選んで写せる形のままにする */
    word-break: break-all;
    font-size: 0.85rem;
  }

  .note {
    color: var(--muted);
    font-size: 0.8rem;
  }

  .scroll {
    /* **表は自分の中で横に流す**（画面ごと流さない） */
    overflow-x: auto;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
    background: #fff;
  }

  th,
  td {
    border-bottom: 1px solid var(--border);
    padding: 0.5rem 0.6rem;
    text-align: left;
    vertical-align: middle;
  }

  th {
    color: var(--muted);
    font-weight: 600;
    font-size: 0.8rem;
    white-space: nowrap;
  }

  tr.disabled {
    color: var(--muted);
  }

  .when {
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .self {
    color: var(--muted);
    font-size: 0.75rem;
  }

  .badge {
    display: inline-block;
    padding: 0.1rem 0.45rem;
    border-radius: 999px;
    font-size: 0.75rem;
    white-space: nowrap;
  }

  .badge.active {
    background: #ecfdf3;
    color: #027a48;
  }

  .badge.pending {
    background: #fffaeb;
    color: #b54708;
  }

  .badge.stopped {
    background: #f2f4f7;
    color: var(--muted);
  }

  button[type='submit'] {
    padding: 0.45rem 1rem;
    border: 0;
    border-radius: 4px;
    background: var(--accent);
    color: #fff;
    font: inherit;
    cursor: pointer;
  }

  button[type='submit']:disabled {
    background: var(--border);
    cursor: default;
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

  .link:disabled {
    color: var(--muted);
    cursor: default;
  }

  .link.danger {
    color: var(--error);
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }

  .status {
    color: var(--muted);
    font-size: 0.9rem;
  }
</style>
