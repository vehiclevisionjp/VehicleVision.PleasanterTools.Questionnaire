<script lang="ts">
  import type { NoteBlock } from '../lib/types';
  import { headingLevel, renderableBlocks, renderableInlines } from '../lib/note';

  /**
   * 説明文ブロックの本文を出す（Issue #108）。
   *
   * **`innerHTML` を使わない。** サーバが作った構造から要素を組み立てるだけ。
   * **HTML の文字列を受け取る経路がどこにも無いので、消し漏らしという事故が起きない。**
   */
  let { blocks }: { blocks: NoteBlock[] } = $props();

  const visible = $derived(renderableBlocks(blocks));
</script>

{#each visible as block, index (index)}
  {@const inlines = renderableInlines(block.inlines)}
  {#if block.kind === 'Heading'}
    {@const level = headingLevel(block.level)}
    <!-- **深さは 2 〜 4 に収める。** 題名（h1）と並ばせない -->
    {#if level === 2}
      <h2>{#each inlines as inline, i (i)}{@render piece(inline)}{/each}</h2>
    {:else if level === 3}
      <h3>{#each inlines as inline, i (i)}{@render piece(inline)}{/each}</h3>
    {:else}
      <h4>{#each inlines as inline, i (i)}{@render piece(inline)}{/each}</h4>
    {/if}
  {:else if block.kind === 'BulletList'}
    <ul>
      {#each block.items as item, i (i)}
        <li>
          {#each renderableInlines(item.inlines) as inline, j (j)}{@render piece(inline)}{/each}
        </li>
      {/each}
    </ul>
  {:else if block.kind === 'NumberedList'}
    <ol>
      {#each block.items as item, i (i)}
        <li>
          {#each renderableInlines(item.inlines) as inline, j (j)}{@render piece(inline)}{/each}
        </li>
      {/each}
    </ol>
  {:else}
    <p>{#each inlines as inline, i (i)}{@render piece(inline)}{/each}</p>
  {/if}
{/each}

{#snippet piece(inline: { kind: string; text: string; href?: string | null })}
  {#if inline.kind === 'Bold'}
    <strong>{inline.text}</strong>
  {:else if inline.kind === 'Italic'}
    <em>{inline.text}</em>
  {:else if inline.kind === 'Link' && inline.href}
    <!-- **別のタブで開く。** 回答の途中で画面が置き換わると入力が消える。
         **`noopener` を付ける。** 開いた先から元の画面を触らせない -->
    <a href={inline.href} target="_blank" rel="noopener noreferrer">{inline.text}</a>
  {:else}
    {inline.text}
  {/if}
{/snippet}

<style>
  h2,
  h3,
  h4 {
    margin: 0.75rem 0 0.25rem;
    line-height: 1.4;

    &:first-child {
      margin-top: 0;
    }
  }

  h2 {
    font-size: 1.15rem;
  }

  h3 {
    font-size: 1.05rem;
  }

  h4 {
    font-size: 1rem;
  }

  p {
    margin: 0 0 0.5rem;
    line-height: 1.7;
    white-space: pre-line;
  }

  ul,
  ol {
    margin: 0 0 0.5rem;
    padding-left: 1.5rem;
    line-height: 1.7;
  }

  a {
    color: var(--accent);
  }

  :last-child {
    margin-bottom: 0;
  }
</style>
