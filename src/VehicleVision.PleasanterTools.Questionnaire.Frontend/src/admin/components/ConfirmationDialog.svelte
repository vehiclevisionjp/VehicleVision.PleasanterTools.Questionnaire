<script lang="ts">
  import { tick } from 'svelte';
  import {
    acceptConfirmation,
    cancelConfirmation,
    currentConfirmation,
    handleConfirmationKeydown,
    setConfirmationChecked,
  } from '../lib/confirmation.svelte';
  import { t } from '../lib/i18n/state.svelte';

  const titleId = 'confirmation-dialog-title';
  const descriptionId = 'confirmation-dialog-description';
  const request = $derived(currentConfirmation());

  let cancelButton = $state<HTMLButtonElement>();
  let confirmButton = $state<HTMLButtonElement>();

  $effect(() => {
    if (request !== null) {
      const target = request.focusCancel === true ? cancelButton : confirmButton;
      void tick().then(() => target?.focus());
    }
  });

  function handleBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      cancelConfirmation();
    }
  }

  function handleKeydown(event: KeyboardEvent): void {
    handleConfirmationKeydown(
      event,
      cancelButton ?? null,
      confirmButton ?? null,
      document.activeElement,
    );
  }
</script>

{#if request}
  <div
    class="backdrop"
    role="presentation"
    onclick={handleBackdropClick}
    onkeydown={handleKeydown}
  >
    <div
      class="dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
    >
      <h2 id={titleId}>{request.title}</h2>
      <p id={descriptionId}>{request.message}</p>
      {#if request.checkbox}
        <!-- **チェックの初期値は呼び出し側が決める**（API キーの失効は、初期値オン） -->
        <label class="choice">
          <input
            type="checkbox"
            checked={request.checkbox.checked}
            onchange={(event) => setConfirmationChecked(event.currentTarget.checked)}
          />
          <span>{request.checkbox.label}</span>
        </label>
      {/if}
      <div class="actions">
        <button type="button" class="secondary" bind:this={cancelButton} onclick={cancelConfirmation}>
          {t('list.cancel')}
        </button>
        <button
          type="button"
          class:danger={request.danger}
          bind:this={confirmButton}
          onclick={acceptConfirmation}
        >
          {request.confirmLabel}
        </button>
      </div>
    </div>
  </div>
{/if}

<style lang="scss">
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 1000;
    display: grid;
    place-items: center;
    padding: var(--space-4);
    background: var(--overlay-bg);
  }

  .dialog {
    width: min(28rem, 100%);
    padding: var(--space-6);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    background: var(--surface);
    box-shadow: var(--shadow-lg);
  }

  h2 {
    margin: 0;
    font-size: 1.15rem;
  }

  p {
    margin: var(--space-3) 0 0;
    color: var(--text);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: var(--space-3);
    margin-top: var(--space-6);
  }

  .choice {
    display: flex;
    align-items: flex-start;
    gap: var(--space-2);
    margin-top: var(--space-3);
    font-size: 0.9rem;
  }

  .choice input {
    margin-top: 0.2rem;
  }

  button.danger {
    border-color: var(--error);
    background: var(--error);
  }
</style>
