<script lang="ts">
  import QRCode from 'qrcode';
  import { beginEnrollment, completeEnrollment } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    /** 登録が終わったので状態を読み直してほしい。 */
    onadvance: () => void;
  }

  let { onadvance }: Props = $props();

  let secret = $state('');
  let uri = $state('');
  let qrDataUrl = $state('');
  let code = $state('');
  let recoveryCodes = $state<string[]>([]);
  let acknowledged = $state(false);
  let error = $state('');
  let busy = $state(false);

  /** 書き写しやすいように 4 文字ずつ区切る。 */
  const groupedSecret = $derived(secret.replace(/(.{4})/g, '$1 ').trim());

  $effect(() => {
    void start();
  });

  async function start() {
    const result = await beginEnrollment();
    if (!result.ok) {
      error = result.message;
      return;
    }

    secret = result.value.secret;
    uri = result.value.uri;

    try {
      // **読み取れない QR を出すより、出さない方がまし。**
      // 共有鍵は下に文字でも出しているので手で入れられる
      qrDataUrl = await QRCode.toDataURL(uri, { margin: 1, width: 220 });
    } catch {
      qrDataUrl = '';
    }
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    error = '';
    busy = true;

    const result = await completeEnrollment(code);
    busy = false;

    if (!result.ok) {
      error = result.message;
      code = '';
      return;
    }

    // **復旧コードを見せられるのはここだけ。** 保存しているのはハッシュのみ
    recoveryCodes = result.value.recoveryCodes;
  }

  function copyCodes() {
    void navigator.clipboard?.writeText(recoveryCodes.join('\n'));
  }
</script>

<div class="panel">
  {#if recoveryCodes.length > 0}
    <h1>{t('enroll.recoveryTitle')}</h1>
    <p class="lead">
      <strong>{t('enroll.recoveryLeadStrong')}</strong>
      {t('enroll.recoveryLead')}
    </p>

    <ul class="codes">
      {#each recoveryCodes as recoveryCode (recoveryCode)}
        <li>{recoveryCode}</li>
      {/each}
    </ul>

    <button type="button" class="secondary" onclick={copyCodes}>{t('enroll.copy')}</button>

    <label class="acknowledge">
      <input type="checkbox" bind:checked={acknowledged} />
      {t('enroll.acknowledged')}
    </label>

    <button type="button" disabled={!acknowledged} onclick={onadvance}>
      {t('enroll.proceed')}
    </button>
  {:else}
    <h1>{t('enroll.title')}</h1>
    <p class="lead">
      {t('enroll.lead')}<strong>{t('enroll.leadStrong')}</strong>
      {t('enroll.leadTail')}
    </p>

    {#if qrDataUrl}
      <img class="qr" src={qrDataUrl} alt={t('enroll.qrAlt')} />
    {/if}

    <p class="secret">
      {t('enroll.secretHint')}<br />
      <code>{groupedSecret}</code>
    </p>

    <form onsubmit={submit}>
      <label>
        {t('enroll.codeLabel')}
        <input type="text" inputmode="numeric" autocomplete="one-time-code" bind:value={code} required />
      </label>

      {#if error}<p class="error" role="alert">{error}</p>{/if}

      <!-- **一度打ってもらってから有効にする。** 読み取りに失敗していた場合、
           そのまま有効にすると本人が入れなくなる -->
      <button type="submit" disabled={busy || secret === ''}>
        {busy ? t('enroll.checking') : t('enroll.register')}
      </button>
    </form>
  {/if}
</div>

<style lang="scss">
  .panel {
    max-width: 26rem;
    margin: 4rem auto;
    padding: 2rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
    text-align: center;
  }

  h1 {
    font-size: 1.25rem;
    margin: 0 0 1rem;
  }

  .lead {
    color: var(--muted);
    font-size: 0.9rem;
    margin: 0 0 1.25rem;
    text-align: left;
  }

  .qr {
    display: block;
    margin: 0 auto 1rem;
    border: 1px solid var(--border);
    border-radius: 4px;
  }

  .secret {
    font-size: 0.85rem;
    color: var(--muted);

    code {
      display: inline-block;
      margin-top: 0.35rem;
      padding: 0.35rem 0.5rem;
      background: var(--bg);
      border-radius: 4px;
      letter-spacing: 0.08em;
      color: #101828;
      word-break: break-all;
    }
  }

  label {
    display: block;
    margin: 1rem 0 0.75rem;
    font-size: 0.9rem;
    text-align: left;
  }

  input[type='text'] {
    display: block;
    width: 100%;
    margin-top: 0.25rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    box-sizing: border-box;
  }

  .codes {
    list-style: none;
    margin: 0 0 1rem;
    padding: 1rem;
    background: var(--bg);
    border-radius: 4px;
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.35rem;
    font-family: var(--font-mono);
    letter-spacing: 0.05em;
  }

  .acknowledge {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    margin: 1rem 0;
    font-size: 0.9rem;
  }

  button[type='submit'],
  button:not(.secondary) {
    width: 100%;
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }
</style>
