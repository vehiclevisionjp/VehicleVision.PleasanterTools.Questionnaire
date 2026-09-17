<script lang="ts">
  import { untrack } from 'svelte';
  import { t } from '../lib/i18n/state.svelte';
  import {
    configWithMapRows,
    mapRowsFromConfig,
    type MapConfigRow,
  } from '../lib/converterConfig';

  interface Props {
    config: Record<string, string>;
    candidates: string[];
    listId: string;
    onchange: (config: Record<string, string>) => void;
  }

  let { config, candidates, listId, onchange }: Props = $props();
  let rows = $state<MapConfigRow[]>(untrack(() => mapRowsFromConfig(config)));

  function update(index: number, next: Partial<MapConfigRow>) {
    const current = rows[index];
    if (!current) return;
    rows[index] = {
      source: next.source ?? current.source,
      target: next.target ?? current.target,
    };
    onchange(configWithMapRows(config, rows));
  }

  function add() {
    rows.push({ source: '', target: '' });
  }

  function remove(index: number) {
    rows.splice(index, 1);
    onchange(configWithMapRows(config, rows));
  }
</script>

<div class="map-config">
  <datalist id={listId}>
    {#each candidates as candidate (candidate)}
      <option value={candidate}></option>
    {/each}
  </datalist>

  {#each rows as row, index (index)}
    <div class="map-row">
      <input
        type="text"
        list={listId}
        aria-label={t('mapping.config.mapSource')}
        placeholder={t('mapping.config.mapSource')}
        value={row.source}
        oninput={(event) => update(index, { source: event.currentTarget.value })}
      />
      <span aria-hidden="true">→</span>
      <input
        type="text"
        aria-label={t('mapping.config.mapTarget')}
        placeholder={t('mapping.config.mapTarget')}
        value={row.target}
        oninput={(event) => update(index, { target: event.currentTarget.value })}
      />
      <button
        type="button"
        class="icon danger"
        onclick={() => remove(index)}
        aria-label={t('mapping.config.removeMapRow')}>×</button
      >
    </div>
  {/each}

  <button type="button" class="secondary small" onclick={add}>
    {t('mapping.config.addMapRow')}
  </button>
</div>

<style lang="scss">
  .map-config {
    margin-top: 0.5rem;
  }

  .map-row {
    display: grid;
    grid-template-columns: minmax(5rem, 1fr) auto minmax(5rem, 1fr) auto;
    gap: 0.3rem;
    align-items: center;
    margin-bottom: 0.4rem;
  }

  input {
    display: block;
    width: 100%;
    min-width: 0;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    color: #101828;
    box-sizing: border-box;
  }

  .icon {
    width: 1.9rem;
    height: 1.9rem;
    padding: 0;
    line-height: 1;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 4px;
    cursor: pointer;

    &.danger {
      color: var(--error);
    }
  }

  .small {
    font-size: 0.85rem;
    padding: 0.35rem 0.75rem;
  }
</style>
