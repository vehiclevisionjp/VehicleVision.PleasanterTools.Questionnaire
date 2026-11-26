<script lang="ts">
  import {
    getAdminSignInSettings,
    getSamlSettings,
    saveAdminSignInSettings,
    saveSamlSettings,
    testSamlMetadata,
  } from '../lib/api';
  import type { AdminSignInSettings, SamlSettingField, SamlSettings } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    onback: () => void;
  }

  let { onback: _onback }: Props = $props();
  let settings = $state<SamlSettings>();
  let loading = $state(true);
  let busy = $state(false);
  let error = $state('');
  let done = $state('');
  let metadataUrl = $state('');
  let testMessage = $state('');
  let testError = $state('');

  // ---- ログイン画面の方式（Issue #1056） -------------------------------------
  // **SAML の保存とは別の API・別の保存。** 外部設定で固定されていれば編集できない

  let signIn = $state<AdminSignInSettings>();
  /** 「ID/パスワードを隠す」のチェック（真なら隠す）。保存するまでサーバの値は変わらない。 */
  let hidePassword = $state(false);
  let signInBusy = $state(false);
  let signInError = $state('');
  let signInDone = $state('');

  $effect(() => {
    void load();
    void loadSignIn();
  });

  async function loadSignIn() {
    const result = await getAdminSignInSettings();
    if (!result.ok) {
      signInError = result.message;
      return;
    }
    signIn = result.value;
    hidePassword = !result.value.passwordSignInEnabled;
  }

  /** 隠せない理由（保存前の案内）。**サーバも保存時に同じ検査をする。** */
  const hideBlockedReason = $derived.by(() => {
    if (!signIn || signIn.fixedByConfiguration) {return '';}
    if (!signIn.ssoEnabled) {return t('saml.signInNeedsSso');}
    if (!signIn.rescueTokenConfigured) {return t('saml.signInNeedsRescue');}
    return '';
  });

  async function saveSignIn(event: SubmitEvent) {
    event.preventDefault();
    signInError = '';
    signInDone = '';
    signInBusy = true;
    const result = await saveAdminSignInSettings(!hidePassword);
    signInBusy = false;

    if (!result.ok) {
      const code =
        typeof result.body === 'object' && result.body !== null && 'code' in result.body
          ? String((result.body as { code: unknown }).code)
          : '';
      signInError =
        code === 'fixed'
          ? t('saml.signInError.fixed')
          : code === 'sso-disabled'
            ? t('saml.signInError.ssoDisabled')
            : code === 'rescue-token-missing'
              ? t('saml.signInError.rescueTokenMissing')
              : t('saml.signInError.failed');
      return;
    }

    signIn = result.value;
    hidePassword = !result.value.passwordSignInEnabled;
    signInDone = t('saml.signInSaved');
  }

  async function load() {
    const result = await getSamlSettings();
    loading = false;
    if (!result.ok) {
      error = result.message;
      return;
    }
    settings = result.value;
  }

  function fixed(field: SamlSettingField): boolean {
    return settings?.fixedFields[field] ?? false;
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!settings) {return;}

    error = '';
    done = '';
    busy = true;
    const result = await saveSamlSettings(settings);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    settings = result.value;
    done = t('saml.saved');
  }

  async function testMetadata() {
    testError = '';
    testMessage = '';
    busy = true;
    const result = await testSamlMetadata(metadataUrl);
    busy = false;

    if (!result.ok) {
      const code =
        typeof result.body === 'object' && result.body !== null && 'code' in result.body
          ? String((result.body as { code: unknown }).code)
          : '';
      testError =
        code === 'invalid-url'
          ? t('saml.testInvalidUrl')
          : code === 'not-metadata'
            ? t('saml.testNotMetadata')
            : t('saml.testFailed');
      return;
    }

    testMessage = result.value.entityId
      ? t('saml.testSucceededWithEntityId', { entityId: result.value.entityId })
      : t('saml.testSucceeded');
  }
</script>

