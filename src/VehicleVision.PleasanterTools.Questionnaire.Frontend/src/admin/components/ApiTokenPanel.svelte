<script lang="ts">
  /**
   * 自分の API キー（個人トークン）の発行・一覧・失効（Issue #1025。親 #1012）。
   *
   * ⚠️ **平文のキーを出せるのは、発行の直後の 1 回だけ。** 画面の変数に持つだけで、
   * **ストレージ・URL・ログへは置かない**。閉じると消え、二度と出せない
   * （サーバが持つのはハッシュだけ）。
   *
   * **発行の前に本人確認をする**（パスワードの再確認。SSO で入った管理者は直前の再ログイン）。
   * 離席中の画面を奪われただけで、2 要素を迂回する資格情報を作らせないため。
   * ⚠️ **ここで隠す・絞るのは画面の親切であって守りではない。** 守りはサーバ側にある。
   */
  import { issueApiToken, revokeApiToken } from '../lib/api';
  import {
    API_TOKEN_SCOPES,
    isExpiryInRange,
    parseIpAllowListInput,
    scopeLabelKey,
  } from '../lib/apiTokens';
  import { confirmAction } from '../lib/confirmation.svelte';
  import type { ApiTokenOverview, ApiTokenRow, IssuedApiToken } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';
  import ApiTokenTable from './ApiTokenTable.svelte';

  interface Props {
    /** 一覧と、画面が出す情報。読み込み前は `null`。 */
    overview: ApiTokenOverview | null;
    /** 状態を読み直してほしい（発行・失効のあと）。 */
    onchanged: () => void;
  }

  let { overview, onchanged }: Props = $props();

  let name = $state('');
  let selectedScopes = $state<string[]>(['surveys.read']);
  let expiresInDays = $state(90);
  let ipText = $state('');
  let password = $state('');
  let busy = $state(false);
  let error = $state('');
  let copied = $state(false);

  /** 発行したばかりのキー。**閉じると消える。** */
  let issued = $state<IssuedApiToken | null>(null);

  let defaultsApplied = false;

  $effect(() => {
    // 既定の期限は、サーバの設定に従う（初回だけ当てる。入力中の値を上書きしない）
    if (overview !== null && !defaultsApplied) {
      defaultsApplied = true;
      expiresInDays = overview.defaultExpiresInDays;
    }
  });

  const scopes = $derived(
    API_TOKEN_SCOPES.filter((scope) => overview?.issuableScopes.includes(scope) ?? false),
  );
  const relogin = $derived(overview?.reauthentication === 'relogin');

  function toggleScope(scope: string, checked: boolean) {
    selectedScopes = checked
      ? [...selectedScopes, scope]
      : selectedScopes.filter((current) => current !== scope);
  }

  async function issue(event: SubmitEvent) {
    event.preventDefault();
    if (overview === null) {
      return;
    }

    error = '';
    if (selectedScopes.length === 0) {
      error = t('apiTokens.failed');
      return;
    }

    if (!isExpiryInRange(expiresInDays, overview.maxExpiresInDays)) {
      error = t('apiTokens.expiresHint', {
        max: overview.maxExpiresInDays,
        default: overview.defaultExpiresInDays,
      });
      return;
    }

    busy = true;
    const result = await issueApiToken({
      name: name.trim(),
      scopes: selectedScopes,
      expiresInDays,
      ipAllowList: parseIpAllowListInput(ipText),
      // 再ログインで確かめる管理者は、パスワードを送らない
      password: relogin ? undefined : password,
    });
    busy = false;
    password = '';

    if (!result.ok) {
      error = result.message;
      return;
    }

    issued = result.value;
    copied = false;
    name = '';
    ipText = '';
    onchanged();
  }

  async function copyIssued() {
    if (issued === null) {
      return;
    }

    try {
      await navigator.clipboard.writeText(issued.token);
      copied = true;
    } catch {
      // クリップボードを使えない環境では、値を選択して手でコピーしてもらう
      copied = false;
    }
  }

  function closeIssued() {
    issued = null;
    copied = false;
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
    error = '';
    const result = await revokeApiToken(token.adminApiTokenId);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    onchanged();
  }
</script>

