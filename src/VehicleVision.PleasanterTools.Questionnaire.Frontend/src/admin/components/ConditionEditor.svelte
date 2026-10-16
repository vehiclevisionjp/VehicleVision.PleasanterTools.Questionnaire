<script lang="ts">
  import {
    conditionOperatorKey,
    conditionOperators,
    displayText,
    needsConditionValue,
    picksFromChoices,
    type ConditionMatch,
    type ConditionOperator,
    type ConditionRule,
    type Question,
    type VisibilityCondition,
  } from '../lib/types';
  import { isUnknownChoiceValue } from '../lib/flow';
  import type { Language } from '../../lib/i18n/language';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    title: string;
    condition?: VisibilityCondition | null;
    priorQuestions: Question[];
    editing: Language;
    onchange: (condition: VisibilityCondition | null) => void;
  }

  let { title, condition, priorQuestions, editing, onchange }: Props = $props();
  const rules = $derived(condition?.rules ?? []);
  const match = $derived(condition?.match ?? 'All');

  function update(next: { match?: ConditionMatch; rules?: ConditionRule[] }) {
    const nextRules = next.rules ?? rules;
    onchange(nextRules.length === 0 ? null : { match: next.match ?? match, rules: nextRules });
  }

  function firstValue(target: Question): string {
    return target.type === 'Confirm' ? 'true' : (target.choices[0]?.value ?? '');
  }

  function addRule() {
    const first = priorQuestions[0];
    if (!first) return;
    update({
      rules: [...rules, { questionId: first.questionId, operator: 'Equals', value: firstValue(first) }],
    });
  }

  function patchRule(index: number, patch: Partial<ConditionRule>) {
    update({ rules: rules.map((rule, i) => (i === index ? { ...rule, ...patch } : rule)) });
  }

  function referenced(rule: ConditionRule): Question | undefined {
    return priorQuestions.find((question) => question.questionId === rule.questionId);
  }

  function changeQuestion(index: number, questionId: string) {
    const rule = rules[index];
    const target = priorQuestions.find((question) => question.questionId === questionId);
    if (!rule || !target) return;
    patchRule(index, {
      questionId,
      value: picksFromChoices(rule.operator) ? firstValue(target) : rule.value,
    });
  }

  function changeOperator(index: number, operator: ConditionOperator) {
    const rule = rules[index];
    if (!rule) return;
    if (!needsConditionValue(operator)) {
      patchRule(index, { operator, value: null });
      return;
    }
    const target = referenced(rule);
    const selectValue =
      picksFromChoices(operator) &&
      (target?.type === 'Confirm' || (target?.choices.length ?? 0) > 0);
    patchRule(index, {
      operator,
      value:
        selectValue && target && isUnknownChoiceValue(target, operator, rule.value)
          ? firstValue(target)
          : (rule.value ?? ''),
    });
  }

  function label(question: Question): string {
    return displayText(question.title, editing) || question.questionId;
  }
</script>

<div class="condition">
  <div class="head">
    <span class="title">{title}</span>
    {#if rules.length > 1}
      <label>
        {t('condition.match')}
        <select
          value={match}
          onchange={(event) => update({ match: event.currentTarget.value as ConditionMatch })}
        >
          <option value="All">{t('condition.matchAll')}</option>
          <option value="Any">{t('condition.matchAny')}</option>
        </select>
      </label>
    {/if}
  </div>

  {#if priorQuestions.length === 0}
    <p class="warn">{t('condition.noEarlierQuestion')}</p>
  {:else if rules.length === 0}
    <p class="hint">{t('condition.none')}</p>
  {/if}

  {#each rules as rule, index (index)}
    {@const target = referenced(rule)}
    {@const unknown = target ? isUnknownChoiceValue(target, rule.operator, rule.value) : false}
    <div class="rule">
      <select value={rule.questionId} onchange={(event) => changeQuestion(index, event.currentTarget.value)}>
        {#if !target}
          <option value={rule.questionId}>
            {t('condition.invalidReference', { questionId: rule.questionId })}
          </option>
        {/if}
        {#each priorQuestions as prior (prior.questionId)}
          <option value={prior.questionId}>{label(prior)}</option>
        {/each}
      </select>
      <select
        value={rule.operator}
        onchange={(event) => changeOperator(index, event.currentTarget.value as ConditionOperator)}
      >
        {#each conditionOperators as operator (operator)}
          <option value={operator}>{t(conditionOperatorKey(operator))}</option>
        {/each}
      </select>
      {#if needsConditionValue(rule.operator)}
        {#if target?.type === 'Confirm' && picksFromChoices(rule.operator)}
          <select value={rule.value ?? 'true'} onchange={(event) => patchRule(index, { value: event.currentTarget.value })}>
            <option value="true">{t('condition.confirmed')}</option>
            <option value="false">{t('condition.notConfirmed')}</option>
          </select>
        {:else if target && picksFromChoices(rule.operator) && target.choices.length > 0}
          <select value={rule.value ?? ''} onchange={(event) => patchRule(index, { value: event.currentTarget.value })}>
            {#if unknown}
              <option value={rule.value ?? ''}>{t('condition.staleValue', { value: rule.value ?? '' })}</option>
            {/if}
            {#each target.choices as choice (choice.value)}
              <option value={choice.value}>{displayText(choice.label, editing) || choice.value}</option>
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
        onclick={() => update({ rules: rules.filter((_, i) => i !== index) })}
        aria-label={t('condition.remove')}
      >×</button>
    </div>
    {#if unknown}<p class="warn">{t('condition.unknownChoice')}</p>{/if}
  {/each}

  {#if priorQuestions.length > 0}
    <button type="button" class="secondary small" onclick={addRule}>{t('condition.add')}</button>
    <p class="hint">{t('condition.earlierOnly')}</p>
  {/if}
</div>

<style lang="scss">
  .condition { margin-top: .75rem; padding-top: .75rem; border-top: 1px dashed var(--border); }
  .head { display: flex; align-items: center; justify-content: space-between; gap: .75rem; }
  .title { font-weight: 600; }
  .rule { display: grid; grid-template-columns: minmax(8rem, 1fr) auto minmax(7rem, 1fr) auto; gap: .4rem; margin: .5rem 0; }
  .rule select, .rule input { min-width: 0; }
  .hint { color: var(--muted); font-size: .8rem; margin: .35rem 0; }
  .warn { color: var(--error); font-size: .8rem; margin: .35rem 0; }
  @media (max-width: 42rem) { .rule { grid-template-columns: 1fr; } }
</style>
