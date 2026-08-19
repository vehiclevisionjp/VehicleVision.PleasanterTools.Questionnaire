<script lang="ts">
  import { adminAssetUrl, uploadHeaderImage } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';
  import {
    DEFAULT_COLORS,
    THEME_FONTS,
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

  /** 色を持つ項目。**画面に出す順。** */
  type ColorKey = 'accentColor' | 'backgroundColor' | 'textColor';

  const COLOR_FIELDS: { key: ColorKey; label: MessageKey }[] = [
    { key: 'accentColor', label: 'theme.accentColor' },
    { key: 'backgroundColor', label: 'theme.backgroundColor' },
    { key: 'textColor', label: 'theme.textColor' },
  ];

  /** 色欄が出す値。**未指定なら既定の色を初期値にする。** */
  function colorValue(key: ColorKey): string {
    return safeColor(theme?.[key]) ?? DEFAULT_COLORS[key];
  }

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
