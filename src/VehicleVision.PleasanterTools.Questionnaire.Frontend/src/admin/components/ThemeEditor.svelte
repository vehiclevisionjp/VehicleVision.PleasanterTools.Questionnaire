<script lang="ts">
  import { adminAssetUrl, uploadHeaderImage } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';
  import {
    CONTRAST_AA,
    DEFAULT_COLORS,
    THEME_FONTS,
    THEME_PRESETS,
    THEME_PRESET_COLORS,
    contrastRatio,
    resolveTheme,
    safeColor,
    type SurveyTheme,
    type ThemeFont,
  } from '../../lib/theme';

  /**
   * 回答画面の見た目を決める（Issue #56）。
   *
   * **決めるのは色 3 本・書体・ヘッダ画像だけ。**
   * 自由な CSS を書かせない。書かせると、そのまま回答者の画面へ流れる文字列になる
   * （`lib/theme.ts` の注意書き）。
   *
   * **保存は編集画面がまとめて行う。** ここは定義の `theme` を書き換えるだけで、
   * 下書きの保存も公開も行わない。**画像だけは先にサーバへ預ける**
   * （定義の JSON へ中身を載せないため）。
   */
  interface Props {
    surveyId: string;
    /** 今のテーマ。**無ければ既定の見た目。** */
    theme: SurveyTheme | null | undefined;
    onchange: (next: SurveyTheme | null) => void;
  }

  let { surveyId, theme, onchange }: Props = $props();

  let uploading = $state(false);
  let imageError = $state('');
  /** 個別の色を開いているか。**既定は畳む**（Issue #109）。 */
  let showDetails = $state(false);

  /** 色を持つ項目。**画面に出す順。** */
  type ColorKey =
    | 'accentColor'
    | 'backgroundColor'
    | 'textColor'
    | 'accentTextColor'
    | 'surfaceColor'
    | 'borderColor';

  const COLOR_FIELDS: { key: ColorKey; label: MessageKey }[] = [
    { key: 'accentColor', label: 'theme.accentColor' },
    { key: 'backgroundColor', label: 'theme.backgroundColor' },
    { key: 'textColor', label: 'theme.textColor' },
    { key: 'accentTextColor', label: 'theme.accentTextColor' },
    { key: 'surfaceColor', label: 'theme.surfaceColor' },
    { key: 'borderColor', label: 'theme.borderColor' },
  ];

  /**
   * 色欄が出す値。**未指定なら「いま実際に使われる色」を初期値にする。**
   *
   * **配色を選んでいればその色。** 既定の色を出すと、
   * 選んだ配色と関係のない色から編集が始まる。
   */
  function colorValue(key: ColorKey): string {
    return safeColor(theme?.[key]) ?? safeColor(resolved[key]) ?? DEFAULT_COLORS[key];
  }

  /** 配色を当てた後の色。**警告と色欄の初期値に使う。** */
  const resolved = $derived(resolveTheme(theme));

  /**
   * 読みにくい組み合わせ（Issue #109）。**保存は止めない。**
   *
   * 社内利用など、事情があることもある。**気付ける形にするだけ。**
   */
  const contrastWarnings = $derived.by(() => {
    const warnings: { label: MessageKey; ratio: number }[] = [];

    const pairs: { label: MessageKey; fg?: string | null; bg?: string | null }[] = [
      {
        label: 'theme.contrastText',
        fg: resolved.textColor ?? DEFAULT_COLORS.textColor,
        bg: resolved.backgroundColor ?? DEFAULT_COLORS.backgroundColor,
      },
      {
        label: 'theme.contrastAccent',
        fg: resolved.accentTextColor ?? DEFAULT_COLORS.accentTextColor,
        bg: resolved.accentColor ?? DEFAULT_COLORS.accentColor,
      },
      {
        label: 'theme.contrastSurface',
        fg: resolved.textColor ?? DEFAULT_COLORS.textColor,
        bg: resolved.surfaceColor ?? DEFAULT_COLORS.surfaceColor,
      },
    ];

    for (const pair of pairs) {
      const ratio = contrastRatio(pair.fg, pair.bg);
      if (ratio !== null && ratio < CONTRAST_AA) {
        warnings.push({ label: pair.label, ratio });
      }
    }

    return warnings;
  });

  /**
   * 色を変える。**`null` で「未指定」へ戻す。**
   *
   * **形は画面でも確かめる。** 色欄は `#rrggbb` しか作らないが、
   * ここを通る値が CSS の変数へ入るので、通す前に見る（`lib/theme.ts`）。
   */
  function setColor(key: ColorKey, value: string | null) {
    update({ [key]: value === null ? null : safeColor(value) } as Partial<SurveyTheme>);
  }

  /**
   * テーマを書き換える。
   *
   * **何も指定が残らなければ `null` を返す。**
   * 空のテーマを持たせると、触っていないアンケートの定義にも `theme` が載り、
   * 公開のたびに版の JSON が変わる。
   */
  function update(patch: Partial<SurveyTheme>) {
    const next: SurveyTheme = { ...(theme ?? {}), ...patch };

    const isDefault =
      !next.accentColor &&
      !next.backgroundColor &&
      !next.textColor &&
      !next.accentTextColor &&
      !next.surfaceColor &&
      !next.borderColor &&
      (next.preset ?? 'None') === 'None' &&
      (next.font ?? 'System') === 'System' &&
      !next.headerImageId;

    onchange(isDefault ? null : next);
  }

  /** 編集中のヘッダ画像。**公開前でも見えるよう管理画面の口を使う。** */
  const imageUrl = $derived(
    theme?.headerImageId ? adminAssetUrl(surveyId, theme.headerImageId) : null,
  );

  async function chooseImage(event: Event & { currentTarget: HTMLInputElement }) {
    const file = event.currentTarget.files?.[0];
    // **同じファイルをもう一度選べるようにする。** 値を残すと `change` が起きない
    event.currentTarget.value = '';
    if (!file) return;

    uploading = true;
    imageError = '';

    const result = await uploadHeaderImage(surveyId, file);
    uploading = false;

    if (!result.ok) {
      // **サーバの文言をそのまま出す。** 要求した言語で返ってきている
      imageError = result.message;
      return;
    }

    update({ headerImageId: result.value.assetId });
  }
