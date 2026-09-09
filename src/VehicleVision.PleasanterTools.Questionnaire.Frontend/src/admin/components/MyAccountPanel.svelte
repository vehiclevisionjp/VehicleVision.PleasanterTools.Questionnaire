<script lang="ts">
  /**
   * 自分のアカウント（Issue #156）。
   *
   * **パスワードの変更**と**2 要素の登録・解除**を、どちらも
   * **パスワードの再確認つき**で行う。離席で奪われた画面から
   * 保護を差し替えられては、乗っ取りが完成してしまう。
   *
   * ⚠️ **ログインのときの登録（`EnrollPanel`）とは別の流れ。**
   * あちらは途中状態（`Admin.Pending`）から、ここはログイン済み（`Admin.Reenroll`）から。
   * **部品を分けてある**（口も cookie も違い、混ぜると読めなくなる）。
   */
  import {
    beginOwnTotp,
    changeOwnPassword,
    completeOwnTotp,
    disableOwnTotp,
  } from '../lib/api';
  import { twoFactorPolicyLabel } from '../lib/adminUsers';
  import type { AdminSession } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    session: AdminSession;
    /** 状態を読み直してほしい（2 要素の有無が変わる）。 */
    onchanged: () => void;
    onback: () => void;
  }

  let { session, onchanged, onback }: Props = $props();

  /**
   * パスワードの最低の長さ。
   *
   * **サーバ側（`AdminPasswordPolicy`）と揃えること。**
   * ここで通してもサーバが断る。打ち直しを減らすためだけのもの。
   */
  const MINIMUM_PASSWORD_LENGTH = 12;

  let currentPassword = $state('');
  let newPassword = $state('');
  let confirmation = $state('');
  let passwordMessage = $state('');
  let passwordError = $state('');
  let passwordBusy = $state(false);

  /** 2 要素の登録の途中。**共有鍵はサーバ側の cookie が持つ。** */
  let secret = $state('');
  let uri = $state('');
  let code = $state('');
  let totpPassword = $state('');
  let recoveryCodes = $state<string[]>([]);
  let totpError = $state('');
  let totpBusy = $state(false);

  /** 2 要素の方針。**必須なら解除させない。** */
  const twoFactor = $derived(session.twoFactor ?? 'Optional');
  const hasTotp = $derived(session.hasTotp ?? false);

  async function submitPassword(event: SubmitEvent) {
    event.preventDefault();
    passwordError = '';
    passwordMessage = '';

    if (newPassword.length < MINIMUM_PASSWORD_LENGTH) {
      passwordError = t('account.passwordTooShort', { minimum: MINIMUM_PASSWORD_LENGTH });
      return;
    }

    if (newPassword !== confirmation) {
      passwordError = t('account.passwordMismatch');
      return;
    }

    passwordBusy = true;
    const result = await changeOwnPassword(currentPassword, newPassword);
    passwordBusy = false;

    if (!result.ok) {
      passwordError = result.message;
      return;
    }

    currentPassword = '';
    newPassword = '';
    confirmation = '';
    passwordMessage = t('account.passwordChanged');
  }

  async function begin(event: SubmitEvent) {
    event.preventDefault();
    totpError = '';
    recoveryCodes = [];

    totpBusy = true;
    const result = await beginOwnTotp(totpPassword);
    totpBusy = false;

    if (!result.ok) {
      totpError = result.message;
      return;
    }

    secret = result.value.secret;
    uri = result.value.uri;
    totpPassword = '';
  }

  async function complete(event: SubmitEvent) {
    event.preventDefault();
    totpError = '';

    totpBusy = true;
    const result = await completeOwnTotp(code);
    totpBusy = false;
    code = '';

    if (!result.ok) {
      totpError = result.message;
      return;
    }

    // ⚠️ **復旧コードを出せるのはこの時だけ**
    recoveryCodes = result.value.recoveryCodes;
    secret = '';
    uri = '';
    onchanged();
  }

  async function disable() {
    totpError = '';

    if (!confirm(t('account.confirmDisableTwoFactor'))) {
      return;
    }

    totpBusy = true;
    const result = await disableOwnTotp(totpPassword);
    totpBusy = false;

    if (!result.ok) {
      totpError = result.message;
      return;
    }

    totpPassword = '';
    onchanged();
  }
</script>

