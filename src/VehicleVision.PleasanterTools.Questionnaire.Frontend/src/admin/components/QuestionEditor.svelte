<script lang="ts">
  import {
    hasChoices,
    isDisplayOnly,
    questionTypes,
    text,
    withText,
    type Question,
  } from '../lib/types';

  interface Props {
    question: Question;
    /** 割り当て先の列。**無いと「Pleasanter に残らない」ことが分からない** */
    mappedColumns: string[];
    canMoveUp: boolean;
    canMoveDown: boolean;
    onchange: (question: Question) => void;
    onremove: () => void;
    onmove: (direction: -1 | 1) => void;
  }

  let { question, mappedColumns, canMoveUp, canMoveDown, onchange, onremove, onmove }: Props =
    $props();

  const showChoices = $derived(hasChoices(question.type));
  const displayOnly = $derived(isDisplayOnly(question.type));

  function update(patch: Partial<Question>) {
    onchange({ ...question, ...patch });
  }

  function addChoice() {
    const next = question.choices.length + 1;
    update({
      choices: [
        ...question.choices,
        { value: `choice-${next}`, label: withText(undefined, `選択肢 ${next}`) },
      ],
    });
  }

  function updateChoice(index: number, patch: Partial<(typeof question.choices)[number]>) {
    update({
      choices: question.choices.map((choice, i) => (i === index ? { ...choice, ...patch } : choice)),
    });
  }

  function removeChoice(index: number) {
    update({ choices: question.choices.filter((_, i) => i !== index) });
  }
</script>

<article class="question">
  <div class="head">
    <input
      class="title"
      type="text"
      placeholder="設問の文言"
      value={text(question.title)}
      oninput={(event) => update({ title: withText(question.title, event.currentTarget.value) })}
    />

    <select
      value={question.type}
      onchange={(event) => {
        const type = event.currentTarget.value as Question['type'];
        // **形式を変えたら要らない設定は落とす。** 残ると画面と食い違う
        update({
          type,
          choices: hasChoices(type) ? question.choices : [],
          isRequired: isDisplayOnly(type) ? false : question.isRequired,
        });
      }}
    >
      {#each questionTypes as option (option.value)}
        <option value={option.value}>{option.label}</option>
      {/each}
    </select>

    <div class="actions">
      <button type="button" class="icon" disabled={!canMoveUp} onclick={() => onmove(-1)} aria-label="上へ">
        ↑
      </button>
      <button
        type="button"
        class="icon"
        disabled={!canMoveDown}
        onclick={() => onmove(1)}
        aria-label="下へ">↓</button
      >
      <button type="button" class="icon danger" onclick={onremove} aria-label="削除">×</button>
    </div>
  </div>

  <input
    class="description"
    type="text"
    placeholder="補足（任意）"
    value={text(question.description)}
    oninput={(event) =>
      update({ description: withText(question.description, event.currentTarget.value) })}
  />

  <div class="meta">
    <code class="id">{question.questionId}</code>

    {#if !displayOnly}
      <label class="inline">
        <input
          type="checkbox"
          checked={question.isRequired}
          onchange={(event) => update({ isRequired: event.currentTarget.checked })}
        />
        必須
      </label>
    {/if}

    {#if displayOnly}
      <span class="note">回答を持たない表示専用の要素です。</span>
    {:else if mappedColumns.length === 0}
      <!-- **拒否はしないが伝える。** 気付かずに公開すると回答が Pleasanter に残らない -->
      <span class="unmapped">未割り当て（Pleasanter に残りません）</span>
    {:else}
      <span class="mapped">→ {mappedColumns.join(' / ')}</span>
    {/if}
  </div>

  {#if showChoices}
    <div class="choices">
      {#each question.choices as choice, index (index)}
        <div class="choice">
          <input
            type="text"
            class="choice-label"
            placeholder="画面に出る文字列"
            value={text(choice.label)}
            oninput={(event) =>
              updateChoice(index, { label: withText(choice.label, event.currentTarget.value) })}
          />
          <input
            type="text"
            class="choice-value"
            placeholder="保存される値"
            value={choice.value}
            oninput={(event) => updateChoice(index, { value: event.currentTarget.value })}
          />
          <label class="inline">
            <input
              type="checkbox"
              checked={choice.isOther === true}
              onchange={(event) => updateChoice(index, { isOther: event.currentTarget.checked })}
            />
            その他
          </label>
          <button type="button" class="icon danger" onclick={() => removeChoice(index)} aria-label="選択肢を削除">
            ×
          </button>
        </div>
      {/each}

      <button type="button" class="secondary small" onclick={addChoice}>選択肢を足す</button>

      <!-- **保存される値が Pleasanter の列に入る。** 画面の文字列ではない -->
      <p class="hint">左が画面に出る文字列、右が Pleasanter へ保存される値です。</p>
    </div>
  {/if}

  {#if question.type === 'Scale' || question.type === 'Rating'}
    <div class="range">
      <label>
        下限
        <input
          type="number"
          value={question.settings.scaleMinimum ?? 1}
          oninput={(event) =>
            update({
              settings: { ...question.settings, scaleMinimum: Number(event.currentTarget.value) },
            })}
        />
      </label>
      <label>
        上限
        <input
          type="number"
          value={question.settings.scaleMaximum ?? 5}
          oninput={(event) =>
            update({
              settings: { ...question.settings, scaleMaximum: Number(event.currentTarget.value) },
            })}
        />
      </label>
    </div>
  {/if}
</article>

<style lang="scss">
  .question {
    padding: 1rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 6px;
    margin-bottom: 0.75rem;
  }

  .head {
    display: flex;
    gap: 0.5rem;
    align-items: center;
  }

  .title {
    flex: 1;
    font-weight: 600;
  }

  input[type='text'],
  input[type='number'],
  select {
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    box-sizing: border-box;
  }

  .description {
    width: 100%;
    margin-top: 0.5rem;
    color: var(--muted);
  }

  .meta {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-top: 0.5rem;
    font-size: 0.82rem;
    flex-wrap: wrap;
  }

  .id {
    color: var(--muted);
    background: var(--bg);
    padding: 0.1rem 0.35rem;
    border-radius: 3px;
  }

  .inline {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
  }

  .unmapped {
    color: #b54708;
  }

  .mapped {
    color: #067647;
  }

  .note {
    color: var(--muted);
  }

  .choices {
    margin-top: 0.75rem;
    padding-top: 0.75rem;
    border-top: 1px dashed var(--border);
  }

  .choice {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    margin-bottom: 0.4rem;
  }

  .choice-label {
    flex: 2;
  }

  .choice-value {
    flex: 1;
    font-family: ui-monospace, monospace;
    font-size: 0.85rem;
  }

  .range {
    display: flex;
    gap: 1rem;
    margin-top: 0.75rem;

    label {
      font-size: 0.85rem;
    }

    input {
      width: 5rem;
      display: block;
      margin-top: 0.2rem;
    }
  }

  .hint {
    color: var(--muted);
    font-size: 0.8rem;
    margin: 0.5rem 0 0;
  }

  .actions {
    display: flex;
    gap: 0.25rem;
  }

  .icon {
    width: 1.9rem;
    height: 1.9rem;
    padding: 0;
    line-height: 1;
    background: #fff;
    color: var(--muted);
    border: 1px solid var(--border);
    border-radius: 4px;
    cursor: pointer;

    &:disabled {
      opacity: 0.35;
      cursor: default;
    }

    &.danger {
      color: var(--error);
    }
  }

  .small {
    font-size: 0.85rem;
    padding: 0.35rem 0.75rem;
  }
</style>