</script>

<section class="theme">
  <h2>{t('theme.title')}</h2>
  <p class="hint">{t('theme.lead')}</p>

  <div class="presets">
    <span class="caption">{t('theme.preset')}</span>
    <p class="hint">{t('theme.presetHint')}</p>
    <div class="preset-list">
      {#each THEME_PRESETS as preset (preset)}
        {@const colors = THEME_PRESET_COLORS[preset]}
        <label class="preset" class:selected={(theme?.preset ?? 'None') === preset}>
          <input
            type="radio"
            name="theme-preset"
            value={preset}
            checked={(theme?.preset ?? 'None') === preset}
            onchange={() => update({ preset })}
          />
          <!-- **色そのものを見せる。** 名前だけでは何色か分からない。
               style へ入るのは THEME_PRESET_COLORS の決め打ちの値だけで、
               利用者が書いた文字列は 1 文字も入らない -->
          <span
            class="swatch"
            style:background={colors.backgroundColor ?? DEFAULT_COLORS.backgroundColor}
            style:border-color={colors.accentColor ?? DEFAULT_COLORS.accentColor}
          >
            <span
              class="swatch-accent"
              style:background={colors.accentColor ?? DEFAULT_COLORS.accentColor}
            ></span>
          </span>
          {t(`themePreset.${preset}` as MessageKey)}
        </label>
      {/each}
    </div>
  </div>

  {#if contrastWarnings.length > 0}
    <!-- **保存は止めない。** 事情があることもあるので、気付ける形にするだけ -->
    <div class="contrast" role="status">
      <p>{t('theme.contrastWarning')}</p>
      <ul>
        {#each contrastWarnings as warning (warning.label)}
          <li>{t(warning.label)}（{warning.ratio.toFixed(1)}:1）</li>
        {/each}
      </ul>
    </div>
  {/if}

  <button type="button" class="link toggle" onclick={() => (showDetails = !showDetails)}>
    {showDetails ? t('theme.hideDetails') : t('theme.showDetails')}
  </button>

  {#if showDetails}
    <div class="colors">
      {#each COLOR_FIELDS as field (field.key)}
        <div class="color">
          <label>
            {t(field.label)}
            <input
              type="color"
              value={colorValue(field.key)}
              oninput={(event) => setColor(field.key, event.currentTarget.value)}
            />
          </label>
          {#if theme?.[field.key]}
            <!-- **「未指定」へ戻せるようにする。** 色欄そのものは空にできない -->
            <button type="button" class="link" onclick={() => setColor(field.key, null)}>
              {t('theme.useDefault')}
            </button>
          {:else}
            <span class="hint">{t('theme.notSet')}</span>
          {/if}
        </div>
      {/each}
    </div>
  {/if}

  <label class="font">
    {t('theme.font')}
    <select
      value={theme?.font ?? 'System'}
      onchange={(event) => update({ font: event.currentTarget.value as ThemeFont })}
    >
      {#each THEME_FONTS as font (font)}
        <option value={font}>{t(`themeFont.${font}` as MessageKey)}</option>
      {/each}
    </select>
  </label>
  <p class="hint">{t('theme.fontHint')}</p>

  <div class="header-image">
    <span class="caption">{t('theme.headerImage')}</span>
    <p class="hint">{t('theme.headerImageHint')}</p>

    {#if imageUrl}
      <img src={imageUrl} alt={t('theme.headerImagePreview')} />
    {/if}

    <div class="image-actions">
      <label class="file">
        <span>{uploading ? t('theme.uploading') : t('theme.chooseImage')}</span>
        <input
          type="file"
          accept="image/png,image/jpeg,image/gif,image/webp"
          disabled={uploading}
          onchange={chooseImage}
        />
      </label>

      {#if theme?.headerImageId}
        <button type="button" class="link" onclick={() => update({ headerImageId: null })}>
          {t('theme.removeImage')}
        </button>
      {/if}
    </div>

    {#if imageError}<p class="error" role="alert">{imageError}</p>{/if}
  </div>
</section>

<style lang="scss">
  .theme {
    padding: 1.25rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
    margin-bottom: 1rem;
  }

  h2 {
    margin: 0 0 0.25rem;
    font-size: 1rem;
  }

  .hint {
    margin: 0 0 0.75rem;
    color: var(--muted);
    font-size: 0.82rem;
  }

  .colors {
    display: flex;
    gap: 1.5rem;
    flex-wrap: wrap;
    margin-bottom: 1rem;
  }

  /* ---- 名前の付いた配色（Issue #109）---------------------------------------- */

  .presets {
    margin-bottom: 1rem;
  }

  .preset-list {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .preset {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.35rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    font-size: 0.85rem;
    color: inherit;
    cursor: pointer;

    /* **選択中は枠を濃くする。** 色だけに頼らず、太さでも分かるようにする */
    &.selected {
      border-color: var(--accent);
      border-width: 2px;
      padding: calc(0.35rem - 1px) calc(0.6rem - 1px);
    }

    input {
      /* **見えないだけで、無くさない。** キーボードと読み上げはこれを辿る */
      position: absolute;
      opacity: 0;
      width: 0;
      height: 0;
    }
  }

  .swatch {
    display: inline-flex;
    align-items: flex-end;
    width: 1.5rem;
    height: 1.1rem;
    border: 1px solid;
    border-radius: 3px;
    overflow: hidden;
  }

  .swatch-accent {
    display: block;
    width: 100%;
    height: 0.35rem;
  }

  .contrast {
    padding: 0.5rem 0.75rem;
    margin-bottom: 0.75rem;
    border: 1px solid var(--border);
    /* **色だけに頼らない。** 左端の太い線でも「いつもと違う」が分かる */
    border-left: 4px solid var(--error);
    border-radius: 6px;
    font-size: 0.82rem;

    p {
      margin: 0 0 0.25rem;
    }

    ul {
      margin: 0;
      padding-left: 1.2rem;
      color: var(--muted);
    }
  }

  .toggle {
    margin-bottom: 0.75rem;
  }

  .color {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;

    .hint {
      margin: 0;
    }
  }

  label {
    display: block;
    font-size: 0.85rem;
    color: var(--muted);
  }

  input[type='color'] {
    display: block;
    width: 4rem;
    height: 2rem;
    margin-top: 0.2rem;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: #fff;
    cursor: pointer;
  }

  .font select {
    display: block;
    margin-top: 0.2rem;
    padding: 0.3rem 0.4rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    background: #fff;
    color: #101828;
  }

  .caption {
    display: block;
    font-size: 0.85rem;
    color: var(--muted);
  }

  .header-image img {
    display: block;
    width: 100%;
    max-width: 28rem;
    max-height: 10rem;
    object-fit: cover;
    border: 1px solid var(--border);
    border-radius: 6px;
    margin-bottom: 0.5rem;
  }

  .image-actions {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  /* **入力欄そのものは出さず、ラベルを釦に見せる** */
  .file {
    display: inline-block;
    padding: 0.35rem 0.75rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: #fff;
    color: var(--accent);
    cursor: pointer;

    input {
      display: none;
    }
  }

  .link {
    background: none;
    border: none;
    padding: 0;
    color: var(--accent);
    font: inherit;
    font-size: 0.82rem;
    cursor: pointer;
  }

  .error {
    margin: 0.5rem 0 0;
    color: var(--error);
    font-size: 0.85rem;
  }
</style>
