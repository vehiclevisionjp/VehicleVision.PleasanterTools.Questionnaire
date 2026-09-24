<script lang="ts">
  import {
    getAppSettings,
    saveAppSettings,
    testAppSettings,
    testAppSettingsMail,
  } from '../lib/api';
  import type { AppSettingField, AppSettings } from '../lib/types';
  import { language, t } from '../lib/i18n/state.svelte';

  interface Props {
    onback: () => void;
  }

  let { onback: _onback }: Props = $props();
  let settings = $state<AppSettings>();
  let loading = $state(true);
  let busy = $state(false);
  let error = $state('');
  let done = $state('');
  let connectionDone = $state('');
  let initialPleasanterBaseUrl = $state('');

  const pleasanterBaseUrlKey = 'QUESTIONNAIRE_PLEASANTER_BASEURL';
  let initialValues = $state<Record<string, string | null>>({});

  const mailTransportKey = 'QUESTIONNAIRE_MAIL_TRANSPORT';
  const mailEnabledKey = 'QUESTIONNAIRE_MAIL_ENABLED';
  const mailBaseUrlKey = 'QUESTIONNAIRE_MAIL_BASEURL';

  $effect(() => {
    void load();
  });

  async function load() {
    const result = await getAppSettings();
    loading = false;
    if (!result.ok) {
      error = result.message;
      return;
    }
    settings = result.value;
    initialPleasanterBaseUrl =
      settings.fields.find((field) => field.key === pleasanterBaseUrlKey)?.value ?? '';
    initialValues = Object.fromEntries(
      result.value.fields.map((field) => [field.key, field.value]),
    );
  }

  function label(field: AppSettingField): string {
    return language() === 'ja' ? field.labelJa : field.labelEn;
  }

  function description(field: AppSettingField): string {
    return language() === 'ja' ? field.descriptionJa : field.descriptionEn;
  }

  function setBoolean(field: AppSettingField, checked: boolean) {
    field.value = checked ? 'true' : 'false';
  }

  function setValue(field: AppSettingField, value: string) {
    field.value = value;
    connectionDone = '';
  }

  function pleasanterDestinationChanged(): boolean {
    if (!settings || settings.publishedSurveyCount === 0) return false;
    const current =
      settings.fields.find((field) => field.key === pleasanterBaseUrlKey)?.value ?? '';
    return current.trim().replace(/\/+$/, '') !== initialPleasanterBaseUrl.trim().replace(/\/+$/, '');
  }

  async function testConnection() {
    if (!settings) return;

    error = '';
    done = '';
    connectionDone = '';
    busy = true;
    const result = await testAppSettings(settings);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    connectionDone = t('appSettings.connectionSucceeded');
  }

  function fieldValue(key: string): string {
    return settings?.fields.find((field) => field.key === key)?.value ?? '';
  }

  function showField(field: AppSettingField): boolean {
    if (field.key.startsWith('QUESTIONNAIRE_MAIL_SMTP_')) {
      return fieldValue(mailTransportKey) === 'Smtp';
    }
    if (field.key === 'QUESTIONNAIRE_MAIL_SES_REGION') {
      return fieldValue(mailTransportKey) === 'AmazonSes';
    }
    if (field.key === 'QUESTIONNAIRE_MAIL_ACS_ENDPOINT') {
      return fieldValue(mailTransportKey) === 'AzureCommunicationServices';
    }
    return true;
  }

  function choices(field: AppSettingField): string[] | undefined {
    if (field.key === mailTransportKey) {
      return ['Smtp', 'AmazonSes', 'AzureCommunicationServices'];
    }
    if (field.key === 'QUESTIONNAIRE_MAIL_SMTP_SECURITY') {
      return ['StartTls', 'ImplicitTls', 'None'];
    }
    return undefined;
  }

  function mailBaseUrlMissing(): boolean {
    return fieldValue(mailEnabledKey) === 'true' && fieldValue(mailBaseUrlKey).trim() === '';
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!settings) return;

    error = '';
    done = '';
    busy = true;
    const changed: AppSettings = {
      ...settings,
      fields: settings.fields.filter(
        (field) => !field.isFixed && field.value !== initialValues[field.key],
      ),
    };
    const result = await saveAppSettings(changed);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    settings = result.value;
    initialPleasanterBaseUrl =
      settings.fields.find((field) => field.key === pleasanterBaseUrlKey)?.value ?? '';
    initialValues = Object.fromEntries(
      result.value.fields.map((field) => [field.key, field.value]),
    );
    done = t('appSettings.saved');
  }

  async function testMail() {
    if (!settings) return;

    error = '';
    done = '';
    connectionDone = '';
    busy = true;
    const result = await testAppSettingsMail(settings);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    done = t('appSettings.testMailSent');
  }
</script>

