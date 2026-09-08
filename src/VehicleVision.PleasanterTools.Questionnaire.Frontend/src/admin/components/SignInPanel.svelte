<script lang="ts">
  import { untrack } from 'svelte';
  import {
    login,
    setupFirstAdministrator,
    verifyRecoveryCode,
    verifyTotp,
  } from '../lib/api';
  import type { AdminSession } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  /**
   * パスワードの最低の長さ。
   *
   * **サーバ側（`Web/Services/AdminPasswordPolicy.cs`）と揃えること。**
   * ここで通してもサーバが断る。画面側は打ち直しを減らすためだけのもの。
   */
  const MINIMUM_PASSWORD_LENGTH = 12;

  interface Props {
    session: AdminSession;
    /** 認証が進んだので状態を読み直してほしい。 */
    onadvance: () => void;
  }

  let { session, onadvance }: Props = $props();

  type Step = 'password' | 'totp' | 'recovery';

  // **途中状態から始めることがある。** パスワードだけ通してページを再読み込みした場合。
  // **最初の 1 回だけ見る。** 以降は画面の側が持ち主になる
  let step = $state<Step>(untrack(() => (session.pending === true ? 'totp' : 'password')));
  let loginId = $state(untrack(() => session.pendingLoginId ?? ''));
  let password = $state('');
  let confirmation = $state('');
  let code = $state('');
  let error = $state('');
  let busy = $state(false);

  /**
   * SAML の受け口が失敗したときの印（Issue #166）。
   *
   * **理由は URL の印だけで返ってくる。** サーバは詳しい理由をログへ残し、
   * 画面には決まった文言を出す。
   */
  const samlError = untrack(() => {
    const failure = new URLSearchParams(window.location.search).get('samlError');
    switch (failure) {
      case 'unknown-user':
        return t('signIn.samlError.unknownUser');
      case 'disabled':
        return t('signIn.samlError.disabled');
      case null:
        return '';
      default:
        return t('signIn.samlError.invalid');
    }
  });

  /**
   * SAML のログインへ送り出す。
   *
   * **fetch では始められない。** IdP へは画面ごと移る必要があるので、
   * 場所を書き換える。
   */
  function startSaml() {
    window.location.assign('/api/admin/saml/login?returnUrl=/admin');
  }

  const isSetup = $derived(session.setupRequired);

  async function submitPassword(event: SubmitEvent) {
    event.preventDefault();
    error = '';

    if (isSetup) {
      if (password.length < MINIMUM_PASSWORD_LENGTH) {
        error = t('signIn.passwordTooShort', { minimum: MINIMUM_PASSWORD_LENGTH });
        return;
      }
      if (password !== confirmation) {
        error = t('signIn.passwordMismatch');
        return;
      }
    }

    busy = true;
    const result = isSetup
      ? await setupFirstAdministrator(loginId, password)
      : await login(loginId, password);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    // **パスワードが通っただけ。** ここから 2 要素へ進む
    password = '';
    confirmation = '';

    // **自分で段階を進める。** 状態を読み直しても、この部品は作り直されないので
    // step は残ったままになる（パスワードの欄が出続ける）。
    // 2 要素の登録が要る場合は、親が別の画面へ差し替える
    if (result.value.next === 'totp') {
      step = 'totp';
    }

    onadvance();
  }

  async function submitCode(event: SubmitEvent) {
    event.preventDefault();
    error = '';
    busy = true;

    const result = step === 'totp' ? await verifyTotp(code) : await verifyRecoveryCode(code);
    busy = false;

    if (!result.ok) {
      error = result.message;
      code = '';
      return;
    }

    code = '';
    onadvance();
  }
</script>