<div class="card">
  <h2>{t('apiTokens.title')}</h2>
  <p class="hint">{t('apiTokens.lead')}</p>

  {#if overview === null}
    <p class="hint">{t('apiTokens.loading')}</p>
  {:else}
    {#if !overview.enabled}
      <p class="hint warning">{t('apiTokens.disabled')}</p>
    {:else if !overview.canIssue}
      <p class="hint">{t('apiTokens.roleNotAllowed')}</p>
    {/if}

    {#if issued}
      <!-- ⚠️ **ここでしか出せない。** 保存しているのはハッシュのみ -->
      <div class="issued" role="status">
        <p><strong>{t('apiTokens.issuedTitle')}</strong></p>
        <p class="value"><code data-testid="issued-api-token">{issued.token}</code></p>
        <p class="hint">{t('apiTokens.issuedNote')}</p>
        <div class="row">
          <button type="button" onclick={copyIssued}>
            {copied ? t('apiTokens.copied') : t('apiTokens.copy')}
          </button>
          <button type="button" class="link" onclick={closeIssued}>{t('apiTokens.close')}</button>
        </div>
      </div>
    {/if}

    {#if overview.canIssue}
      <form onsubmit={issue}>
        <label>
          {t('apiTokens.name')}
          <input type="text" bind:value={name} required maxlength="100" autocomplete="off" />
        </label>

        <fieldset>
          <legend>{t('apiTokens.scopes')}</legend>
          {#each scopes as scope (scope)}
            <label class="check">
              <input
                type="checkbox"
                checked={selectedScopes.includes(scope)}
                onchange={(event) => toggleScope(scope, event.currentTarget.checked)}
              />
              <span>{t(scopeLabelKey(scope)!)}</span>
            </label>
          {/each}
          {#if scopes.includes('surveys.publish')}
            <p class="hint">{t('apiTokens.publishWarning')}</p>
          {/if}
        </fieldset>

        <label>
          {t('apiTokens.expiresInDays')}
          <input
            type="number"
            bind:value={expiresInDays}
            min="1"
            max={overview.maxExpiresInDays}
            step="1"
            required
          />
        </label>
        <p class="hint">
          {t('apiTokens.expiresHint', {
            max: overview.maxExpiresInDays,
            default: overview.defaultExpiresInDays,
          })}
        </p>

        <label>
          {t('apiTokens.ipAllowList')}
          <textarea bind:value={ipText} rows="2" spellcheck="false" autocomplete="off"></textarea>
        </label>
        <p class="hint">{t('apiTokens.ipAllowListHint', { max: overview.maxIpAllowListEntries })}</p>

        {#if relogin}
          <!-- **SSO の管理者はパスワードを持たない。** 直前の再ログインで確かめる -->
          <p class="hint">{t('apiTokens.reloginNote', { minutes: overview.reloginMinutes })}</p>
        {:else}
          <label>
            {t('account.confirmPassword')}
            <input type="password" autocomplete="current-password" bind:value={password} required />
          </label>
        {/if}

        {#if error}<p class="error" role="alert">{error}</p>{/if}

        <button type="submit" disabled={busy || selectedScopes.length === 0}>
          {busy ? t('apiTokens.issuing') : t('apiTokens.issue')}
        </button>
      </form>
    {:else if error}
      <p class="error" role="alert">{error}</p>
    {/if}

    <div class="list">
      {#if overview.tokens.length === 0}
        <p class="hint">{t('apiTokens.empty')}</p>
      {:else}
        <ApiTokenTable tokens={overview.tokens} canRevoke={true} {busy} onrevoke={revoke} />
      {/if}
    </div>
  {/if}
</div>

<style lang="scss">
  h2 {
    font-size: 1rem;
    margin: 0 0 var(--space-3);
  }

  .card {
    padding: 1.25rem;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    margin-bottom: 1.25rem;
  }

  /* 入力欄まで横いっぱいに伸ばすと、かえって読みにくい */
  .card form {
    max-width: 28rem;
  }

  label {
    display: block;
    margin-bottom: var(--space-3);
    font-size: 0.9rem;
  }

  input[type='text'],
  input[type='number'],
  input[type='password'],
  textarea {
    display: block;
    width: 100%;
    margin-top: var(--space-1);
    padding: var(--space-2);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-sm);
    font: inherit;
    box-sizing: border-box;
  }

  fieldset {
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    margin: 0 0 var(--space-3);
    padding: var(--space-2) var(--space-3);
  }

  legend {
    font-size: 0.9rem;
    padding: 0 var(--space-1);
  }

  .check {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin-bottom: var(--space-1);
  }

  .check input {
    width: auto;
    margin: 0;
  }

  .hint {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 var(--space-3);
  }

  .warning {
    color: var(--warning-text, var(--muted));
  }

  .issued {
    padding: var(--space-3);
    margin-bottom: var(--space-4);
    border: 1px solid var(--success);
    border-radius: var(--radius-sm);
  }

  .issued p {
    margin: 0 0 var(--space-2);
  }

  .value code {
    display: block;
    padding: 0.4rem 0.6rem;
    background: var(--disabled-surface);
    border-radius: var(--radius-sm);
    font-family: var(--font-mono, monospace);
    word-break: break-all;
    user-select: all;
  }

  .row {
    display: flex;
    align-items: center;
    gap: var(--space-4);
  }

  .list {
    margin-top: var(--space-4);
  }

  button[type='submit'],
  .row button:not(.link) {
    padding: var(--space-2) 1.25rem;
    border: 0;
    border-radius: var(--radius-sm);
    background: var(--accent);
    color: var(--accent-text);
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

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }
</style>
