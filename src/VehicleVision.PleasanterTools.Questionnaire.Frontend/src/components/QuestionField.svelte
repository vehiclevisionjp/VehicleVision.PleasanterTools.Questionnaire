<script lang="ts">
  import type { AnswerState, Question } from '../lib/types';
  import { allowsMultiplePerRow, rowValues, text } from '../lib/types';
  import type { Language } from '../lib/i18n/language';
  import { translator } from '../lib/i18n/messages';

  interface Props {
    question: Question;
    /** 画面に出す言語。**設問の文言も画面の文言もこれで決まる。** */
    language: Language;
    /** **まだ初期化されていないことがある。** 辞書からそのまま渡ってくるため */
    answer: AnswerState | undefined;
    error?: string;
  }

  let { question, language, answer = $bindable(), error }: Props = $props();

  const t = $derived(translator(language));

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

  // ---- グリッド（Issue #74）--------------------------------------------------

  const rows = $derived(question.settings.rows ?? []);

  /** その行で選ばれている値。 */
  function row(rowId: string): string[] {
    return rowValues(current, rowId);
  }

  function setRow(rowId: string, values: string[]) {
    answer = { ...current, rows: { ...(current.rows ?? {}), [rowId]: values } };
  }

  function toggleCell(rowId: string, value: string, checked: boolean) {
    if (!allowsMultiplePerRow(question)) {
      // **1 行 1 つ。** 押した値で置き換える
      setRow(rowId, checked ? [value] : []);
      return;
    }

    const values = checked
      ? [...row(rowId), value]
      : row(rowId).filter((existing) => existing !== value);

    setRow(rowId, values);
  }

  // ---- ランキング（Issue #74）------------------------------------------------

  /**
   * その項目の順位。**選ばれていなければ 0。**
   *
   * **1 から数える。** 0 始まりだと画面に出したときに読み違える。
   */
  function rankOf(value: string): number {
    return current.values.indexOf(value) + 1;
  }

  /** 選ぶ・外す。**押した順に並ぶ。** */
  function toggleRank(value: string) {
    answer = {
      ...current,
      values: current.values.includes(value)
        ? current.values.filter((existing) => existing !== value)
        : [...current.values, value],
    };
  }

  /**
   * 順位を 1 つ動かす。
   *
   * **掴んで動かすだけにしない。** キーボードだけの人と読み上げの人が
   * 順位を変えられないと、その設問に答えられなくなる。
   */
  function moveRank(value: string, offset: number) {
    const index = current.values.indexOf(value);
    const target = index + offset;
    if (index < 0 || target < 0 || target >= current.values.length) return;

    // **抜いて差し直す。** 添字への代入だと、
    // 添字で取り出した値が undefined になり得る形になる（noUncheckedIndexedAccess）
    const values = [...current.values];
    const [moved] = values.splice(index, 1);
    if (moved === undefined) return;

    values.splice(target, 0, moved);
    answer = { ...current, values };
  }

  /** 読み上げへ流す、今の順位。**押すたびに変わるので状態として出す。** */
  const rankingStatus = $derived.by(() => {
    if (question.type !== 'Ranking' || current.values.length === 0) return '';

    const labels = new Map(question.choices.map((choice) => [choice.value, choice.label]));

    return current.values
      .map((value, index) =>
        t('question.rankingStatus', {
          label: text(labels.get(value), language),
          rank: index + 1,
          total: current.values.length,
        }),
      )
      .join('. ');
  });

  /** 添付の上限を文字で出す。**選んでから弾かれるより先に伝える。** */
  const fileLimits = $derived.by(() => {
    const parts: string[] = [];
    if (question.settings.maxFileCount !== undefined) {
      parts.push(t('question.fileCountLimit', { count: question.settings.maxFileCount }));
    }
    if (question.settings.maxFileSizeBytes !== undefined) {
      const megabytes = Math.floor(question.settings.maxFileSizeBytes / (1024 * 1024));
      parts.push(
        megabytes > 0
          ? t('question.fileSizeLimitMegabytes', { megabytes })
          : t('question.fileSizeLimitBytes', { bytes: question.settings.maxFileSizeBytes }),
      );
    }
    return parts.join(' / ');
  });
</script>

