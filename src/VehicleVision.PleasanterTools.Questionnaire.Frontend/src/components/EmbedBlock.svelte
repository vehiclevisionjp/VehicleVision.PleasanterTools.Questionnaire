<script lang="ts">
  import type { Question } from '../lib/types';
  import { text } from '../lib/types';
  import type { Language } from '../lib/i18n/language';
  import { translator } from '../lib/i18n/messages';
  import { embedAspectRatio, embedSource } from '../lib/embed';

  interface Props {
    question: Question;
    language: Language;
  }

  let { question, language }: Props = $props();

  const t = $derived(translator(language));

  /**
   * **出す前にもう一度確かめる。**
   * 配信元の設定は後から狭められるうえ、公開済みの定義は版として固まっていて
   * **後から書き換わらない。**
   */
  const embed = $derived(embedSource(question));

  const label = $derived(
    embed?.alternativeText ? text(embed.alternativeText, language) : text(question.title, language),
  );

  const ratio = $derived(embed ? embedAspectRatio(embed) : 1);
</script>

<section class="embed">
  {#if question.title}
    <h3>{text(question.title, language)}</h3>
  {/if}

  {#if question.description}
    <p class="description">{text(question.description, language)}</p>
  {/if}

  {#if embed === undefined}
    <p class="blocked">{t('embed.blocked')}</p>
  {:else if embed.kind === 'Image'}
    <!-- **参照元を渡さない。** どのアンケートを見ているかを配信元へ知らせない -->
    <img src={embed.url} alt={label} loading="lazy" referrerpolicy="no-referrer" />
  {:else}
    <!--
      **`allow-same-origin` は絶対に付けない。**
      付けると埋め込み先が本アプリと同じ生成元として扱われ、
      **回答画面の Cookie や localStorage（下書き）へ届いてしまう。**
    -->
    <div class="frame" style="aspect-ratio: {ratio};">
      <iframe
        src={embed.url}
        title={label}
        loading="lazy"
        referrerpolicy="no-referrer"
        sandbox="allow-scripts"
      ></iframe>
    </div>
  {/if}
</section>

<style>
  .embed {
    margin: 0 0 1.5rem;
  }

  h3 {
    margin: 0 0 0.25rem;
    font-size: 1.05rem;
  }

  .description {
    margin: 0 0 0.5rem;
    color: var(--color-muted, #555);
  }

  img {
    max-width: 100%;
    height: auto;
    display: block;
  }

  .frame {
    width: 100%;
  }

  iframe {
    width: 100%;
    height: 100%;
    border: 0;
  }

  .blocked {
    margin: 0;
    color: var(--color-muted, #555);
  }
</style>