<div class="panel">
  {#if step === 'password'}
    <h1>{isSetup ? t('signIn.setupTitle') : t('signIn.title')}</h1>

    {#if isSetup}
      <!-- **既定のパスワードを仕込まない。** 変え忘れた既定値が残らないようにしている -->
      <p class="lead">
        {t('signIn.setupLead')}
        <strong>{t('signIn.setupLeadStrong')}</strong>
      </p>
    {/if}

    <form onsubmit={submitPassword}>
      <label>
        {t('signIn.loginId')}
        <input type="text" autocomplete="username" bind:value={loginId} required />
      </label>

      <label>
        {t('signIn.password')}
        <input
          type="password"
          autocomplete={isSetup ? 'new-password' : 'current-password'}
          bind:value={password}
          required
        />
      </label>

      {#if isSetup}
        <label>
          {t('signIn.passwordConfirmation')}
          <input type="password" autocomplete="new-password" bind:value={confirmation} required />
        </label>
        <p class="hint">{t('signIn.passwordHint', { minimum: MINIMUM_PASSWORD_LENGTH })}</p>
      {/if}

      {#if error}<p class="error" role="alert">{error}</p>{/if}

      <button type="submit" disabled={busy}>
        {busy ? t('signIn.checking') : isSetup ? t('signIn.register') : t('signIn.next')}
      </button>
    </form>

    {#if session.samlEnabled && !isSetup}
      <!-- **最初の管理者を作る画面には出さない。** IdP から来た人を
           最初の管理者にすると、誰でも全権を取れる -->
      <div class="or"><span>{t('signIn.samlOr')}</span></div>

      {#if samlError}<p class="error" role="alert">{samlError}</p>{/if}

      <button type="button" class="saml" onclick={startSaml}>
        {session.samlLabel ?? t('signIn.samlButton')}
      </button>
    {/if}
  {:else}
    <h1>{step === 'totp' ? t('signIn.totpTitle') : t('signIn.recoveryTitle')}</h1>
    <p class="lead">
      {#if step === 'totp'}
        {t('signIn.totpLead')}
      {:else}
        {t('signIn.recoveryLead')}<strong>{t('signIn.recoveryLeadStrong')}</strong>
      {/if}
    </p>

    <form onsubmit={submitCode}>
      <label>
        {step === 'totp' ? t('signIn.totpLabel') : t('signIn.recoveryLabel')}
        <input
          type="text"
          inputmode={step === 'totp' ? 'numeric' : 'text'}
          autocomplete="one-time-code"
          bind:value={code}
          required
        />
      </label>

      {#if error}<p class="error" role="alert">{error}</p>{/if}

      <button type="submit" disabled={busy}>
        {busy ? t('signIn.checking') : t('signIn.signIn')}
      </button>
    </form>

    <button
      type="button"
      class="link"
      onclick={() => {
        step = step === 'totp' ? 'recovery' : 'totp';
        code = '';
        error = '';
      }}
    >
      {step === 'totp' ? t('signIn.useRecovery') : t('signIn.useTotp')}
    </button>
  {/if}
</div>

<style lang="scss">
  .panel {
    max-width: 24rem;
    margin: 4rem auto;
    padding: 2rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  h1 {
    font-size: 1.25rem;
    margin: 0 0 1rem;
  }

  .lead {
    color: var(--muted);
    font-size: 0.9rem;
    margin: 0 0 1.25rem;
  }

  .hint {
    color: var(--muted);
    font-size: 0.85rem;
    margin: -0.5rem 0 0.75rem;
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

  /* **区切りは線と文字で出す**（Issue #166） */
  .or {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: 1.25rem 0;
    color: var(--muted);
    font-size: 0.85rem;
  }

  .or::before,
  .or::after {
    content: '';
    flex: 1;
    border-top: 1px solid var(--border);
  }

  .saml {
    display: block;
    width: 100%;
    padding: 0.6rem;
    background: #fff;
    color: inherit;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    cursor: pointer;
  }

  .saml:hover {
    background: var(--surface, #f5f5f5);
  }

  button[type='submit'] {
    width: 100%;
    margin-top: 0.5rem;
  }

  .link {
    display: block;
    width: 100%;
    margin-top: 1rem;
    background: none;
    border: none;
    color: var(--accent);
    font: inherit;
    font-size: 0.85rem;
    cursor: pointer;
    text-decoration: underline;
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }
</style>
