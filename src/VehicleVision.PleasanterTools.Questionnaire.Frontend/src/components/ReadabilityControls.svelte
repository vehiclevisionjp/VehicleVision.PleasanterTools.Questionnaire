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
     * 選択肢をアイコンの釦で横に並べる。**選んでいるものは `aria-pressed` と枠で示し、
     * 名前は `aria-label` / `title` に持つ**（色や形だけに頼らない）。既定は従来のドロップダウン。
     */
    compact?: boolean;
    onchange: (
      preferences: ReadabilityPreferences,
      changed: ReadabilityPreferenceName,
    ) => void;
  }

  let { id, preferences, labels, compact = false, onchange }: Props = $props();

  /** 文字の大きさのアイコンで「A」を縮尺する倍率。 */
  const FONT_ICON_SCALE: Record<FontSize, number> = {
    standard: 0.75,
    large: 1,
    extraLarge: 1.3,
  };
</script>

{#if compact}
<!-- **選択肢をアイコンで横に並べる**（Issue #592）。選んでいるものは `aria-pressed` と枠で示す -->
<div class="icons" role="group" aria-label={labels.group}>
  <div class="choices" role="group" aria-label={labels.fontSize}>
    {#each FONT_SIZES as fontSize (fontSize)}
      <button
        type="button"
        id={`${id}-font-size-${fontSize}`}
        class="icon"
        data-value={fontSize}
        aria-pressed={preferences.fontSize === fontSize}
        title={labels.fontSizes[fontSize]}
        aria-label={labels.fontSizes[fontSize]}
        onclick={() => onchange({ ...preferences, fontSize }, 'fontSize')}
      >
        <!-- **「A」の大きさがそのまま文字の大きさ。** 標準・大・特大 -->
        <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
          <path
            d="M12 3 5 16h3l1.2-2.8h5.6L16 16h3L12 3Zm0 5.2 1.8 4H10.2L12 8.2Z"
            fill="currentColor"
            transform={`translate(12 20) scale(${FONT_ICON_SCALE[fontSize]}) translate(-12 -16)`}
          />
        </svg>
      </button>
    {/each}
  </div>
  <div class="choices" role="group" aria-label={labels.colorMode}>
    {#each COLOR_MODES as colorMode (colorMode)}
      <button
        type="button"
        id={`${id}-color-mode-${colorMode}`}
        class="icon"
        data-value={colorMode}
        aria-pressed={preferences.colorMode === colorMode}
        title={labels.colorModes[colorMode]}
        aria-label={labels.colorModes[colorMode]}
        onclick={() => onchange({ ...preferences, colorMode }, 'colorMode')}
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
          {#if colorMode === 'system'}
            <rect x="3" y="4" width="18" height="12" rx="2" />
            <path d="M8 20h8M12 16v4" />
          {:else if colorMode === 'default'}
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
          {:else if colorMode === 'highContrast'}
            <circle cx="12" cy="12" r="9" />
            <path d="M12 3a9 9 0 0 1 0 18Z" fill="currentColor" />
          {:else}
            <path d="M20 14.5A8 8 0 0 1 9.5 4 8.5 8.5 0 1 0 20 14.5Z" />
          {/if}
        </svg>
      </button>
    {/each}
  </div>
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
    flex-wrap: wrap;
    align-items: center;
    gap: 0.25rem 0.75rem;
  }

  .choices {
    display: flex;
    align-items: center;
    gap: 0.125rem;
  }

  .icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 2.25rem;
    height: 2.25rem;
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

  /* **選んでいるものは枠と色で示す。** 色だけに頼らず `aria-pressed` も持つ */
  .icon[aria-pressed='true'] {
    border-color: var(--accent);
    color: var(--accent);
  }

  .icon svg {
    width: 1.5rem;
    height: 1.5rem;
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