<section>
  <header class="head">
    <h1>{t('appSettings.title')}</h1>
  </header>

  {#if loading}
    <p>{t('app.loading')}</p>
  {:else if !settings}
    <p class="error" role="alert">{error || t('app.loadFailed')}</p>
  {:else}
    <p class="lead">{t('appSettings.lead')}</p>

    <form onsubmit={save}>
      {#each settings.fields as field (field.key)}
        {#if showField(field)}
          <label class:check={field.type === 'boolean'}>
            {#if field.type === 'boolean'}
              <input
                type="checkbox"
                checked={field.value === 'true'}
                disabled={field.isFixed}
                onchange={(event) => setBoolean(field, event.currentTarget.checked)}
              />
            {/if}
            <span>
              {label(field)}
              {#if field.isFixed}<span class="fixed">{t('appSettings.fixed')}</span>{/if}
            </span>
            {#if field.type === 'string'}
              {#if choices(field) !== undefined}
                <select
                  value={field.value ?? ''}
                  disabled={field.isFixed}
                  onchange={(event) => setValue(field, event.currentTarget.value)}
                >
                  {#each choices(field) ?? [] as choice}
                    <option value={choice}>{choice}</option>
                  {/each}
                </select>
              {:else if field.isSecret}
                <input
                  type="password"
                  value={field.value ?? ''}
                  autocomplete="new-password"
                  disabled={field.isFixed}
                  oninput={(event) => setValue(field, event.currentTarget.value)}
                />
                <span class="hint">
                  {field.hasValue
                    ? t('appSettings.secretConfigured')
                    : t('appSettings.secretNotConfigured')}
                </span>
              {:else}
                <textarea
                  rows="4"
                  maxlength={field.maximumLength ?? undefined}
                  value={field.value ?? ''}
                  disabled={field.isFixed}
                  oninput={(event) => setValue(field, event.currentTarget.value)}
                ></textarea>
              {/if}
            {:else if field.type === 'integer'}
              <input
                type="number"
                min={field.minimum ?? undefined}
                max={field.maximum ?? undefined}
                value={field.value ?? ''}
                disabled={field.isFixed}
                oninput={(event) => setValue(field, event.currentTarget.value)}
              />
            {/if}
            <span class="hint">{description(field)}</span>
            <span class="hint">
              {t('appSettings.defaultValue')}: {field.defaultValue}
              {#if field.isDefault} ({t('appSettings.usingDefault')}){/if}
            </span>
            {#if field.showPreview && (field.value?.trim() ?? '') !== ''}
              <aside class="preview">{field.value}</aside>
            {/if}
          </label>
        {/if}
      {/each}

      {#if mailBaseUrlMissing()}
        <p class="warning" role="alert">{t('appSettings.mailBaseUrlRequired')}</p>
      {/if}
      {#if pleasanterDestinationChanged()}
        <p class="warning" role="alert">
          {t('appSettings.publishedDestinationWarning').replace(
            '{count}',
            String(settings.publishedSurveyCount),
          )}
        </p>
      {/if}
      {#if !settings.isPleasanterConfigured}
        <p class="warning" role="alert">{t('appSettings.pleasanterNotConfigured')}</p>
      {/if}
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      {#if done}<p class="done" role="status">{done}</p>{/if}
      {#if connectionDone}<p class="done" role="status">{connectionDone}</p>{/if}
      <div class="actions">
        <button type="button" disabled={busy} onclick={testConnection}>
          {busy ? t('appSettings.testingConnection') : t('appSettings.testConnection')}
        </button>
        <button type="button" disabled={busy} onclick={testMail}>
          {t('appSettings.testMail')}
        </button>
        <button
          type="submit"
          disabled={busy || mailBaseUrlMissing() || settings.fields.every((field) => field.isFixed)}
        >
          {busy ? t('appSettings.saving') : t('appSettings.save')}
        </button>
      </div>
    </form>
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

  form {
    max-width: 52rem;
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
  form {
    display: grid;
    gap: 1rem;
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
  }
  label {
    display: grid;
    gap: 0.35rem;
    font-weight: 600;
  }
  .check {
    grid-template-columns: auto 1fr;
    align-items: center;
  }
  .check .hint {
    grid-column: 2;
  }
  input[type='number'],
  input[type='password'],
  select,
  textarea {
    box-sizing: border-box;
    width: 100%;
    padding: 0.55rem;
    border: 1px solid var(--border);
    border-radius: 5px;
    font: inherit;
  }
  input:disabled,
  textarea:disabled {
    background: var(--disabled-surface);
    color: var(--disabled-text);
  }
  .fixed {
    margin-left: 0.5rem;
    color: var(--warning-text);
    font-size: 0.85rem;
    font-weight: 400;
  }
  .preview {
    white-space: pre-wrap;
    padding: 0.9rem 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    font-weight: 400;
  }
  .error {
    color: var(--error);
  }
  .warning {
    color: var(--warning-text);
    font-weight: 600;
  }
  .done {
    color: var(--success);
  }
  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
  }
</style>
