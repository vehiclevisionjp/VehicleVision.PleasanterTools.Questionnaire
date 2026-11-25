<script lang="ts">
  /**
   * 本人のパスキーの管理（Issue #1068。Core の仕様書 3.10.3・3.10.6）。
   *
   * 一覧・登録（名前を付ける）・名前の変更・削除。**ログイン中の本人の分だけ**で、
   * 他人の分を見る・登録する口は無い（サーバも 404）。
   * **登録・削除は直近の再認証が要る。** 画面は、サーバが 401 を返したときにパスワードの確認を求めて
   * やり直すだけで、「再認証済み」を画面の側で判断しない（古い cookie・画面操作時刻を再認証の証拠にしない）。
   * ⚠️ **一覧に資格情報の ID・公開鍵は出ない**（サーバも返さない）。名前にも使わない。
   * ⚠️ **PIN・生体情報は、ブラウザーと端末の画面で扱う。ここに入力欄は作らない。**
   */
  import { passkeyApi } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import {
    failureMessage,
    formatPasskeyDate,
    isLastPasskey,
    needsReauthentication,
    normalizePasskeyName,
    PASSKEY_NAME_MAX_LENGTH,
  } from '../lib/passkeys';
  import type { PasskeyRow } from '../lib/types';
  import { passkeySupport, registerPasskey, type PasskeyFailure } from '../lib/webauthn';
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

  /** 再認証を求められて、やり直しを待っている操作。 */
  let pending = $state<{ kind: 'register' } | { kind: 'remove'; row: PasskeyRow } | null>(null);
  let reauthPassword = $state('');

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
    error = 'key' in message ? t(message.key) : message.serverMessage;
  }

  /** パスワードを送るのは、再認証を求められたあとの 1 回だけ。**送ったら消す。** */
  function takePassword(): string | undefined {
    const password = reauthPassword === '' ? undefined : reauthPassword;
    reauthPassword = '';
    return password;
  }

  async function register(event?: SubmitEvent) {
    event?.preventDefault();
    error = '';
    done = '';

    const name = normalizePasskeyName(newName);
    if (name === null) {
      error = t('passkey.nameInvalid', { max: PASSKEY_NAME_MAX_LENGTH });
      return;
    }

    busy = true;
    const outcome = await registerPasskey(passkeyApi, navigator.credentials, {
      name,
      password: takePassword(),
    });
    busy = false;

    if (!outcome.ok) {
      if (needsReauthentication(outcome.failure) && pending === null) {
        pending = { kind: 'register' };
        return;
      }

      showFailure(outcome.failure);
      return;
    }

    pending = null;
    newName = '';
    done = t('passkey.added');
    await load();
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
      error = result.message;
      return;
    }

    editingId = null;
    done = t('passkey.renamed');
    await load();
  }

  async function remove(row: PasskeyRow, confirmed = false) {
    error = '';
    done = '';

    if (!confirmed) {
      const last = isLastPasskey(rows, row.id);
      const ok = await confirmAction({
        title: t('passkey.remove'),
        message: t(last ? 'passkey.confirmRemoveLast' : 'passkey.confirmRemove', { name: row.name }),
        confirmLabel: t('passkey.removeConfirmLabel'),
        danger: true,
        // 取り返しのつかない操作なので、初期の focus はキャンセル
        focusCancel: true,
      });
      if (!ok) {
        return;
      }
    }

    busy = true;
    const result = await passkeyApi.remove(row.id, takePassword());
    busy = false;

    if (!result.ok) {
      if (result.status === 401 && pending === null) {
        pending = { kind: 'remove', row };
        return;
      }

      // 409（最後のログイン方法になる）などは、サーバの文言を出す
      error = result.message;
      return;
    }

    pending = null;
    done = t('passkey.removed');
    await load();
  }

  /** パスワードを添えて、止まっていた操作をやり直す。 */
  async function continuePending(event: SubmitEvent) {
    event.preventDefault();
    const current = pending;
    if (current === null || reauthPassword === '') {
      return;
    }

    // 2 回目の 401（パスワード違い）は、入力欄を出したまま、サーバの文言をエラーに出す
    if (current.kind === 'register') {
      await register();
    } else {
      await remove(current.row, true);
    }
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
                <button type="button" class="link danger" disabled={busy} onclick={() => remove(row)}>
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
    <!-- **直近の再認証が要るとサーバが返したとき**（登録・削除）。画面は再認証済みかを判断しない -->
    <form class="reauth" onsubmit={continuePending}>
      <p class="hint">{t('passkey.reauthLead')}</p>
      <label>
        {t('passkey.reauthPassword')}
        <input type="password" autocomplete="current-password" bind:value={reauthPassword} required />
      </label>
      <div class="row">
        <button type="submit" disabled={busy}>{t('passkey.reauthContinue')}</button>
        <button type="button" class="link" onclick={cancelPending}>{t('passkey.reauthCancel')}</button>
      </div>
    </form>
  {/if}

  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if done}<p class="done" role="status">{done}</p>{/if}

  {#if support === 'ok'}
    <form class="add" onsubmit={register}>
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

      {#if busy && pending === null}
        <p class="hint" role="status">{t('passkey.addWaiting')}</p>
      {/if}

      <button type="submit" disabled={busy}>
        {busy && pending === null ? t('passkey.adding') : t('passkey.add')}
      </button>
    </form>
  {:else}
    <!-- 非対応のブラウザでは、動かない登録の釦を残さない -->
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
