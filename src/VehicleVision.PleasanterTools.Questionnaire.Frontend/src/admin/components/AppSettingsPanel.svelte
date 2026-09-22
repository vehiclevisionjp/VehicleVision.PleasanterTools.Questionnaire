<script lang="ts">
  import { getAppSettings, saveAppSettings } from '../lib/api';
  import type { AppSettings } from '../lib/types';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    onback: () => void;
  }

  let { onback }: Props = $props();
  let settings = $state<AppSettings>();
  let loading = $state(true);
  let busy = $state(false);
  let error = $state('');
  let done = $state('');

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
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!settings) return;

    error = '';
    done = '';
    busy = true;
    const result = await saveAppSettings(settings);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    settings = result.value;
    done = t('appSettings.saved');
  }
</script>

<section>
  <header class="head">
    <button type="button" class="link" onclick={onback}>{t('appSettings.back')}</button>
    <h1>{t('appSettings.title')}</h1>
  </header>

  {#if loading}
    <p>{t('app.loading')}</p>
  {:else if !settings}
    <p class="error" role="alert">{error || t('app.loadFailed')}</p>
  {:else}
    <p class="lead">{t('appSettings.lead')}</p>

    <form onsubmit={save}>
      <label>
        {t('appSettings.adminNotice')}
        {#if settings.fixedFields.adminNotice}
          <span class="fixed">{t('appSettings.fixed')}</span>
        {/if}
        <textarea
          rows="4"
          maxlength="1000"
          bind:value={settings.adminNotice}
          disabled={settings.fixedFields.adminNotice}
        ></textarea>
        <span class="hint">{t('appSettings.adminNoticeHint')}</span>
      </label>

      {#if settings.adminNotice.trim() !== ''}
        <aside class="preview" aria-label={t('appSettings.preview')}>
          {settings.adminNotice}
        </aside>
      {/if}

      {#if error}<p class="error" role="alert">{error}</p>{/if}
      {#if done}<p class="done" role="status">{done}</p>{/if}
      <button type="submit" disabled={busy || settings.fixedFields.adminNotice}>
        {busy ? t('appSettings.saving') : t('appSettings.save')}
      </button>
    </form>
  {/if}
</section>

<style lang="scss">
  section {
    max-width: 52rem;
    margin: 0 auto;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 1rem;
  }
  .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--accent);
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
  textarea {
    box-sizing: border-box;
    width: 100%;
    padding: 0.55rem;
    border: 1px solid var(--border);
    border-radius: 5px;
    font: inherit;
  }
  textarea:disabled {
    background: var(--disabled-surface);
    color: var(--disabled-text);
  }
  .fixed {
    color: var(--warning-text);
    font-size: 0.85rem;
    font-weight: 400;
  }
  .preview {
    white-space: pre-wrap;
    padding: 0.9rem 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
  }
  .error {
    color: var(--error);
  }
  .done {
    color: var(--success);
  }
</style>
