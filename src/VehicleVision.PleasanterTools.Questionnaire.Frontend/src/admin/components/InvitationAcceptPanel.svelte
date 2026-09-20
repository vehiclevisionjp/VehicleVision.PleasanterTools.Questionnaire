<script lang="ts">
  import { acceptInvitation, getAdminCaptchaChallenge } from '../lib/api';
  import type { AdminSession } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';
  import { solveAltcha } from '../../lib/altcha';

  const MINIMUM_PASSWORD_LENGTH = 12;

  interface Props {
    session: AdminSession;
    onadvance: () => void;
  }

  let { session, onadvance }: Props = $props();

  let password = $state('');
  let confirmation = $state('');
  let error = $state('');
  let busy = $state(false);

  const token = new URLSearchParams(window.location.search).get('token') ?? '';

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    error = '';

    if (password.length < MINIMUM_PASSWORD_LENGTH) {
      error = t('signIn.passwordTooShort', { minimum: MINIMUM_PASSWORD_LENGTH });
      return;
    }
    if (password !== confirmation) {
      error = t('signIn.passwordMismatch');
      return;
    }

    busy = true;
    let altcha: string | undefined;
    if (session.captchaEnabled) {
      const challenge = await getAdminCaptchaChallenge();
      if (!challenge.ok) {
        busy = false;
        error = challenge.message;
        return;
      }

      altcha = (await solveAltcha(challenge.value)) ?? undefined;
      if (altcha === undefined) {
        busy = false;
        error = t('signIn.captchaFailed');
        return;
      }
    }

    const result = await acceptInvitation(token, password, altcha);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    password = '';
    confirmation = '';
    history.replaceState(null, '', '/admin');
    onadvance();
  }
</script>

<div class="panel">
  <h1>{t('invitation.title')}</h1>
  <p class="lead">{t('invitation.lead')}</p>

  <form onsubmit={submit}>
    <label>
      {t('signIn.password')}
      <input type="password" autocomplete="new-password" bind:value={password} required />
    </label>

    <label>
      {t('signIn.passwordConfirmation')}
      <input type="password" autocomplete="new-password" bind:value={confirmation} required />
    </label>
    <p class="hint">{t('signIn.passwordHint', { minimum: MINIMUM_PASSWORD_LENGTH })}</p>

    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <button type="submit" disabled={busy}>
      {busy ? t('invitation.accepting') : t('invitation.accept')}
    </button>
  </form>
</div>

<style lang="scss">
  .panel {
    max-width: 24rem;
    margin: 4rem auto;
    padding: 2rem;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  h1 {
    font-size: 1.25rem;
    margin: 0 0 1rem;
  }

  .lead,
  .hint {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .lead {
    margin: 0 0 1.25rem;
  }

  .hint {
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

  button {
    width: 100%;
    margin-top: 0.5rem;
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }
</style>
