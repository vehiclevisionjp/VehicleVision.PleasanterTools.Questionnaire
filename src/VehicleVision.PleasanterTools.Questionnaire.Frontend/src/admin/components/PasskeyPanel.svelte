<script lang="ts">
  /**
   * 本人のパスキーの管理（Issue #1068。Core の仕様書 3.10.3・3.10.6。Core#222 で確定）。
   *
   * 一覧・登録（名前を付ける）・名前の変更・削除。**ログイン中の本人の分だけ**で、
   * 他人の分を見る・登録する口は無い（サーバも 404）。
   * **登録・削除は、要求ごとに再認証の証拠を添える**（時間窓は 0。401 でやり直す方式ではない）。
   * 操作を始めたら、パスワードかパスキーで確認する欄を出し、確認できた 1 回の要求だけに添える。
   * パスワードを持たない人（SAML・Pleasanter のみ）は、パスキーで確認する。
   * 画面は「再認証済み」を覚えず、判断もしない（古い cookie・画面操作時刻を証拠にしない。判断はサーバ）。
   * ⚠️ **一覧に資格情報の ID・公開鍵は出ない**（サーバも返さない）。名前にも使わない。
   * ⚠️ **PIN・生体情報は、ブラウザーと端末の画面で扱う。ここに入力欄は作らない。**
   */
  import { passkeyApi } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import {
    failureMessage,
    formatPasskeyDate,
    isLastPasskey,
    normalizePasskeyName,
    PASSKEY_NAME_MAX_LENGTH,
  } from '../lib/passkeys';
  import type { PasskeyRow } from '../lib/types';
  import {
    errorCodeOf,
    getPasskeyAssertion,
    passkeySupport,
    registerPasskey,
    type PasskeyFailure,
    type Reauthentication,
  } from '../lib/webauthn';
  import { t } from '../lib/i18n/state.svelte';

  const support = passkeySupport(window);

  let rows = $state<PasskeyRow[]>([]);
  let loading = $state(true);
  let loadError = $state('');

  let newName = $state('');
  let busy = $state(false);
  let error = $state('');
  let done = $state('');

  /** 名前を変えている行の ID と、入力中の名前。 */
  let editingId = $state<string | null>(null);
  let editingName = $state('');

  /** 再認証の確認を待っている操作。 */
  let pending = $state<
    { kind: 'register'; name: string } | { kind: 'remove'; row: PasskeyRow } | null
  >(null);
  let reauthPassword = $state('');

  /** 確認の欄を開いたままにする失敗（証拠をやり直せば通る）。それ以外は操作ごとやめる。 */
  const RETRYABLE_CODES = new Set([
    'passkeys.reauthRequired',
    'users.currentPasswordRejected',
    'users.operationTemporarilyLocked',
  ]);

  $effect(() => {
    void load();
  });

  async function load() {
    loading = true;
    const result = await passkeyApi.list();
    loading = false;
    if (!result.ok) {
      loadError = result.message || t('passkey.loadFailed');
      return;
    }

    loadError = '';
    rows = result.value.passkeys;
  }

  function showFailure(failure: PasskeyFailure) {
    const message = failureMessage(failure, 'manage');
    if ('serverMessage' in message) {
      error = message.serverMessage;
      return;
    }
    error = t(message.key, message.max === undefined ? undefined : { max: message.max });
  }

  /** HTTP の失敗を、失敗の種類へ。 */
  function serverFailureOf(result: { status: number; message: string; body?: unknown }): PasskeyFailure {
    return {
      kind: 'server',
      status: result.status,
      code: errorCodeOf(result.body),
      message: result.message,
    };
  }

  /** 失敗のあと、確認の欄を残すか閉じるか。 */
  function settleFailure(failure: PasskeyFailure) {
    showFailure(failure);
    const retry =
      failure.kind === 'server' &&
      (RETRYABLE_CODES.has(failure.code) || failure.status === 429 || failure.status === 423);
    if (!retry) {
      pending = null;
    }
  }

  /** 登録を始める。名前を確かめたら、確認の欄を出す（証拠はまだ送らない）。 */
  function startRegister(event: SubmitEvent) {
    event.preventDefault();
    error = '';
    done = '';

    const name = normalizePasskeyName(newName);
    if (name === null) {
      error = t('passkey.nameInvalid', { max: PASSKEY_NAME_MAX_LENGTH });
      return;
    }

    pending = { kind: 'register', name };
  }

  function startRename(row: PasskeyRow) {
    editingId = row.id;
    editingName = row.name;
    error = '';
    done = '';
  }

  async function saveRename(event: SubmitEvent, row: PasskeyRow) {
    event.preventDefault();
    error = '';

    const name = normalizePasskeyName(editingName);
    if (name === null) {
      error = t('passkey.nameInvalid', { max: PASSKEY_NAME_MAX_LENGTH });
      return;
    }

    busy = true;
    const result = await passkeyApi.rename(row.id, name);
    busy = false;

    if (!result.ok) {
      showFailure(serverFailureOf(result));
      return;
    }

    editingId = null;
    done = t('passkey.renamed');
    await load();
  }

  /** 削除を始める。対象名の確認のあと、再認証の欄を出す。 */
  async function startRemove(row: PasskeyRow) {
    error = '';
    done = '';

    const last = isLastPasskey(rows, row.id);
    const ok = await confirmAction({
      title: t('passkey.remove'),
      message: t(last ? 'passkey.confirmRemoveLast' : 'passkey.confirmRemove', { name: row.name }),
      confirmLabel: t('passkey.removeConfirmLabel'),
      danger: true,
      // 取り返しのつかない操作なので、初期の focus はキャンセル
      focusCancel: true,
    });
    if (ok) {
      pending = { kind: 'remove', row };
    }
  }

  /** 証拠を添えて、止まっていた操作を 1 回だけ行う。 */
  async function proceed(proof: Reauthentication) {
    const current = pending;
    if (current === null) {
      return;
    }

    error = '';
    busy = true;

    if (current.kind === 'register') {
      const outcome = await registerPasskey(passkeyApi, navigator.credentials, {
        name: current.name,
        proof,
      });
      busy = false;
      if (!outcome.ok) {
        settleFailure(outcome.failure);
        return;
      }

      newName = '';
      done = t('passkey.added');
    } else {
      const result = await passkeyApi.remove(current.row.id, proof);
      busy = false;
      if (!result.ok) {
        settleFailure(serverFailureOf(result));
        return;
      }

      done = t('passkey.removed');
    }

    pending = null;
    await load();
  }

  /** パスワードで確認する。**送ったら消す。** */
  async function confirmWithPassword(event: SubmitEvent) {
    event.preventDefault();
    if (reauthPassword === '') {
      return;
    }

    const password = reauthPassword;
    reauthPassword = '';
    await proceed({ password });
  }

  /** パスキーで確認する（パスワードを持たない人の道）。 */
  async function confirmWithPasskey() {
    error = '';
    busy = true;
    const assertion = await getPasskeyAssertion(passkeyApi, navigator.credentials);
    busy = false;
    if (!assertion.ok) {
      // 取り消しなどでは確認の欄を残す（やり直せる）
      showFailure(assertion.failure);
      return;
    }

    await proceed({ assertion: assertion.value });
  }

  function cancelPending() {
    pending = null;
    reauthPassword = '';
  }
