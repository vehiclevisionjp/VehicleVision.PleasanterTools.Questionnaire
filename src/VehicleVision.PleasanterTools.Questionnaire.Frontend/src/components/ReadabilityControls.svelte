<script lang="ts">
  import {
    COLOR_MODES,
    FONT_SIZES,
    type ColorMode,
    type FontSize,
    type ReadabilityPreferences,
  } from '../lib/readability';

  interface Labels {
    group: string;
    fontSize: string;
    fontSizes: Record<FontSize, string>;
    colorMode: string;
    colorModes: Record<ColorMode, string>;
  }

  interface Props {
    id: string;
    preferences: ReadabilityPreferences;
    labels: Labels;
    onchange: (preferences: ReadabilityPreferences) => void;
  }

  let { id, preferences, labels, onchange }: Props = $props();
</script>

<fieldset aria-label={labels.group}>
  <legend>{labels.group}</legend>
  <label for={`${id}-font-size`}>
    <span>{labels.fontSize}</span>
    <select
      id={`${id}-font-size`}
      value={preferences.fontSize}
      onchange={(event) =>
        onchange({ ...preferences, fontSize: event.currentTarget.value as FontSize })}
    >
      {#each FONT_SIZES as fontSize (fontSize)}
        <option value={fontSize}>{labels.fontSizes[fontSize]}</option>
      {/each}
    </select>
  </label>
  <label for={`${id}-color-mode`}>
    <span>{labels.colorMode}</span>
    <select
      id={`${id}-color-mode`}
      value={preferences.colorMode}
      onchange={(event) =>
        onchange({ ...preferences, colorMode: event.currentTarget.value as ColorMode })}
    >
      {#each COLOR_MODES as colorMode (colorMode)}
        <option value={colorMode}>{labels.colorModes[colorMode]}</option>
      {/each}
    </select>
  </label>
</fieldset>

<style>
  fieldset {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
    margin: 0;
    padding: 0;
    border: 0;
  }

  legend {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    padding: 0;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }

  label {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    min-width: 0;
  }

  select {
    max-width: 100%;
    padding: 0.25rem 0.4rem;
    border: 1px solid var(--border);
    border-radius: 0.25rem;
    background: var(--surface);
    color: var(--text);
    font: inherit;
  }
</style>
