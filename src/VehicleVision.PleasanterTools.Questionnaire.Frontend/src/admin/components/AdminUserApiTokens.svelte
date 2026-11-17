<script lang="ts">
  /**
   * ほかの管理者の API キーの一覧と失効（Issue #1025）。**管理者の一覧から開く。**
   *
   * **見るのは `users.read`、失効は `users.write`**（発行はできない。値も見えない）。
   * ⚠️ **押せない釦を出さないのは画面の親切であって、守りはサーバ側にある。**
   */
  import { listAdminUserApiTokens, revokeApiToken } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import type { ApiTokenRow } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';
  import ApiTokenTable from './ApiTokenTable.svelte';

  interface Props {
    adminUserId: string;
    canRevoke: boolean;
  }

  let { adminUserId, canRevoke }: Props = $props();

  let tokens = $state<ApiTokenRow[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state('');

  $effect(() => {
    void refresh(adminUserId);
  });

  async function refresh(userId = adminUserId) {
    loading = true;
    const result = await listAdminUserApiTokens(userId);
    loading = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    error = '';
    tokens = result.value.tokens;
  }

  async function revoke(token: ApiTokenRow) {
    if (
      !(await confirmAction({
        title: t('apiTokens.revoke'),
        message: t('apiTokens.confirmRevoke', { name: token.name }),
        confirmLabel: t('apiTokens.revoke'),
        danger: true,
      }))
    ) {
      return;
    }

    busy = true;
    const result = await revokeApiToken(token.adminApiTokenId, adminUserId);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    await refresh();
  }
</script>

<div class="tokens">
  <h3>{t('apiTokens.title')}</h3>

  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if loading}
    <p class="hint">{t('apiTokens.loading')}</p>
  {:else if tokens.length === 0}
    <p class="hint">{t('apiTokens.empty')}</p>
  {:else}
    <ApiTokenTable {tokens} {canRevoke} {busy} onrevoke={revoke} />
  {/if}
</div>

<style lang="scss">
  .tokens {
    margin-top: var(--space-3);
  }

  h3 {
    margin: 0;
    font-size: 0.95rem;
  }

  .hint {
    color: var(--muted);
    font-size: 0.8rem;
    margin: var(--space-1) 0 var(--space-3);
  }

  .error {
    color: var(--error);
  }
</style>