<!-- 説明文ブロックは回答を持たない -->
{#if question.type === 'Note'}
  <section class="note">
    <h3>{text(question.title, language)}</h3>
    {#if question.description}<p>{text(question.description, language)}</p>{/if}
  </section>
{:else}
  <fieldset class="field" class:has-error={error !== undefined}>
    <legend id={labelId}>
      {text(question.title, language)}
      {#if question.isRequired}
        <span class="required" aria-label={t('question.required')}>*</span>
      {/if}
    </legend>

    {#if question.description}
      <p class="description">{text(question.description, language)}</p>
    {/if}

    {#if question.type === 'Text'}
      <input
        type="text"
        aria-labelledby={labelId}
        aria-describedby={error ? errorId : undefined}
        aria-invalid={error !== undefined}
        maxlength={question.settings.maxLength}
        placeholder={text(question.settings.placeholder, language)}
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
        placeholder={text(question.settings.placeholder, language)}
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
          <span>{text(choice.label, language)}</span>
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
          <span>{text(choice.label, language)}</span>
        </label>
      {/each}
    {:else if question.type === 'Dropdown'}
      <select
        aria-labelledby={labelId}
        aria-invalid={error !== undefined}
        value={current.values[0] ?? ''}
        onchange={(event) => setSingle(event.currentTarget.value)}
      >
        <option value="">{t('question.selectPlaceholder')}</option>
        {#each question.choices as choice (choice.value)}
          <option value={choice.value}>{text(choice.label, language)}</option>
        {/each}
      </select>
    {:else if question.type === 'Scale'}
      <div class="scale" role="radiogroup" aria-labelledby={labelId}>
        {#if question.settings.scaleMinimumLabel}
          <span class="scale-label">{text(question.settings.scaleMinimumLabel, language)}</span>
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
          <span class="scale-label">{text(question.settings.scaleMaximumLabel, language)}</span>
        {/if}
      </div>
    {:else if question.type === 'Rating'}
      <div class="rating" role="radiogroup" aria-labelledby={labelId}>
        {#each scaleValues as value (value)}
          <button
            type="button"
            class="star"
            class:filled={Number(current.values[0] ?? '0') >= value}
            aria-label={t('question.ratingLabel', { value, max: scaleValues.at(-1) ?? value })}
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
    {:else if question.type === 'Grid' || question.type === 'CheckboxGrid'}
      <!--
        **横に長い。** 画面本体を横スクロールさせず、表の中だけ流す。
        **狭い画面では表をやめる**（下の @media で行ごとの塊にする）
      -->
      <div class="grid-scroll">
        <table class="grid">
          <thead>
            <tr>
              <th scope="col">{t('question.gridRowHeader')}</th>
              {#each question.choices as choice (choice.value)}
                <th scope="col">{text(choice.label, language)}</th>
              {/each}
            </tr>
          </thead>
          <tbody>
            {#each rows as gridRow (gridRow.rowId)}
              <tr>
                <th scope="row">{text(gridRow.label, language)}</th>
                {#each question.choices as choice (choice.value)}
                  <td>
                    <!--
                      **見出しだけでは読み上げに足りない。** 表を線形に読むと
                      「どの行のどの列か」が失われるので、入力自体に名前を付ける
                    -->
                    <label class="cell">
                      <input
                        type={allowsMultiplePerRow(question) ? 'checkbox' : 'radio'}
                        name={`${question.questionId}-${gridRow.rowId}`}
                        value={choice.value}
                        checked={row(gridRow.rowId).includes(choice.value)}
                        aria-label={t('question.gridRowLabel', {
                          row: text(gridRow.label, language),
                          choice: text(choice.label, language),
                        })}
                        onchange={(event) =>
                          toggleCell(gridRow.rowId, choice.value, event.currentTarget.checked)}
                      />
                      <!-- **狭い画面でだけ出す。** 表のときは列見出しと重複する -->
                      <span class="cell-label">{text(choice.label, language)}</span>
                    </label>
                  </td>
                {/each}
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {:else if question.type === 'Ranking'}
      <p class="description">{t('question.rankingLead')}</p>

      <ol class="ranking">
        {#each question.choices as choice (choice.value)}
          {@const rank = rankOf(choice.value)}
          <li class="rank-item" class:ranked={rank > 0}>
            <button
              type="button"
              class="rank-toggle"
              aria-pressed={rank > 0}
              onclick={() => toggleRank(choice.value)}
            >
              <span class="rank-badge">
                {rank > 0 ? t('question.rankingRank', { rank }) : t('question.rankingUnranked')}
              </span>
              <span>{text(choice.label, language)}</span>
            </button>

            <!--
              **掴んで動かすだけにしない。** キーボードと読み上げで順位を変えられること。
              選んでいないものには出さない（動かす順位が無い）
            -->
            {#if rank > 0}
              <span class="rank-actions">
                <button
                  type="button"
                  class="rank-move"
                  disabled={rank === 1}
                  aria-label={`${text(choice.label, language)}: ${t('question.rankingUp')}`}
                  onclick={() => moveRank(choice.value, -1)}>↑</button
                >
                <button
                  type="button"
                  class="rank-move"
                  disabled={rank === current.values.length}
                  aria-label={`${text(choice.label, language)}: ${t('question.rankingDown')}`}
                  onclick={() => moveRank(choice.value, 1)}>↓</button
                >
              </span>
            {/if}
          </li>
        {/each}
      </ol>

      <!-- **順位は押すたびに変わる。** 読み上げへ届かないと変わったことが分からない -->
      <p class="visually-hidden" aria-live="polite">{rankingStatus}</p>
    {:else if question.type === 'Time'}
      <input
        type="time"
        aria-labelledby={labelId}
        aria-invalid={error !== undefined}
        value={current.values[0] ?? ''}
        oninput={(event) => setSingle(event.currentTarget.value)}
      />
    {:else}
      <p class="unsupported">{t('question.unsupported', { type: question.type })}</p>
    {/if}

    <!-- 「その他」を選んだときだけ自由記述を出す -->
    {#if otherSelected}
      <input
        type="text"
        class="other"
        placeholder={t('question.otherText')}
        aria-label={t('question.otherText')}
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
    /* **入力欄の地と文字もテーマに追随させる**（Issue #109）。
       ブラウザ既定の白のままだと、地の色を濃くしたときに入力欄だけが浮く */
    background: var(--surface);
    color: var(--text);
  }

  .other {
    margin-top: 0.5rem;
  }

  /* ---- グリッド（Issue #74）------------------------------------------------ */

  /* **画面本体を横スクロールさせない。** 溢れるのは表の中だけ */
  .grid-scroll {
    overflow-x: auto;
  }

  .grid {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
  }

  .grid th,
  .grid td {
    border-bottom: 1px solid var(--border);
    padding: 0.5rem;
    text-align: center;
  }

  .grid thead th {
    color: var(--muted);
    font-weight: 600;
  }

  /* 行の見出しだけ左寄せ。**読む向きが列見出しと違う** */
  .grid tbody th {
    text-align: left;
    font-weight: 500;
  }

  .cell {
    display: block;
    padding: 0.35rem;
    cursor: pointer;
  }

  /* 表として出せている間は、列見出しと重複するので出さない */
  .cell-label {
    display: none;
  }

  /*
    **狭い画面では表をやめる。** 列が 5 つも並ぶと、横に流しても読めない。
    行ごとの塊にして縦に積み、選択肢の名前を各行へ出す
  */
  @media (max-width: 40rem) {
    .grid,
    .grid tbody,
    .grid tr,
    .grid td {
      display: block;
    }

    /* 列見出しは畳んだ形では意味を持たない。**消すのではなく読み上げにだけ残す** */
    .grid thead {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }

    .grid tr {
      border: 1px solid var(--border);
      border-radius: 6px;
      margin-bottom: 0.75rem;
      padding: 0.5rem;
    }

    .grid tbody th {
      display: block;
      border-bottom: none;
      font-weight: 600;
      padding: 0.25rem 0.5rem 0.5rem;
    }

    .grid td {
      border-bottom: none;
      text-align: left;
      padding: 0;
    }

    .cell {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }

    .cell-label {
      display: inline;
    }
  }

  /* ---- ランキング（Issue #74）---------------------------------------------- */

  .ranking {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .rank-item {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    margin-bottom: 0.5rem;
    padding: 0.25rem 0.5rem;
  }

  /* **色だけに頼らない。** 左端の太い線と、順位そのものの文字で分かる */
  .rank-item.ranked {
    border-left: 4px solid var(--accent);
  }

  .rank-toggle {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex: 1;
    background: none;
    border: none;
    padding: 0.5rem;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }

  .rank-badge {
    flex: 0 0 auto;
    min-width: 5rem;
    color: var(--muted);
    font-size: 0.85rem;
  }

  .rank-item.ranked .rank-badge {
    color: var(--accent);
    font-weight: 600;
  }

  .rank-actions {
    display: flex;
    gap: 0.25rem;
  }

  /* **指で押せる大きさにする。** 並べ替えは押し間違えると順位が崩れる */
  .rank-move {
    min-width: 2.5rem;
    min-height: 2.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: none;
    font: inherit;
    cursor: pointer;
  }

  .rank-move:disabled {
    opacity: 0.4;
    cursor: default;
  }

  /* 読み上げにだけ届かせる */
  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
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
