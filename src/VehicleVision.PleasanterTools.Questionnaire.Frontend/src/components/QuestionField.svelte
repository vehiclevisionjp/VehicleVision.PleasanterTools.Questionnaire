<script lang="ts">
  import type { AnswerState, Question } from '../lib/types';
  import { text } from '../lib/types';

  interface Props {
    question: Question;
    /** **まだ初期化されていないことがある。** 辞書からそのまま渡ってくるため */
    answer: AnswerState | undefined;
    error?: string;
  }

  let { question, answer = $bindable(), error }: Props = $props();

  /** 読み取り用。未初期化なら空の回答として扱う。 */
  const current = $derived<AnswerState>(answer ?? { values: [], otherText: '' });

  const labelId = $derived(`label-${question.questionId}`);
  const errorId = $derived(`error-${question.questionId}`);
  const otherChoice = $derived(question.choices.find((choice) => choice.isOther));
  const otherSelected = $derived(
    otherChoice !== undefined && current.values.includes(otherChoice.value),
  );

  function setSingle(value: string) {
    answer = { ...current, values: value === '' ? [] : [value] };
  }

  function toggleMultiple(value: string, checked: boolean) {
    const values = checked
      ? [...current.values, value]
      : current.values.filter((existing) => existing !== value);
    answer = { ...current, values };
  }

  const scaleValues = $derived.by(() => {
    const min = question.settings.scaleMinimum ?? 1;
    const max = question.settings.scaleMaximum ?? 5;
    return Array.from({ length: Math.max(0, max - min + 1) }, (_, index) => min + index);
  });

  function setFiles(files: File[]) {
    answer = { ...current, files };
  }

  /** 添付の上限を文字で出す。**選んでから弾かれるより先に伝える。** */
  const fileLimits = $derived.by(() => {
    const parts: string[] = [];
    if (question.settings.maxFileCount !== undefined) {
      parts.push(`${question.settings.maxFileCount} 件まで`);
    }
    if (question.settings.maxFileSizeBytes !== undefined) {
      const megabytes = Math.floor(question.settings.maxFileSizeBytes / (1024 * 1024));
      parts.push(megabytes > 0 ? `1 件 ${megabytes} MB まで` : `1 件 ${question.settings.maxFileSizeBytes} バイトまで`);
    }
    return parts.join(' / ');
  });
</script>

