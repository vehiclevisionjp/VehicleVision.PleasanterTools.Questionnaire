<script lang="ts">
  import {
    conditionOperatorKey,
    conditionOperators,
    displayText,
    hasChoices,
    hasRows,
    isDisplayOnly,
    needsConditionValue,
    picksFromChoices,
    questionTypeKey,
    questionTypes,
    text,
    withText,
    type ConditionMatch,
    type ConditionOperator,
    type ConditionRule,
    type Question,
  } from '../lib/types';
  import {
    canCarryTransitions,
    hasChoiceTransitions,
    isUnknownChoiceValue,
    staleTargetId,
    toTransition,
    transitionValue,
  } from '../lib/flow';
  import type { Language } from '../../lib/i18n/language';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    question: Question;
    /** 入力欄が書き込む言語。**管理画面の表示言語とは別。** */
    editing: Language;
    /** 割り当て先の列。**無いと「Pleasanter に残らない」ことが分からない** */
    mappedColumns: string[];
    canMoveUp: boolean;
    canMoveDown: boolean;
    /**
     * 飛び先に選べるページ。**このページより後ろだけ**（Issue #44）。
     *
     * **前を向いた行き先は一覧に出さない。** 選ばせてから公開で弾くより、
     * 選べないほうが早い（無限に回るアンケートを作れてしまう）。
     */
    jumpTargets: { pageId: string; label: string }[];
    /** 表示条件で参照できる設問。**この設問より前だけ**（前方参照は選ばせない） */
    priorQuestions: Question[];
    /**
     * 同じページで既に行き先を持っている、別の設問の文言。
     *
     * **2 つ目を付けさせないため。** どちらの行き先が勝つのかを利用者が決められない。
     */
    branchTakenBy: string | null;
    onchange: (question: Question) => void;
    onremove: () => void;
    onmove: (direction: -1 | 1) => void;
  }

  let {
    question,
    editing,
    mappedColumns,
    canMoveUp,
    canMoveDown,
    jumpTargets,
    priorQuestions,
    branchTakenBy,
    onchange,
    onremove,
    onmove,
  }: Props = $props();

  const showChoices = $derived(hasChoices(question.type));

  /** 行を編集させるか（Issue #74）。**列（選択肢）とは別の欄。** */
  const showRows = $derived(hasRows(question.type));
  const rows = $derived(question.settings.rows ?? []);
  const displayOnly = $derived(isDisplayOnly(question.type));

  /** 選択肢に行き先を置ける設問か。**1 つだけ選ぶ設問だけ。** */
  const canBranch = $derived(canCarryTransitions(question.type));

  /** 行き先の欄を触らせないか。**同じページの別の設問が既に持っている。** */
  const branchLocked = $derived(branchTakenBy !== null && !hasChoiceTransitions(question));

  /** 飛び先に出せるページ ID。**一覧から外れた飛び先を見つけるのに使う。** */
  const targetIds = $derived(jumpTargets.map((target) => target.pageId));

  const rules = $derived(question.visibleWhen?.rules ?? []);
  const match = $derived(question.visibleWhen?.match ?? 'All');

  /** 選べる数を指定できる設問か（Issue #101）。**複数選ぶチェックボックスだけ。** */
  const showSelectionRange = $derived(
    question.type === 'Checkbox' || question.type === 'CheckboxGrid',
  );

  /** 選べる数の指定が矛盾していないか。**公開時にサーバ側でも同じことを見る。** */
  const selectionProblem = $derived.by(() => {
    if (!showSelectionRange) return null;

    const minimum = question.settings.minSelections;
    const maximum = question.settings.maxSelections;

    if ((minimum !== undefined && minimum < 1) || (maximum !== undefined && maximum < 1)) {
      return t('question.selectionNotPositive');
    }
    if (minimum !== undefined && maximum !== undefined && minimum > maximum) {
      return t('question.selectionReversed');
    }
    if (minimum !== undefined && minimum > question.choices.length) {
      return t('question.selectionExceedsChoices', { choices: question.choices.length });
    }
    return null;
  });

  function update(patch: Partial<Question>) {
    onchange({ ...question, ...patch });
  }

  /**
   * 数の欄を読む（Issue #101）。
   *
   * **空欄は「指定なし」。** 0 や NaN を入れると、答えようのない設問になる。
   */
  function optionalCount(value: string): number | undefined {
    if (value.trim() === '') return undefined;
    const count = Number(value);
    return Number.isFinite(count) ? count : undefined;
  }

  function addChoice() {
    const next = question.choices.length + 1;
    update({
      choices: [
        ...question.choices,
        {
          value: `choice-${next}`,
          // **最初の文言は編集中の言語へ入れる。** 他の言語は空のまま
          label: withText(undefined, t('question.defaultChoiceLabel', { number: next }), editing),
        },
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

  // ---- グリッドの行（Issue #74）----------------------------------------------

  function updateRows(next: { rowId: string; label: Record<string, string> }[]) {
    update({ settings: { ...question.settings, rows: next } });
  }

  function addRow() {
    const number = rows.length + 1;
    updateRows([
      ...rows,
      {
        rowId: `row-${number}`,
        // **最初の文言は編集中の言語へ入れる。** 他の言語は空のまま
        label: withText(undefined, t('question.defaultRowLabel', { number }), editing),
      },
    ]);
  }

  function updateRow(index: number, patch: Partial<(typeof rows)[number]>) {
    updateRows(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  }

  function removeRow(index: number) {
    updateRows(rows.filter((_, i) => i !== index));
  }

  /**
   * 行を 1 つ動かす。
   *
   * **並びが画面の並びそのもの。** 後から行を足したときに、
   * 消して作り直さずに済むようにする（作り直すと識別子が変わり、
   * マッピングの指す先が消える）。
   */
  function moveRow(index: number, offset: number) {
    const target = index + offset;
    if (target < 0 || target >= rows.length) return;

    const next = [...rows];
    const [moved] = next.splice(index, 1);
    if (moved === undefined) return;

    next.splice(target, 0, moved);
    updateRows(next);
  }

  /**
   * NPS のかたちを一発で入れる（Issue #74）。
   *
   * **新しい形式にはしない。** NPS は「0〜10 で聞いて、両端に文言を置いた尺度」で、
   * 形式を増やすと集計も画面も分岐が 1 本増えるだけになる。
   *
   * **今ある文言は上書きしない。** 押し間違いで書いたものを消さない。
   */
  function applyNpsPreset() {
    update({
      settings: {
        ...question.settings,
        scaleMinimum: 0,
        scaleMaximum: 10,
        scaleMinimumLabel:
          text(question.settings.scaleMinimumLabel, editing) === ''
            ? withText(question.settings.scaleMinimumLabel, t('question.npsMinimum'), editing)
            : question.settings.scaleMinimumLabel,
        scaleMaximumLabel:
          text(question.settings.scaleMaximumLabel, editing) === ''
            ? withText(question.settings.scaleMaximumLabel, t('question.npsMaximum'), editing)
            : question.settings.scaleMaximumLabel,
      },
    });
  }

  // ---- 表示条件 -------------------------------------------------------------

  /**
   * 表示条件を書き換える。
   *
   * **条件が 0 件になったら器ごと外す。** 空の器を残すと、
   * 保存した JSON に「条件あり」の跡が残って読み手を惑わせる。
   */
  function updateCondition(next: { match?: ConditionMatch; rules?: ConditionRule[] }) {
    const nextRules = next.rules ?? rules;

    update({
      visibleWhen:
        nextRules.length === 0 ? null : { match: next.match ?? match, rules: nextRules },
    });
  }

  function addRule() {
    const first = priorQuestions[0];
    if (!first) return;

    updateCondition({
      rules: [...rules, { questionId: first.questionId, operator: 'Equals', value: firstValue(first) }],
    });
  }

  function patchRule(index: number, patch: Partial<ConditionRule>) {
    updateCondition({ rules: rules.map((rule, i) => (i === index ? { ...rule, ...patch } : rule)) });
  }

  function removeRule(index: number) {
    updateCondition({ rules: rules.filter((_, i) => i !== index) });
  }

  /** その条件が見に行く設問。**一覧に無ければ `undefined`。** */
  function referenced(rule: ConditionRule): Question | undefined {
    return priorQuestions.find((prior) => prior.questionId === rule.questionId);
  }

  /** 最初の選択肢の値。選択肢を持たない設問では空。 */
  function firstValue(target: Question): string {
    return target.choices[0]?.value ?? '';
  }

  /**
   * 条件が見に行く設問を替える。
   *
   * **値も選び直す。** 前の設問の選択肢が残ると、成立しない条件になる。
   */
  function changeRuleQuestion(index: number, questionId: string) {
    const rule = rules[index];
    const target = priorQuestions.find((prior) => prior.questionId === questionId);
    if (!rule || !target) return;

    patchRule(index, {
      questionId,
      value: picksFromChoices(rule.operator) ? firstValue(target) : rule.value,
    });
  }

  /** 条件の比べ方を替える。**値が要らない比べ方にしたら値も落とす。** */
  function changeRuleOperator(index: number, operator: ConditionOperator) {
    const rule = rules[index];
    if (!rule) return;

    if (!needsConditionValue(operator)) {
      patchRule(index, { operator, value: null });
      return;
    }

    const target = referenced(rule);
    const pickFromChoices = picksFromChoices(operator) && (target?.choices.length ?? 0) > 0;

    patchRule(index, {
      operator,
      value:
        pickFromChoices && target && isUnknownChoiceValue(target, operator, rule.value ?? '')
          ? firstValue(target)
          : (rule.value ?? ''),
    });
  }

  /** 設問の見出し。**回答画面で出る文字列と同じ見え方にする。** */
  function questionLabel(target: Question): string {
    return displayText(target.title, editing) || target.questionId;
  }
</script>

<article class="question">
  <div class="head">
    <input
      class="title"
      type="text"
      placeholder={t('question.titlePlaceholder')}
      value={text(question.title, editing)}
      oninput={(event) =>
        update({ title: withText(question.title, event.currentTarget.value, editing) })}
    />

    <select
      value={question.type}
      onchange={(event) => {
        const type = event.currentTarget.value as Question['type'];
        // **形式を変えたら要らない設定は落とす。** 残ると画面と食い違う
        update({
          type,
          // **行き先を置けない形式にしたら行き先も落とす。**
          // 残すと画面に出ないまま公開で弾かれる（`TransitionOnUnsupportedQuestion`）
          choices: hasChoices(type)
            ? canCarryTransitions(type)
              ? question.choices
              : question.choices.map((choice) => ({ ...choice, next: null }))
            : [],
          isRequired: isDisplayOnly(type) ? false : question.isRequired,
          // **行を持たない形式にしたら行も落とす**（Issue #74）。
          // 残すと、画面に出ないのにマッピングの指す先だけが生き続ける
          settings: hasRows(type)
            ? question.settings
            : { ...question.settings, rows: undefined },
        });
      }}
    >
      {#each questionTypes as option (option)}
        <option value={option}>{t(questionTypeKey(option))}</option>
      {/each}
    </select>

    <div class="actions">
      <button
        type="button"
        class="icon"
        disabled={!canMoveUp}
        onclick={() => onmove(-1)}
        aria-label={t('question.moveUp')}
      >
        ↑
      </button>
      <button
        type="button"
        class="icon"
        disabled={!canMoveDown}
        onclick={() => onmove(1)}
        aria-label={t('question.moveDown')}>↓</button
      >
      <button type="button" class="icon danger" onclick={onremove} aria-label={t('question.remove')}>
        ×
      </button>
    </div>
  </div>

  <input
    class="description"
    type="text"
    placeholder={t('question.descriptionPlaceholder')}
    value={text(question.description, editing)}
    oninput={(event) =>
      update({ description: withText(question.description, event.currentTarget.value, editing) })}
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
        {t('question.required')}
      </label>
    {/if}

    {#if displayOnly}
      <span class="note">{t('question.displayOnly')}</span>
    {:else if mappedColumns.length === 0}
      <!-- **拒否はしないが伝える。** 気付かずに公開すると回答が Pleasanter に残らない -->
      <span class="unmapped">{t('question.unmapped')}</span>
    {:else}
      <span class="mapped">{t('question.mapped', { columns: mappedColumns.join(' / ') })}</span>
    {/if}
  </div>

  {#if showRows}
    <!--
      **行と列を分けて出す。** グリッドは「行×選択肢」で、
      どちらがどちらか分からなくなると、作った人の意図と逆の表ができる
    -->
    <div class="rows">
      <p class="section">{t('question.rowsTitle')}</p>

      {#each rows as row, index (index)}
        <div class="row-line">
          <input
            type="text"
            class="row-label"
            placeholder={t('question.rowLabelPlaceholder')}
            value={text(row.label, editing)}
            oninput={(event) =>
              updateRow(index, { label: withText(row.label, event.currentTarget.value, editing) })}
          />
          <input
            type="text"
            class="row-id"
            placeholder={t('question.rowIdPlaceholder')}
            value={row.rowId}
            oninput={(event) => updateRow(index, { rowId: event.currentTarget.value })}
          />
          <button
            type="button"
            class="icon"
            disabled={index === 0}
            onclick={() => moveRow(index, -1)}
            aria-label={t('question.moveRowUp')}>↑</button
          >
          <button
            type="button"
            class="icon"
            disabled={index === rows.length - 1}
            onclick={() => moveRow(index, 1)}
            aria-label={t('question.moveRowDown')}>↓</button
          >
          <button
            type="button"
            class="icon danger"
            onclick={() => removeRow(index)}
            aria-label={t('question.removeRow')}>×</button
          >
        </div>
      {/each}

      <button type="button" class="secondary small" onclick={addRow}>
        {t('question.addRow')}
      </button>

      <!-- **行を増やすほど Pleasanter の列を食う。** 作る前に分かるようにする -->
      <p class="hint">{t('question.rowHint')}</p>

      {#if rows.length === 0}
        <p class="warn">{t('question.rowsEmpty')}</p>
      {/if}
    </div>
  {/if}

  {#if showChoices}
    <div class="choices">
      {#if showRows}
        <!-- **選択肢が列になる。** 行の欄と取り違えさせない -->
        <p class="section">{t('question.gridColumnsTitle')}</p>
      {:else if question.type === 'Ranking'}
        <p class="section">{t('question.rankingItemsTitle')}</p>
      {/if}
      {#each question.choices as choice, index (index)}
        {@const stale = staleTargetId(choice.next, targetIds)}
        <div class="choice-block">
          <div class="choice">
            <input
              type="text"
              class="choice-label"
              placeholder={t('question.choiceLabelPlaceholder')}
              value={text(choice.label, editing)}
              oninput={(event) =>
                updateChoice(index, {
                  label: withText(choice.label, event.currentTarget.value, editing),
                })}
            />
            <input
              type="text"
              class="choice-value"
              placeholder={t('question.choiceValuePlaceholder')}
              value={choice.value}
              oninput={(event) => updateChoice(index, { value: event.currentTarget.value })}
            />
            <label class="inline">
              <input
                type="checkbox"
                checked={choice.isOther === true}
                onchange={(event) => updateChoice(index, { isOther: event.currentTarget.checked })}
              />
              {t('question.choiceIsOther')}
            </label>
            <button
              type="button"
              class="icon danger"
              onclick={() => removeChoice(index)}
              aria-label={t('question.removeChoice')}
            >
              ×
            </button>
          </div>

          {#if canBranch}
            <!-- **選べるのは後ろのページだけ。** 前を向いた行き先は一覧に出さない -->
            <label class="choice-next">
              {t('branching.choiceNext')}
              <select
                disabled={branchLocked}
                value={transitionValue(choice.next)}
                onchange={(event) =>
                  updateChoice(index, { next: toTransition(event.currentTarget.value) })}
              >
                <option value="">{t('branching.followPage')}</option>
                <option value="Next">{t('branching.toNextPage')}</option>
                {#each jumpTargets as target, targetIndex (targetIndex)}
                  <option value={`page:${target.pageId}`}>{target.label}</option>
                {/each}
                <option value="Submit">{t('branching.toSubmit')}</option>
                {#if stale !== null}
                  <!-- **今は選べない飛び先も出す。** 黙って別の行き先に変えない -->
                  <option value={`page:${stale}`}>
                    {t('branching.staleTarget', { pageId: stale })}
                  </option>
                {/if}
              </select>
            </label>
          {/if}
        </div>
      {/each}

      <button type="button" class="secondary small" onclick={addChoice}>
        {t('question.addChoice')}
      </button>

      <!-- **保存される値が Pleasanter の列に入る。** 画面の文字列ではない -->
      <p class="hint">{t('question.choiceHint')}</p>

      {#if canBranch}
        {#if branchLocked}
          <!-- **1 ページに行き先を持てる設問は 1 つだけ。** 2 つ目は付けさせない -->
          <p class="warn">{t('branching.lockedByOther', { question: branchTakenBy ?? '' })}</p>
        {:else if jumpTargets.length === 0}
          <p class="hint">{t('branching.noLaterPage')}</p>
        {:else}
          <!-- **前のページが一覧に無い理由を書いておく** -->
          <p class="hint">{t('branching.backwardHint')}</p>
        {/if}
      {:else if hasChoiceTransitions(question)}
        <!-- **複数選べる設問では、どの選択肢の行き先を使うのか決まらない** -->
        <p class="warn">{t('branching.unsupportedType')}</p>
      {/if}
    </div>
  {/if}

  {#if question.type === 'Scale' || question.type === 'Rating'}
    <div class="range">
      <label>
        {t('question.scaleMinimum')}
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
        {t('question.scaleMaximum')}
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

    {#if question.type === 'Scale'}
      <div class="range">
        <label>
          {t('question.scaleMinimumLabel')}
          <input
            type="text"
            placeholder={t('question.scaleEndPlaceholder')}
            value={text(question.settings.scaleMinimumLabel, editing)}
            oninput={(event) =>
              update({
                settings: {
                  ...question.settings,
                  scaleMinimumLabel: withText(
                    question.settings.scaleMinimumLabel,
                    event.currentTarget.value,
                    editing,
                  ),
                },
              })}
          />
        </label>
        <label>
          {t('question.scaleMaximumLabel')}
          <input
            type="text"
            placeholder={t('question.scaleEndPlaceholder')}
            value={text(question.settings.scaleMaximumLabel, editing)}
            oninput={(event) =>
              update({
                settings: {
                  ...question.settings,
                  scaleMaximumLabel: withText(
                    question.settings.scaleMaximumLabel,
                    event.currentTarget.value,
                    editing,
                  ),
                },
              })}
          />
        </label>
      </div>

      <!--
        **NPS は形式ではなく尺度の使い方**（Issue #74）。
        形式を増やすと、集計も画面も分岐が 1 本増えるだけで得るものが無い
      -->
      <button type="button" class="secondary small" onclick={applyNpsPreset}>
        {t('question.npsPreset')}
      </button>
      <p class="hint">{t('question.npsHint')}</p>
    {/if}
  {/if}

  <!-- **選べる数の指定**（Issue #101）。空欄は「指定なし」 -->
  {#if showSelectionRange}
    <div class="range">
      <label>
        {t('question.minSelections')}
        <input
          type="number"
          min="1"
          value={question.settings.minSelections ?? ''}
          oninput={(event) =>
            update({
              settings: {
                ...question.settings,
                minSelections: optionalCount(event.currentTarget.value),
              },
            })}
        />
      </label>
      <label>
        {t('question.maxSelections')}
        <input
          type="number"
          min="1"
          value={question.settings.maxSelections ?? ''}
          oninput={(event) =>
            update({
              settings: {
                ...question.settings,
                maxSelections: optionalCount(event.currentTarget.value),
              },
            })}
        />
      </label>
    </div>

    <!-- **下限は未回答には効かない。** 答えさせたいなら必須の方 -->
    <p class="hint">{t('question.selectionHint')}</p>
    {#if selectionProblem !== null}
      <p class="warn">{selectionProblem}</p>
    {/if}
  {/if}

  <!-- **同じページの中で出し分けるのがこちら。** ページを飛ばすのはジャンプ。
       **前に設問が無く条件も無いときは、置き場所ごと出さない** -->
  {#if priorQuestions.length > 0 || rules.length > 0}
    <div class="visibility">
      <div class="visibility-head">
        <span class="section-title">{t('condition.title')}</span>
        {#if rules.length > 1}
          <label class="inline">
            {t('condition.match')}
            <select
              value={match}
              onchange={(event) =>
                updateCondition({ match: event.currentTarget.value as ConditionMatch })}
            >
              <option value="All">{t('condition.matchAll')}</option>
              <option value="Any">{t('condition.matchAny')}</option>
            </select>
          </label>
        {/if}
      </div>

      {#if priorQuestions.length === 0}
        <!-- **設問を動かすと前方参照になる。** 条件は残して直させる -->
        <p class="warn">{t('condition.noEarlierQuestion')}</p>
      {:else if rules.length === 0}
        <p class="hint">{t('condition.none')}</p>
      {/if}

      {#each rules as rule, index (index)}
        {@const target = referenced(rule)}
        {@const unknownChoice = target
          ? isUnknownChoiceValue(target, rule.operator, rule.value)
          : false}
        <div class="rule">
          <select
            value={rule.questionId}
            onchange={(event) => changeRuleQuestion(index, event.currentTarget.value)}
          >
            {#if !target}
              <!-- **前に無い設問を黙って別の設問に付け替えない** -->
              <option value={rule.questionId}>
                {t('condition.invalidReference', { questionId: rule.questionId })}
              </option>
            {/if}
            {#each priorQuestions as prior, priorIndex (priorIndex)}
              <option value={prior.questionId}>{questionLabel(prior)}</option>
            {/each}
          </select>

          <select
            value={rule.operator}
            onchange={(event) =>
              changeRuleOperator(index, event.currentTarget.value as ConditionOperator)}
          >
            {#each conditionOperators as operator (operator)}
              <option value={operator}>{t(conditionOperatorKey(operator))}</option>
            {/each}
          </select>

          {#if needsConditionValue(rule.operator)}
            {#if target && picksFromChoices(rule.operator) && target.choices.length > 0}
              <!-- **無い選択肢を書かせない。** 選択肢から選ばせる -->
              <select
                value={rule.value ?? ''}
                onchange={(event) => patchRule(index, { value: event.currentTarget.value })}
              >
                {#if unknownChoice}
                  <option value={rule.value ?? ''}>
                    {t('condition.staleValue', { value: rule.value ?? '' })}
                  </option>
                {/if}
                {#each target.choices as choice, choiceIndex (choiceIndex)}
                  <option value={choice.value}>
                    {displayText(choice.label, editing) || choice.value}
                  </option>
                {/each}
              </select>
            {:else}
              <input
                type="text"
                placeholder={t('condition.valuePlaceholder')}
                value={rule.value ?? ''}
                oninput={(event) => patchRule(index, { value: event.currentTarget.value })}
              />
            {/if}
          {/if}

          <button
            type="button"
            class="icon danger"
            onclick={() => removeRule(index)}
            aria-label={t('condition.remove')}
          >
            ×
          </button>
        </div>

        {#if unknownChoice}
          <!-- **選択肢を消したときに気付けるように。** 永久に成立しない条件になる -->
          <p class="warn">{t('condition.unknownChoice')}</p>
        {/if}
      {/each}

      {#if priorQuestions.length > 0}
        <button type="button" class="secondary small" onclick={addRule}>
          {t('condition.add')}
        </button>

        <p class="hint">{t('condition.earlierOnly')}</p>
      {/if}
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

  .rows {
    margin-top: 0.75rem;
  }

  .section {
    color: var(--muted);
    font-size: 0.85rem;
    font-weight: 600;
    margin: 0 0 0.35rem;
  }

  .row-line {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 0.35rem;
  }

  .row-label {
    flex: 2 1 12rem;
  }

  .row-id {
    flex: 1 1 8rem;
    font-family: ui-monospace, monospace;
    font-size: 0.85rem;
  }

  .choice-block {
    margin-bottom: 0.4rem;
  }

  .choice {
    display: flex;
    gap: 0.5rem;
    align-items: center;
  }

  .choice-next {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0.25rem 0 0 1rem;
    font-size: 0.8rem;
    color: var(--muted);

    select {
      flex: 1;
      max-width: 22rem;
    }
  }

  .visibility {
    margin-top: 0.75rem;
    padding-top: 0.75rem;
    border-top: 1px dashed var(--border);
  }

  .visibility-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    margin-bottom: 0.4rem;
    font-size: 0.85rem;
  }

  .section-title {
    font-weight: 600;
  }

  .rule {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    margin-bottom: 0.4rem;

    select,
    input[type='text'] {
      flex: 1;
      min-width: 0;
    }
  }

  .warn {
    color: #b54708;
    font-size: 0.8rem;
    margin: 0.25rem 0 0.5rem;
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
