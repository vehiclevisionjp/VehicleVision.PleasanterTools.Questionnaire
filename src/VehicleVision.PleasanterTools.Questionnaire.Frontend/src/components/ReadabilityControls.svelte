<script lang="ts">
  import {
    COLOR_MODES,
    FONT_SIZES,
    type ColorMode,
    type FontSize,
    type ReadabilityPreferenceName,
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
    /**
     * アイコンのボタンで出す（Issue #592）。回答画面用。
     *
     * 押すと次の値へ巡る。**状態はアイコンで示し、名前と現在値は `aria-label` / `title` に持つ**
     * （色や形だけに頼らず、読み上げでも現在値が分かるように）。既定は従来のドロップダウン。
     */
    compact?: boolean;
    onchange: (
      preferences: ReadabilityPreferences,
      changed: ReadabilityPreferenceName,
    ) => void;
  }

  let { id, preferences, labels, compact = false, onchange }: Props = $props();

  function next<T>(values: readonly T[], current: T): T {
    return values[(values.indexOf(current) + 1) % values.length]!;
  }

  const fontSizeText = $derived(labels.fontSizes[preferences.fontSize]);
  const colorModeText = $derived(labels.colorModes[preferences.colorMode]);
  const fontSizeLevel = $derived(FONT_SIZES.indexOf(preferences.fontSize) + 1);
</script>

{#if compact}
<div class="icons" role="group" aria-label={labels.group}>
  <button
    type="button"
    id={`${id}-font-size`}
    class="icon"
    data-value={preferences.fontSize}
    title={`${labels.fontSize}: ${fontSizeText}`}
    aria-label={`${labels.fontSize}: ${fontSizeText}`}
    onclick={() =>
      onchange(
        { ...preferences, fontSize: next(FONT_SIZES, preferences.fontSize) },
        'fontSize',
      )}
  >
    <!-- **「A」の下の点の数が大きさ。** 標準 1・大 2・特大 3 -->
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M12 3 5 16h3l1.2-2.8h5.6L16 16h3L12 3Zm0 5.2 1.8 4H10.2L12 8.2Z" fill="currentColor" />
      {#each Array.from({ length: fontSizeLevel }, (_, index) => index) as dot (dot)}
        <circle cx={12 + (dot - (fontSizeLevel - 1) / 2) * 4} cy="20.5" r="1.1" fill="currentColor" />
      {/each}
    </svg>
  </button>
  <button
    type="button"
    id={`${id}-color-mode`}
    class="icon"
    data-value={preferences.colorMode}
    title={`${labels.colorMode}: ${colorModeText}`}
    aria-label={`${labels.colorMode}: ${colorModeText}`}
    onclick={() =>
      onchange(
        { ...preferences, colorMode: next(COLOR_MODES, preferences.colorMode) },
        'colorMode',
      )}
  >
    <svg
      viewBox="0 0 24 24"
      aria-hidden="true"
      focusable="false"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
    >
      {#if preferences.colorMode === 'system'}
        <rect x="3" y="4" width="18" height="12" rx="2" />
        <path d="M8 20h8M12 16v4" />
      {:else if preferences.colorMode === 'default'}
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
      {:else if preferences.colorMode === 'highContrast'}
        <circle cx="12" cy="12" r="9" />
        <path d="M12 3a9 9 0 0 1 0 18Z" fill="currentColor" />
      {:else}
        <path d="M20 14.5A8 8 0 0 1 9.5 4 8.5 8.5 0 1 0 20 14.5Z" />
      {/if}
    </svg>
  </button>
  <!-- **押した結果を読み上げへ伝える。** ボタンの名前が変わるだけでは気付けない -->
  <span class="sr-only" role="status">{fontSizeText} / {colorModeText}</span>
</div>
{:else}
<fieldset aria-label={labels.group}>
  <legend>{labels.group}</legend>
  <label for={`${id}-font-size`}>
    <span>{labels.fontSize}</span>
    <select
      id={`${id}-font-size`}
      value={preferences.fontSize}
      onchange={(event) =>
        onchange(
          { ...preferences, fontSize: event.currentTarget.value as FontSize },
          'fontSize',
        )}
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
        onchange(
          { ...preferences, colorMode: event.currentTarget.value as ColorMode },
          'colorMode',
        )}
    >
      {#each COLOR_MODES as colorMode (colorMode)}
        <option value={colorMode}>{labels.colorModes[colorMode]}</option>
      {/each}
    </select>
  </label>
</fieldset>
{/if}

<style>
  .icons {
    display: flex;
    align-items: center;
    gap: 0.25rem;
  }

  .icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    padding: 0;
    border: 1px solid transparent;
    border-radius: 0.35rem;
    background: transparent;
    color: var(--muted);
    cursor: pointer;
  }

  .icon:hover {
    border-color: var(--border);
    color: var(--text);
  }

  .icon svg {
    width: 1.25rem;
    height: 1.25rem;
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    padding: 0;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }

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
