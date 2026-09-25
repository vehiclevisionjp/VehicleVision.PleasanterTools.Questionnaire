<script lang="ts">
  import {
    getPleasanterSsoSettings,
    savePleasanterSsoSettings,
    testPleasanterSsoSettings,
  } from '../lib/api';
  import type { PleasanterSsoSettingField, PleasanterSsoSettings } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  /**
   * Pleasanter のログインで入る設定（Issue #464）。
   *
   * **SAML 設定（`SamlSettingsPanel`）と同じ作り。** 外部設定で決まっている項目はロックする。
   * 秘密の値は無い（403 のときに使う API キーは、Pleasanter 接続設定のものをサーバだけが読む）。
   */
  interface Props {
    onback: () => void;
  }

  let { onback: _onback }: Props = $props();

  // ⚠️ **数値の欄も文字列のまま送る。** type="number" にすると数値で束ねられ、
  // サーバの文字列の項目へ入らず 400 になる。範囲はサーバが確かめる
  let settings = $state<PleasanterSsoSettings>();
  let loading = $state(true);
  let busy = $state(false);
  let error = $state('');
  let done = $state('');
  let testMessage = $state('');
  let testError = $state('');

  $effect(() => {
    void load();
  });

  async function load() {
    const result = await getPleasanterSsoSettings();
    loading = false;
    if (!result.ok) {
      error = result.message;
      return;
    }
    settings = result.value;
  }

  function fixed(field: PleasanterSsoSettingField): boolean {
    return settings?.fixedFields[field] ?? false;
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!settings) return;

    error = '';
    done = '';
    busy = true;
    const result = await savePleasanterSsoSettings(settings);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    settings = result.value;
    done = t('pleasanterSso.saved');
  }

  async function test() {
    if (!settings) return;

    testError = '';
    testMessage = '';
    busy = true;
    const result = await testPleasanterSsoSettings(settings);
    busy = false;

    if (!result.ok) {
      testError = result.message;
      return;
    }

    const value = result.value;
    if (value.status === 'Authenticated') {
      testMessage = t('pleasanterSso.testAuthenticated', {
        loginId: value.loginId ?? '',
        userId: value.userId ?? '',
        tenantId: value.tenantId ?? '',
      });
      if (value.registered === false) {
        testError = t('pleasanterSso.testNotRegistered');
      }
      return;
    }

    testError =
      value.status === 'Unauthenticated'
        ? t('pleasanterSso.testUnauthenticated')
        : t('pleasanterSso.testUpstreamError', { reason: value.reason ?? '' });
  }
</script>