<section>
  <header class="head">
    <button type="button" class="link" onclick={onback}>{t('account.back')}</button>
    <h1>{t('account.title')}</h1>
  </header>

  <p class="who">{session.loginId}</p>

  <!-- ---- パスワード ------------------------------------------------------- -->
  <div class="card">
    <h2>{t('account.passwordTitle')}</h2>

    <form onsubmit={submitPassword}>
      <label>
        {t('account.currentPassword')}
        <input
          type="password"
          autocomplete="current-password"
          bind:value={currentPassword}
          required
        />
      </label>

      <label>
        {t('account.newPassword')}
        <input type="password" autocomplete="new-password" bind:value={newPassword} required />
      </label>

      <label>
        {t('account.newPasswordConfirmation')}
        <input type="password" autocomplete="new-password" bind:value={confirmation} required />
      </label>

      <p class="hint">{t('account.passwordHint', { minimum: MINIMUM_PASSWORD_LENGTH })}</p>

      {#if passwordError}<p class="error" role="alert">{passwordError}</p>{/if}
      {#if passwordMessage}<p class="done" role="status">{passwordMessage}</p>{/if}

      <button type="submit" disabled={passwordBusy}>
        {passwordBusy ? t('account.saving') : t('account.changePassword')}
      </button>
    </form>
  </div>

  <!-- ---- 2 要素認証 ------------------------------------------------------- -->
  <div class="card">
    <h2>{t('account.twoFactorTitle')}</h2>

    <p class="state">
      {hasTotp ? t('account.twoFactorOn') : t('account.twoFactorOff')}
      <span class="policy">{twoFactorPolicyLabel(twoFactor)}</span>
    </p>

    {#if twoFactor === 'Disabled' && !hasTotp}
      <!-- **無効にしている間は登録もさせない**（サーバ側でも断る） -->
      <p class="hint">{t('account.twoFactorDisabledNote')}</p>
    {:else if recoveryCodes.length > 0}
      <!-- ⚠️ **この画面を閉じると二度と出せない** -->
      <p class="done" role="status">{t('account.recoveryTitle')}</p>
      <ul class="codes">
        {#each recoveryCodes as recoveryCode (recoveryCode)}
          <li><code>{recoveryCode}</code></li>
        {/each}
      </ul>
      <p class="hint">{t('account.recoveryNote')}</p>
    {:else if secret !== ''}
      <p class="hint">{t('account.enrollLead')}</p>
      <p class="secret"><code>{secret}</code></p>
      <p class="hint"><a href={uri}>{t('account.openAuthenticator')}</a></p>

      <form onsubmit={complete}>
        <label>
          {t('account.code')}
          <input type="text" inputmode="numeric" autocomplete="one-time-code" bind:value={code} required />
        </label>

        {#if totpError}<p class="error" role="alert">{totpError}</p>{/if}

        <button type="submit" disabled={totpBusy}>
          {totpBusy ? t('account.saving') : t('account.completeEnroll')}
        </button>
      </form>
    {:else}
      <form onsubmit={begin}>
        <!-- **パスワードをもう一度求める。** 離席で奪われた画面から差し替えられないため -->
        <label>
          {t('account.confirmPassword')}
          <input
            type="password"
            autocomplete="current-password"
            bind:value={totpPassword}
            required
          />
        </label>

        {#if totpError}<p class="error" role="alert">{totpError}</p>{/if}

        <div class="row">
          <button type="submit" disabled={totpBusy}>
            {hasTotp ? t('account.reenroll') : t('account.enroll')}
          </button>

          {#if hasTotp && twoFactor !== 'Required'}
            <!-- ⚠️ **必須のときは出さない。** 設定を無視して保護を外させない -->
            <button type="button" class="link danger" disabled={totpBusy} onclick={disable}>
              {t('account.disableTwoFactor')}
            </button>
          {/if}
        </div>

        {#if hasTotp && twoFactor === 'Required'}
          <p class="hint">{t('account.cannotDisable')}</p>
        {/if}
      </form>
    {/if}
  </div>
</section>

<style lang="scss">
  .head {
    display: flex;
    align-items: baseline;
    gap: 1rem;
    margin-bottom: 0.25rem;
  }

  h1 {
    font-size: 1.15rem;
    margin: 0;
  }

  h2 {
    font-size: 1rem;
    margin: 0 0 0.75rem;
  }

  .who {
    color: var(--muted);
    font-size: 0.9rem;
    margin: 0 0 1.25rem;
  }

  .card {
    max-width: 28rem;
    padding: 1.25rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 6px;
    margin-bottom: 1.25rem;
  }

  label {
    display: block;
    margin-bottom: 0.75rem;
    font-size: 0.9rem;
  }

  input {
    display: block;
    width: 100%;
    margin-top: 0.25rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    box-sizing: border-box;
  }

  .row {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  .state {
    margin: 0 0 0.75rem;
    font-size: 0.9rem;
  }

  .policy {
    color: var(--muted);
    font-size: 0.8rem;
    margin-left: 0.5rem;
  }

  .hint {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 0.75rem;
  }

  .secret code {
    display: inline-block;
    padding: 0.4rem 0.6rem;
    background: #f2f4f7;
    border-radius: 4px;
    /* **写し間違いを減らす。** 桁が揃う書体で出す */
    font-family: var(--font-mono, monospace);
    letter-spacing: 0.08em;
    word-break: break-all;
  }

  .codes {
    list-style: none;
    padding: 0;
    margin: 0 0 0.75rem;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(9rem, 1fr));
    gap: 0.35rem;
  }

  .codes code {
    font-family: var(--font-mono, monospace);
  }

  button[type='submit'] {
    padding: 0.5rem 1.25rem;
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

  .link.danger {
    color: var(--error);
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }

  .done {
    color: #027a48;
    font-size: 0.9rem;
  }
</style>