</script>

<div class="card">
  <h2>{t('passkey.title')}</h2>
  <p class="hint">{t('passkey.lead')}</p>

  {#if loading}
    <p class="hint" role="status">{t('passkey.loading')}</p>
  {:else if loadError}
    <p class="error" role="alert">{loadError}</p>
  {:else if rows.length === 0}
    <p class="hint">{t('passkey.empty')}</p>
  {:else}
    <ul class="list">
      {#each rows as row (row.id)}
        <li>
          {#if editingId === row.id}
            <form class="rename" onsubmit={(event) => saveRename(event, row)}>
              <label>
                {t('passkey.renameLabel', { name: row.name })}
                <input type="text" bind:value={editingName} maxlength={PASSKEY_NAME_MAX_LENGTH} required />
              </label>
              <div class="row">
                <button type="submit" disabled={busy}>{t('passkey.save')}</button>
                <button type="button" class="link" onclick={() => (editingId = null)}>
                  {t('list.cancel')}
                </button>
              </div>
            </form>
          {:else}
            <div class="item">
              <div>
                <strong>{row.name}</strong>
                {#if row.backedUp}<span class="badge">{t('passkey.synced')}</span>{/if}
                <div class="meta">
                  {#if formatPasskeyDate(row.createdAt)}
                    {t('passkey.registeredAt')}: {formatPasskeyDate(row.createdAt)}
                  {/if}
                  <!-- 最終利用日は、取得できる場合だけ -->
                  {#if formatPasskeyDate(row.lastUsedAt)}
                    / {t('passkey.lastUsedAt')}: {formatPasskeyDate(row.lastUsedAt)}
                  {/if}
                </div>
              </div>
              <div class="row">
                <button type="button" class="link" disabled={busy} onclick={() => startRename(row)}>
                  {t('passkey.rename')}
                </button>
                <button type="button" class="link danger" disabled={busy} onclick={() => startRemove(row)}>
                  {t('passkey.remove')}
                </button>
              </div>
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}

  {#if pending !== null}
    <!-- **操作ごとに再認証の証拠を添える**（時間窓は 0）。画面は再認証済みかを覚えない -->
    <form class="reauth" onsubmit={confirmWithPassword}>
      <p class="hint">{t('passkey.reauthLead')}</p>
      <label>
        {t('passkey.reauthPassword')}
        <input type="password" autocomplete="current-password" bind:value={reauthPassword} />
      </label>
      <div class="row">
        <button type="submit" disabled={busy || reauthPassword === ''}>
          {t('passkey.reauthContinue')}
        </button>
        {#if support === 'ok'}
          <button type="button" class="secondary" disabled={busy} onclick={confirmWithPasskey}>
            {t('passkey.reauthWithPasskey')}
          </button>
        {/if}
        <button type="button" class="link" onclick={cancelPending}>{t('passkey.reauthCancel')}</button>
      </div>
      {#if support === 'ok'}<p class="hint">{t('passkey.reauthPasskeyHint')}</p>{/if}
    </form>
  {/if}

  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if done}<p class="done" role="status">{done}</p>{/if}

  {#if support === 'ok'}
    <form class="add" onsubmit={startRegister}>
      <label>
        {t('passkey.nameLabel')}
        <input
          type="text"
          bind:value={newName}
          maxlength={PASSKEY_NAME_MAX_LENGTH}
          autocomplete="off"
          required
        />
      </label>
      <p class="hint">{t('passkey.nameHint', { max: PASSKEY_NAME_MAX_LENGTH })}</p>

      {#if busy && pending?.kind === 'register'}
        <p class="hint" role="status">{t('passkey.addWaiting')}</p>
      {/if}

      <button type="submit" disabled={busy || pending !== null}>
        {busy && pending?.kind === 'register' ? t('passkey.adding') : t('passkey.add')}
      </button>
    </form>
  {:else}
    <!-- 非対応のブラウザでは、動かない登録のボタンを残さない -->
    <p class="hint" role="status">{t('passkey.unsupported')}</p>
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

  .card form {
    max-width: 28rem;
  }

  .list {
    list-style: none;
    padding: 0;
    margin: 0 0 var(--space-4);
  }

  .list li {
    padding: var(--space-3) 0;
    border-top: 1px solid var(--border);
  }

  .item {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: center;
    gap: var(--space-3);
  }

  .meta {
    color: var(--muted);
    font-size: 0.8rem;
  }

  .badge {
    margin-left: var(--space-2);
    padding: 0 var(--space-2);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    color: var(--muted);
    font-size: 0.75rem;
  }

  label {
    display: block;
    margin-bottom: var(--space-3);
    font-size: 0.9rem;
  }

  input {
    display: block;
    width: 100%;
    margin-top: var(--space-1);
  }

  .row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--space-4);
  }

  .reauth {
    margin: 0 0 var(--space-4);
  }

  .hint {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 var(--space-3);
  }

  button[type='submit'] {
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

  .secondary {
    padding: var(--space-2) 1.25rem;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--surface);
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  .secondary:disabled {
    cursor: default;
    opacity: 0.6;
  }

  .link {
    min-height: 44px;
    background: none;
    border: 0;
    padding: 0 var(--space-1);
    color: var(--accent);
    font: inherit;
    cursor: pointer;
    text-decoration: underline;
  }

  .link.danger {
    color: var(--error);
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }

  .done {
    color: var(--success);
    font-size: 0.9rem;
  }
</style>
