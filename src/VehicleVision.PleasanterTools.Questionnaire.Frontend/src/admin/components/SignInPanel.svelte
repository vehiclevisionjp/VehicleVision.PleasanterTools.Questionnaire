<script lang="ts">
  import { untrack } from 'svelte';
  import {
    login,
    setupFirstAdministrator,
    verifyRecoveryCode,
    verifyTotp,
  } from '../lib/api';
  import type { AdminSession } from '../lib/types';

  interface Props {
    session: AdminSession;
    /** 認証が進んだので状態を読み直してほしい。 */
    onadvance: () => void;
  }

  let { session, onadvance }: Props = $props();

  type Step = 'password' | 'totp' | 'recovery';

  // **途中状態から始めることがある。** 合言葉だけ通してページを再読み込みした場合。
  // **最初の 1 回だけ見る。** 以降は画面の側が持ち主になる
  let step = $state<Step>(untrack(() => (session.pending === true ? 'totp' : 'password')));
  let loginId = $state(untrack(() => session.pendingLoginId ?? ''));
  let password = $state('');
  let confirmation = $state('');
  let code = $state('');
  let error = $state('');
  let busy = $state(false);

  const isSetup = $derived(session.setupRequired);

  async function submitPassword(event: SubmitEvent) {
    event.preventDefault();
    error = '';

    if (isSetup) {
      if (password.length < 12) {
        error = '合言葉は 12 文字以上にしてください。';
        return;
      }
      if (password !== confirmation) {
        error = '確認用の合言葉が一致しません。';
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

    // **合言葉が通っただけ。** ここから 2 要素へ進む
    password = '';
    confirmation = '';
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
    <h1>{isSetup ? '最初の管理者を登録する' : '管理画面にログイン'}</h1>

    {#if isSetup}
      <!-- **既定の合言葉を仕込まない。** 変え忘れた既定値が残らないようにしている -->
      <p class="lead">
        まだ管理者が登録されていません。最初の 1 人を作ってください。
        <strong>この入口はここで一度きりです。</strong>
      </p>
    {/if}

    <form onsubmit={submitPassword}>
      <label>
        ログイン ID
        <input type="text" autocomplete="username" bind:value={loginId} required />
      </label>

      <label>
        合言葉
        <input
          type="password"
          autocomplete={isSetup ? 'new-password' : 'current-password'}
          bind:value={password}
          required
        />
      </label>

      {#if isSetup}
        <label>
          合言葉（確認）
          <input type="password" autocomplete="new-password" bind:value={confirmation} required />
        </label>
        <p class="hint">12 文字以上にしてください。</p>
      {/if}

      {#if error}<p class="error" role="alert">{error}</p>{/if}

      <button type="submit" disabled={busy}>
        {busy ? '確認しています…' : isSetup ? '登録する' : '次へ'}
      </button>
    </form>
  {:else}
    <h1>{step === 'totp' ? '認証アプリの数字を入力' : '復旧コードを入力'}</h1>
    <p class="lead">
      {#if step === 'totp'}
        認証アプリに表示されている 6 桁の数字を入力してください。
      {:else}
        登録時に控えた復旧コードを 1 つ入力してください。<strong>一度使うと無効になります。</strong>
      {/if}
    </p>

    <form onsubmit={submitCode}>
      <label>
        {step === 'totp' ? '6 桁の数字' : '復旧コード'}
        <input
          type="text"
          inputmode={step === 'totp' ? 'numeric' : 'text'}
          autocomplete="one-time-code"
          bind:value={code}
          required
        />
      </label>

      {#if error}<p class="error" role="alert">{error}</p>{/if}

      <button type="submit" disabled={busy}>{busy ? '確認しています…' : 'ログイン'}</button>
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
      {step === 'totp' ? '認証アプリが使えない（復旧コードを使う）' : '認証アプリを使う'}
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