<section>
  <header class="head">
    <h1>{t('pleasanterSso.title')}</h1>
  </header>

  {#if loading}
    <p>{t('app.loading')}</p>
  {:else if !settings}
    <p class="error" role="alert">{error || t('app.loadFailed')}</p>
  {:else}
    <aside class="warning" role="note">
      <strong>{t('pleasanterSso.prerequisitesTitle')}</strong>
      <span>{t('pleasanterSso.prerequisites')}</span>
    </aside>

    <p class="lead">{t('pleasanterSso.lead')}</p>

    <form onsubmit={save}>
      <label class="check">
        <input type="checkbox" bind:checked={settings.enabled} disabled={fixed('enabled')} />
        {t('pleasanterSso.enabled')}
        {#if fixed('enabled')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
      </label>

      <label>
        {t('pleasanterSso.internalBaseUrl')}
        {#if fixed('internalBaseUrl')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
        <input
          type="url"
          bind:value={settings.internalBaseUrl}
          disabled={fixed('internalBaseUrl')}
        />
        <span class="hint">{t('pleasanterSso.internalBaseUrlHint')}</span>
      </label>

      <label>
        {t('pleasanterSso.loginUrl')}
        {#if fixed('loginUrl')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
        <input type="text" bind:value={settings.loginUrl} disabled={fixed('loginUrl')} />
        <span class="hint">{t('pleasanterSso.loginUrlHint')}</span>
      </label>

      <label>
        {t('pleasanterSso.logoutUrl')}
        {#if fixed('logoutUrl')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
        <input type="text" bind:value={settings.logoutUrl} disabled={fixed('logoutUrl')} />
        <span class="hint">{t('pleasanterSso.logoutUrlHint')}</span>
      </label>

      <label>
        {t('pleasanterSso.cookieNames')}
        {#if fixed('cookieNames')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
        <input type="text" bind:value={settings.cookieNames} disabled={fixed('cookieNames')} />
        <span class="hint">{t('pleasanterSso.cookieNamesHint')}</span>
      </label>

      <div class="row">
        <label>
          {t('pleasanterSso.unknownUser')}
          {#if fixed('unknownUser')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
          <select bind:value={settings.unknownUser} disabled={fixed('unknownUser')}>
            <option value="Reject">{t('saml.unknownUserReject')}</option>
            <option value="Register">{t('saml.unknownUserRegister')}</option>
          </select>
        </label>

        <label>
          {t('pleasanterSso.registerRole')}
          {#if fixed('registerRole')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
          <select bind:value={settings.registerRole} disabled={fixed('registerRole')}>
            <option value="Editor">{t('users.role.Editor')}</option>
            <option value="SurveyAdministrator">{t('users.role.SurveyAdministrator')}</option>
            <option value="UserAdministrator">{t('users.role.UserAdministrator')}</option>
            <option value="Auditor">{t('users.role.Auditor')}</option>
            <option value="Administrator">{t('users.role.Administrator')}</option>
          </select>
        </label>
      </div>

      <div class="row">
        <label>
          {t('pleasanterSso.revalidateMinutes')}
          {#if fixed('revalidateMinutes')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
          <input
            type="text"
            inputmode="numeric"
            bind:value={settings.revalidateMinutes}
            disabled={fixed('revalidateMinutes')}
          />
          <span class="hint">{t('pleasanterSso.revalidateMinutesHint')}</span>
        </label>

        <label>
          {t('pleasanterSso.timeoutSeconds')}
          {#if fixed('timeoutSeconds')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
          <input
            type="text"
            inputmode="numeric"
            bind:value={settings.timeoutSeconds}
            disabled={fixed('timeoutSeconds')}
          />
        </label>
      </div>

      <label>
        {t('pleasanterSso.buttonLabel')}
        {#if fixed('buttonLabel')}<span class="fixed">{t('pleasanterSso.fixed')}</span>{/if}
        <input type="text" bind:value={settings.buttonLabel} disabled={fixed('buttonLabel')} />
      </label>

      {#if error}<p class="error" role="alert">{error}</p>{/if}
      {#if done}<p class="done" role="status">{done}</p>{/if}
      <button type="submit" disabled={busy}>
        {busy ? t('pleasanterSso.saving') : t('pleasanterSso.save')}
      </button>
    </form>

    <div class="test">
      <h2>{t('pleasanterSso.testTitle')}</h2>
      <p class="hint">{t('pleasanterSso.testLead')}</p>
      {#if testError}<p class="error" role="alert">{testError}</p>{/if}
      {#if testMessage}<p class="done" role="status">{testMessage}</p>{/if}
      <button type="button" class="secondary" disabled={busy} onclick={test}>
        {t('pleasanterSso.test')}
      </button>
    </div>
  {/if}
</section>

<style lang="scss">
  /* **SAML 設定と同じ見た目にそろえる**（SamlSettingsPanel.svelte） */
  section {
    margin: 0 auto;
  }

  form,
  .test {
    max-width: 52rem;
    display: grid;
    gap: 1rem;
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
  }
  .head {
    display: flex;
    align-items: center;
    gap: 1rem;
  }
  .lead,
  .hint {
    color: var(--muted);
  }
  .hint {
    font-weight: 400;
    font-size: 0.85rem;
  }
  .test {
    margin-top: 1.5rem;
  }
  label {
    display: grid;
    gap: 0.35rem;
    font-weight: 600;
  }
  .check {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }
  .check input[type='checkbox'] {
    flex: none;
    width: auto;
    margin: 0;
    padding: 0;
    border: 0;
  }
  input:not([type='checkbox']),
  select {
    box-sizing: border-box;
    width: 100%;
    padding: 0.55rem;
    border: 1px solid var(--border);
    border-radius: 5px;
    font: inherit;
  }
  input:disabled,
  select:disabled {
    background: var(--disabled-surface);
    color: var(--disabled-text);
  }
  .row {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 1rem;
  }
  .fixed {
    color: var(--warning-text);
    font-size: 0.85rem;
    font-weight: 400;
  }
  .warning {
    display: grid;
    gap: 0.25rem;
    max-width: 52rem;
    margin-bottom: 1rem;
    padding: 0.9rem 1rem;
    border: 1px solid var(--warning-border);
    border-radius: 6px;
    background: var(--warning-surface);
    color: var(--warning-text);
  }
  .error {
    color: var(--error);
  }
  .done {
    color: var(--success);
  }
  h2 {
    margin: 0;
  }
  @media (max-width: 42rem) {
    .row {
      grid-template-columns: 1fr;
    }
  }
</style>
