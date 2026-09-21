<script lang="ts">
  import {
    setMaintenanceMode,
    type MaintenanceModeStatus,
  } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    status: MaintenanceModeStatus;
    canManage: boolean;
    onchanged: (status: MaintenanceModeStatus) => void;
  }

  let { status, canManage, onchanged }: Props = $props();
  let messageJa = $state('');
  let messageEn = $state('');
  let busy = $state(false);
  let error = $state('');

  $effect(() => {
    messageJa = status.messageJa;
    messageEn = status.messageEn;
  });

  async function save(enabled: boolean) {
    busy = true;
    error = '';
    const result = await setMaintenanceMode(enabled, messageJa, messageEn);
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    onchanged(result.value);
  }
</script>

<aside class:active={status.isActive} class="maintenance" role={status.isActive ? 'alert' : 'status'}>
  <div class="summary">
    <span class="material-icons" aria-hidden="true">
      {status.isActive ? 'engineering' : 'check_circle'}
    </span>
    <div>
      <strong>
        {status.isActive ? t('maintenance.active') : t('maintenance.inactive')}
      </strong>
      <p>
        {#if status.environmentEnabled && status.databaseEnabled}
          {t('maintenance.sourceBoth')}
        {:else if status.environmentEnabled}
          {t('maintenance.sourceEnvironment')}
        {:else if status.databaseEnabled}
          {t('maintenance.sourceDatabase')}
        {:else}
          {t('maintenance.sourceNone')}
        {/if}
      </p>
      {#if status.environmentEnabled}
        <p class="locked">{t('maintenance.environmentLocked')}</p>
      {/if}
    </div>
  </div>

  {#if canManage}
    <details>
      <summary>{t('maintenance.settings')}</summary>
      <div class="fields">
        <label>
          {t('maintenance.messageJa')}
          <textarea bind:value={messageJa} maxlength="500"></textarea>
        </label>
        <label>
          {t('maintenance.messageEn')}
          <textarea bind:value={messageEn} maxlength="500"></textarea>
        </label>
        <p class="hint">{t('maintenance.messageHint')}</p>
        <button
          type="button"
          class:stop={!status.databaseEnabled}
          disabled={busy}
          onclick={() => save(!status.databaseEnabled)}
        >
          {status.databaseEnabled ? t('maintenance.disable') : t('maintenance.enable')}
        </button>
        {#if status.environmentEnabled && status.databaseEnabled}
          <p class="hint">{t('maintenance.environmentRemains')}</p>
        {/if}
        {#if error}<p class="error" role="alert">{error}</p>{/if}
      </div>
    </details>
  {/if}
</aside>

<style lang="scss">
  .maintenance {
    display: grid;
    gap: 0.75rem;
    max-width: 77rem;
    margin: 1rem auto 0;
    padding: 0.9rem 1rem;
    border: 2px solid var(--border);
    border-radius: 6px;
    background: var(--surface);

    &.active {
      border-color: var(--warning-border);
      background: var(--warning-surface);
      color: var(--warning-text);
    }
  }

  .summary {
    display: flex;
    gap: 0.75rem;
    align-items: flex-start;
  }

  p {
    margin: 0.2rem 0 0;
  }

  .locked {
    font-weight: 700;
  }

  details {
    margin-left: 2rem;
  }

  summary {
    cursor: pointer;
    font-weight: 600;
  }

  .fields {
    display: grid;
    gap: 0.75rem;
    max-width: 42rem;
    margin-top: 0.75rem;
  }

  label {
    display: grid;
    gap: 0.25rem;
    font-weight: 600;
  }

  textarea {
    min-height: 4rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    font-weight: 400;
  }

  button.stop {
    border-color: var(--error);
    background: var(--error);
  }

  .hint {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .error {
    color: var(--error);
  }
</style>