<!-- 説明文ブロックは回答を持たない -->
{#if question.type === 'Note'}
  <section class="note">
    <h3>{text(question.title)}</h3>
    {#if question.description}<p>{text(question.description)}</p>{/if}
  </section>
{:else}
  <fieldset class="field" class:has-error={error !== undefined}>
    <legend id={labelId}>
      {text(question.title)}
      {#if question.isRequired}<span class="required" aria-label="必須">*</span>{/if}
    </legend>

    {#if question.description}
      <p class="description">{text(question.description)}</p>
    {/if}

    {#if question.type === 'Text'}
      <input
        type="text"
        aria-labelledby={labelId}
        aria-describedby={error ? errorId : undefined}
        aria-invalid={error !== undefined}
        maxlength={question.settings.maxLength}
        placeholder={text(question.settings.placeholder)}
        value={current.values[0] ?? ''}
        oninput={(event) => setSingle(event.currentTarget.value)}
      />
    {:else if question.type === 'Paragraph'}
      <textarea
        rows="4"
        aria-labelledby={labelId}
        aria-describedby={error ? errorId : undefined}
        aria-invalid={error !== undefined}
        maxlength={question.settings.maxLength}
        placeholder={text(question.settings.placeholder)}
        value={current.values[0] ?? ''}
        oninput={(event) => setSingle(event.currentTarget.value)}
      ></textarea>
    {:else if question.type === 'Radio'}
      {#each question.choices as choice (choice.value)}
        <label class="choice">
          <input
            type="radio"
            name={question.questionId}
            value={choice.value}
            checked={current.values.includes(choice.value)}
            onchange={() => setSingle(choice.value)}
          />
          <span>{text(choice.label)}</span>
        </label>
      {/each}
    {:else if question.type === 'Checkbox'}
      {#each question.choices as choice (choice.value)}
        <label class="choice">
          <input
            type="checkbox"
            value={choice.value}
            checked={current.values.includes(choice.value)}
            onchange={(event) => toggleMultiple(choice.value, event.currentTarget.checked)}
          />
          <span>{text(choice.label)}</span>
        </label>
      {/each}
    {:else if question.type === 'Dropdown'}
      <select
        aria-labelledby={labelId}
        aria-invalid={error !== undefined}
        value={current.values[0] ?? ''}
        onchange={(event) => setSingle(event.currentTarget.value)}
      >
        <option value="">選択してください</option>
        {#each question.choices as choice (choice.value)}
          <option value={choice.value}>{text(choice.label)}</option>
        {/each}
      </select>
    {:else if question.type === 'Scale'}
      <div class="scale" role="radiogroup" aria-labelledby={labelId}>
        {#if question.settings.scaleMinimumLabel}
          <span class="scale-label">{text(question.settings.scaleMinimumLabel)}</span>
        {/if}
        {#each scaleValues as value (value)}
          <label class="scale-item">
            <input
              type="radio"
              name={question.questionId}
              value={String(value)}
              checked={current.values.includes(String(value))}
              onchange={() => setSingle(String(value))}
            />
            <span>{value}</span>
          </label>
        {/each}
        {#if question.settings.scaleMaximumLabel}
          <span class="scale-label">{text(question.settings.scaleMaximumLabel)}</span>
        {/if}
      </div>
    {:else if question.type === 'Rating'}
      <div class="rating" role="radiogroup" aria-labelledby={labelId}>
        {#each scaleValues as value (value)}
          <button
            type="button"
            class="star"
            class:filled={Number(current.values[0] ?? '0') >= value}
            aria-label={`${value} / ${scaleValues.at(-1)}`}
            aria-pressed={current.values.includes(String(value))}
            onclick={() => setSingle(String(value))}>★</button
          >
        {/each}
      </div>
    {:else if question.type === 'Date'}
      <input
        type="date"
        aria-labelledby={labelId}
        aria-invalid={error !== undefined}
        value={current.values[0] ?? ''}
        oninput={(event) => setSingle(event.currentTarget.value)}
      />
    {:else if question.type === 'File'}
      <!-- **受け付けるかどうかはサーバが決める。** ここは選びやすさのためだけ -->
      <input
        type="file"
        multiple={(question.settings.maxFileCount ?? 1) > 1}
        aria-labelledby={labelId}
        aria-describedby={error ? errorId : undefined}
        aria-invalid={error !== undefined}
        onchange={(event) => setFiles(Array.from(event.currentTarget.files ?? []))}
      />
      {#if fileLimits !== ''}
        <p class="description">{fileLimits}</p>
      {/if}
      {#if (current.files ?? []).length > 0}
        <ul class="files">
          {#each current.files ?? [] as file (file.name)}
            <li>{file.name}</li>
          {/each}
        </ul>
      {/if}
    {:else if question.type === 'Time'}
      <input
        type="time"
        aria-labelledby={labelId}
        aria-invalid={error !== undefined}
        value={current.values[0] ?? ''}
        oninput={(event) => setSingle(event.currentTarget.value)}
      />
    {:else}
      <p class="unsupported">この設問形式にはまだ対応していません（{question.type}）</p>
    {/if}

    <!-- 「その他」を選んだときだけ自由記述を出す -->
    {#if otherSelected}
      <input
        type="text"
        class="other"
        placeholder="その他の内容"
        aria-label="その他の内容"
        value={current.otherText}
        oninput={(event) => (answer = { ...current, otherText: event.currentTarget.value })}
      />
    {/if}

    {#if error}
      <!-- **読み上げに届く形でエラーを出す** -->
      <p class="error" id={errorId} role="alert">{error}</p>
    {/if}
  </fieldset>
{/if}

<style lang="scss">
  .field {
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 1rem 1.25rem;
    margin: 0 0 1rem;

    &.has-error {
      border-color: var(--error);
    }
  }

  legend {
    font-weight: 600;
    padding: 0 0.25rem;
  }

  .required {
    color: var(--error);
    margin-left: 0.25rem;
  }

  .description {
    color: var(--muted);
    margin: 0.25rem 0 0.75rem;
    font-size: 0.9rem;
  }

  .choice {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0;
    cursor: pointer;
  }

  input[type='text'],
  input[type='date'],
  input[type='time'],
  textarea,
  select {
    width: 100%;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    font: inherit;
    box-sizing: border-box;
  }

  .other {
    margin-top: 0.5rem;
  }

  .files {
    margin: 0.5rem 0 0;
    padding-left: 1.25rem;
    color: var(--muted);
    font-size: 0.9rem;
  }

  .scale {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex-wrap: wrap;
  }

  .scale-item {
    display: flex;
    flex-direction: column;
    align-items: center;
    cursor: pointer;
  }

  .scale-label {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .rating {
    display: flex;
    gap: 0.25rem;
  }

  .star {
    background: none;
    border: none;
    font-size: 1.75rem;
    line-height: 1;
    color: var(--border);
    cursor: pointer;

    &.filled {
      color: var(--accent);
    }
  }

  .note {
    margin: 0 0 1rem;

    h3 {
      margin: 0 0 0.25rem;
    }
  }

  .error {
    color: var(--error);
    margin: 0.5rem 0 0;
    font-size: 0.9rem;
  }

  .unsupported {
    color: var(--muted);
  }
</style>