<section>
  <header class="head">
    <h1>{t('saml.title')}</h1>
  </header>

  {#if loading}
    <p>{t('app.loading')}</p>
  {:else if !settings}
    <p class="error" role="alert">{error || t('app.loadFailed')}</p>
  {:else}
    <aside class="warning" role="alert">
      <strong>{t('saml.passwordFallbackTitle')}</strong>
      <span>{t('saml.passwordFallback')}</span>
    </aside>

    <p class="lead">{t('saml.lead')}</p>

    <form onsubmit={save}>
      <label class="check">
        <input type="checkbox" bind:checked={settings.enabled} disabled={fixed('enabled')} />
        {t('saml.enabled')}
        {#if fixed('enabled')}<span class="fixed">{t('saml.fixed')}</span>{/if}
      </label>

      <label>
        {t('saml.entityId')}
        {#if fixed('entityId')}<span class="fixed">{t('saml.fixed')}</span>{/if}
        <input type="text" bind:value={settings.entityId} disabled={fixed('entityId')} />
      </label>

      <label>
        {t('saml.idpEntityId')}
        {#if fixed('idpEntityId')}<span class="fixed">{t('saml.fixed')}</span>{/if}
        <input type="text" bind:value={settings.idpEntityId} disabled={fixed('idpEntityId')} />
      </label>

      <label>
        {t('saml.singleSignOnUrl')}
        {#if fixed('singleSignOnUrl')}<span class="fixed">{t('saml.fixed')}</span>{/if}
        <input
          type="url"
          bind:value={settings.singleSignOnUrl}
          disabled={fixed('singleSignOnUrl')}
        />
      </label>

      <label>
        {t('saml.idpCertificate')}
        {#if fixed('idpCertificate')}<span class="fixed">{t('saml.fixed')}</span>{/if}
        <textarea
          rows="8"
          bind:value={settings.idpCertificate}
          disabled={fixed('idpCertificate')}
        ></textarea>
        <span class="hint">{t('saml.idpCertificateHint')}</span>
      </label>

      <div class="row">
        <label>
          {t('saml.unknownUser')}
          {#if fixed('unknownUser')}<span class="fixed">{t('saml.fixed')}</span>{/if}
          <select bind:value={settings.unknownUser} disabled={fixed('unknownUser')}>
            <option value="Reject">{t('saml.unknownUserReject')}</option>
            <option value="Register">{t('saml.unknownUserRegister')}</option>
          </select>
        </label>

        <label>
          {t('saml.registerRole')}
          {#if fixed('registerRole')}<span class="fixed">{t('saml.fixed')}</span>{/if}
          <select bind:value={settings.registerRole} disabled={fixed('registerRole')}>
            <option value="Editor">{t('users.role.Editor')}</option>
            <option value="SurveyAdministrator">{t('users.role.SurveyAdministrator')}</option>
            <option value="UserAdministrator">{t('users.role.UserAdministrator')}</option>
            <option value="Auditor">{t('users.role.Auditor')}</option>
          </select>
        </label>
      </div>

      <div class="row">
        <label>
          {t('saml.loginIdSource')}
          {#if fixed('loginIdSource')}<span class="fixed">{t('saml.fixed')}</span>{/if}
          <select bind:value={settings.loginIdSource} disabled={fixed('loginIdSource')}>
            <option value="NameId">NameID</option>
            <option value="Claim">{t('saml.loginIdSourceClaim')}</option>
          </select>
        </label>

        <label>
          {t('saml.loginIdClaim')}
          {#if fixed('loginIdClaim')}<span class="fixed">{t('saml.fixed')}</span>{/if}
          <input
            type="text"
            bind:value={settings.loginIdClaim}
            disabled={fixed('loginIdClaim') || settings.loginIdSource !== 'Claim'}
          />
        </label>
      </div>

      <label>
        {t('saml.singleLogoutUrl')}
        {#if fixed('singleLogoutUrl')}<span class="fixed">{t('saml.fixed')}</span>{/if}
        <input
          type="url"
          bind:value={settings.singleLogoutUrl}
          disabled={fixed('singleLogoutUrl')}
        />
        <span class="hint">{t('saml.singleLogoutUrlHint')}</span>
      </label>

      {#if error}<p class="error" role="alert">{error}</p>{/if}
      {#if done}<p class="done" role="status">{done}</p>{/if}
      <button type="submit" disabled={busy}>{busy ? t('saml.saving') : t('saml.save')}</button>
    </form>

    <form class="signin" onsubmit={saveSignIn}>
      <h2>{t('saml.signInTitle')}</h2>
      {#if signIn}
        <label class="check">
          <input
            type="checkbox"
            bind:checked={hidePassword}
            disabled={signIn.fixedByConfiguration ||
              (hideBlockedReason !== '' && !hidePassword)}
          />
          {t('saml.hidePassword')}
          {#if signIn.fixedByConfiguration}<span class="fixed">{t('saml.fixed')}</span>{/if}
        </label>
        <p class="hint">{t('saml.hidePasswordHint')}</p>
        {#if hideBlockedReason}<p class="hint" role="status">{hideBlockedReason}</p>{/if}
        {#if signInError}<p class="error" role="alert">{signInError}</p>{/if}
        {#if signInDone}<p class="done" role="status">{signInDone}</p>{/if}
        <button type="submit" disabled={signInBusy || signIn.fixedByConfiguration}>
          {signInBusy ? t('saml.saving') : t('saml.signInSave')}
        </button>
      {:else if signInError}
        <p class="error" role="alert">{signInError}</p>
      {:else}
        <p>{t('app.loading')}</p>
      {/if}
    </form>

    <div class="test">
      <h2>{t('saml.testTitle')}</h2>
      <p class="hint">{t('saml.testLead')}</p>
      <label>
        {t('saml.metadataUrl')}
        <input type="url" bind:value={metadataUrl} />
      </label>
      {#if testError}<p class="error" role="alert">{testError}</p>{/if}
      {#if testMessage}<p class="done" role="status">{testMessage}</p>{/if}
      <button type="button" class="secondary" disabled={busy || metadataUrl.trim() === ''} onclick={testMetadata}>
        {t('saml.test')}
      </button>
    </div>
  {/if}
</section>

<style lang="scss">
  /*
    ⚠️ **ページの幅は呼び出し側が決める**（Issue #436）。
    ここで 52rem を持っていたため、**ほかの画面と端がずれていた。**
    1 行の入力欄が長くなりすぎる分は、下の form 側で絞る。
  */
  section {
    margin: 0 auto;
  }

  form,
  .test {
    max-width: 52rem;
  }
  .signin {
    margin-top: var(--space-6);
  }
  .head {
    display: flex;
    align-items: center;
    gap: var(--space-4);
  }
  .lead,
  .hint {
    color: var(--muted);
  }
  form,
  .test {
    display: grid;
    gap: var(--space-4);
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    background: var(--surface);
  }
  .test {
    margin-top: var(--space-6);
  }
  label {
    display: grid;
    gap: 0.35rem;
    font-weight: 600;
  }
  .check {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  /*
    ⚠️ **チェックボックスを width: 100% の対象から外す**（Issue #416）。
    入れていたため**チェックボックスが横いっぱいに伸び、ラベルが右端へ押し出されていた。**
    枠と余白の指定も、四角い箱には合わない
  */
  .check input[type='checkbox'] {
    flex: none;
    width: auto;
    margin: 0;
    padding: 0;
    border: 0;
  }

  input:not([type='checkbox']),
  select,
  textarea {
    width: 100%;
  }
  textarea {
    font-family: var(--font-mono);
  }
  .row {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: var(--space-4);
  }
  .fixed {
    color: var(--warning-text);
    font-size: 0.85rem;
    font-weight: 400;
  }
  .warning {
    display: grid;
    gap: var(--space-1);
    padding: 0.9rem var(--space-4);
    border: 1px solid var(--warning-border);
    border-radius: var(--radius-md);
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
