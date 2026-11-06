<script lang="ts">
  import { onMount } from 'svelte';
  import {
    createLookupCredential,
    deleteLookupCredential,
    inspectLookupSource,
    loadLookupCredentials,
    loadLookupProblems,
    rotateLookupCredential,
    setLookupCredentialEnabled,
  } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import {
    columnNames,
    freeAnchorQuestions,
    inspectFailureKey,
    isCompatibleTarget,
    keyKindOf,
    keyQuestionOptions,
    MAX_KEYS,
    MAX_TARGETS,
    newLookup,
    nextDisplayId,
    problemMessageKey,
    SYSTEM_MAX_RESULTS,
    targetQuestionOptions,
    unusedCredentials,
    withDerivedAllowedColumns,
  } from '../lib/lookupAdmin';
  import {
    text,
    withText,
    type LookupColumnKind,
    type LookupCredentialStatus,
    type LookupDefinition,
    type LookupInspectResult,
    type LookupPublishProblem,
    type SurveyDefinition,
  } from '../lib/types';
  import type { Language } from '../../lib/i18n/language';

  /**
   * ルックアップ設定（Issue #880・仕様書 8 章）。
   *
   * **「割り当て」（回答 → 保存先の列）とは別物。** ここは「参照元の検索結果 → 入力先の設問」。
   * **保存は編集画面がまとめて行う**（`DraftRevision` の競合検知に含まれる）。
   * ただし専用 API キーは別で、登録・失効などはその場でサーバへ送る（定義には入らない）。
   *
   * **API キーは入力欄へ書いて送るだけ。** 登録後は入力欄を空にし、再表示・コピーは提供しない。
   * 許可列は入力元列と表示列の和集合から自動で作る（余計な列を返さない）。
   * 表示 ID は自動採番で、編集できない（参照元の列名を回答者へ見せないため）。
   */
  interface Props {
    surveyId: string;
    definition: SurveyDefinition;
    /** 入力欄が書き込む言語。 */
    editing: Language;
    /** 設定・API キー・接続試験をしてよい相手か（`lookups.manage`）。 */
    canManage: boolean;
    /** 下書きの版。保存で変わったら、資格情報の「参照されているか」を読み直す。 */
    savedRevision?: number;
    onchange: (next: LookupDefinition[]) => void;
  }

  let { surveyId, definition, editing, canManage, savedRevision, onchange }: Props = $props();

  const lookups = $derived(definition.lookups ?? []);
  const allQuestions = $derived(definition.pages.flatMap((page) => page.questions));

  let credentials = $state<LookupCredentialStatus[]>([]);
  let loadError = $state('');
  let inspections = $state<Record<string, LookupInspectResult | undefined>>({});
  /** 入力中の API キー。**送ったら空にする。保存・再表示しない。** */
  let apiKeys = $state<Record<string, string>>({});
  let busy = $state<Record<string, boolean>>({});
  let notices = $state<Record<string, string>>({});
  let problems = $state<LookupPublishProblem[]>([]);
  let problemsChecked = $state(false);
  let problemsError = $state('');

  async function reloadCredentials() {
    const result = await loadLookupCredentials(surveyId);
    if (result.ok) {
      credentials = result.value;
      loadError = '';
    } else {
      loadError = result.message;
    }
  }

  onMount(reloadCredentials);

  // 下書きを保存すると、サーバー側の「参照されているか」が変わる（再読み込みで孤立したものを正しく出す）
  let lastRevision: number | undefined;
  let revisionSeen = false;
  $effect(() => {
    const current = savedRevision;
    if (!revisionSeen) {
      revisionSeen = true;
      lastRevision = current;
      return;
    }
    if (current !== lastRevision) {
      lastRevision = current;
      void reloadCredentials();
    }
  });

  /** 未使用の資格情報（Issue #892）。保存済みの定義にも、編集中の定義にも参照されていないもの。 */
  const unused = $derived(unusedCredentials(credentials, lookups));
  let reusePick = $state<Record<string, string>>({});
  let unusedNotice = $state('');
  let unusedBusy = $state(false);

  function reuse(lookup: LookupDefinition) {
    const id = reusePick[lookup.lookupId] ?? '';
    if (id === '' || !unused.some((item) => item.credentialId === id)) {
      return;
    }
    replace(lookup.lookupId, (item) => ({ ...item, credentialId: id }));
    reusePick = { ...reusePick, [lookup.lookupId]: '' };
    setNotice(lookup.lookupId, t('lookupAdmin.credentialReused'));
  }

  async function removeUnused(credential: LookupCredentialStatus) {
    if (!window.confirm(t('lookupAdmin.unusedDeleteConfirm'))) {
      return;
    }

    unusedBusy = true;
    unusedNotice = '';
    const result = await deleteLookupCredential(surveyId, credential.credentialId);
    unusedBusy = false;
    if (!result.ok) {
      unusedNotice = result.message;
      return;
    }

    credentials = credentials.filter((item) => item.credentialId !== credential.credentialId);
    unusedNotice = t('lookupAdmin.credentialDeleted');
  }

  function questionTitle(questionId: string): string {
    const question = allQuestions.find((item) => item.questionId === questionId);
    return (question && text(question.title, editing)) || questionId;
  }

  function replace(lookupId: string, change: (lookup: LookupDefinition) => LookupDefinition) {
    onchange(
      lookups.map((lookup) =>
        lookup.lookupId === lookupId ? withDerivedAllowedColumns(change(lookup)) : lookup,
      ),
    );
  }

  function add(anchorQuestionId: string) {
    if (anchorQuestionId === '') {
      return;
    }
    onchange([...lookups, newLookup(anchorQuestionId)]);
  }

  function remove(lookupId: string) {
    onchange(lookups.filter((lookup) => lookup.lookupId !== lookupId));
  }

  function statusOf(lookup: LookupDefinition): LookupCredentialStatus | undefined {
    return credentials.find((credential) => credential.credentialId === lookup.credentialId);
  }

  function setBusy(lookupId: string, value: boolean) {
    busy = { ...busy, [lookupId]: value };
  }

  function setNotice(lookupId: string, message: string) {
    notices = { ...notices, [lookupId]: message };
  }

  function upsertCredential(status: LookupCredentialStatus) {
    credentials = [...credentials.filter((item) => item.credentialId !== status.credentialId), status];
  }

  /** 登録（まだ無いとき）・再入力（あるとき）。**送ったら入力欄を空にする。** */
  async function saveKey(lookup: LookupDefinition) {
    const apiKey = apiKeys[lookup.lookupId] ?? '';
    if (apiKey.trim() === '') {
      return;
    }

    setBusy(lookup.lookupId, true);
    setNotice(lookup.lookupId, '');
    const existing = statusOf(lookup);
    const result = existing
      ? await rotateLookupCredential(surveyId, existing.credentialId, apiKey)
      : await createLookupCredential(surveyId, apiKey);
    apiKeys = { ...apiKeys, [lookup.lookupId]: '' };
    setBusy(lookup.lookupId, false);

    if (!result.ok) {
      setNotice(lookup.lookupId, result.message);
      return;
    }

    upsertCredential(result.value);
    if (!existing) {
      replace(lookup.lookupId, (item) => ({ ...item, credentialId: result.value.credentialId }));
    }
    setNotice(lookup.lookupId, t('lookupAdmin.credentialSaved'));
  }

  async function toggleEnabled(lookup: LookupDefinition, enabled: boolean) {
    const status = statusOf(lookup);
    if (!status) {
      return;
    }

    setBusy(lookup.lookupId, true);
    const result = await setLookupCredentialEnabled(surveyId, status.credentialId, enabled);
    setBusy(lookup.lookupId, false);
    if (result.ok) {
      upsertCredential(result.value);
      setNotice(lookup.lookupId, t(enabled ? 'lookupAdmin.credentialResumed' : 'lookupAdmin.credentialRevoked'));
    } else {
      setNotice(lookup.lookupId, result.message);
    }
  }

  async function removeKey(lookup: LookupDefinition) {
    const status = statusOf(lookup);
    if (!status || !window.confirm(t('lookupAdmin.credentialDeleteConfirm'))) {
      return;
    }

    setBusy(lookup.lookupId, true);
    const result = await deleteLookupCredential(surveyId, status.credentialId);
    setBusy(lookup.lookupId, false);
    if (!result.ok) {
      setNotice(lookup.lookupId, result.message);
      return;
    }

    credentials = credentials.filter((item) => item.credentialId !== status.credentialId);
    replace(lookup.lookupId, (item) => ({ ...item, credentialId: null }));
    setNotice(lookup.lookupId, t('lookupAdmin.credentialDeleted'));
  }

  async function inspect(lookup: LookupDefinition) {
    const status = statusOf(lookup);
    if (!status || !lookup.sourceSiteId) {
      setNotice(lookup.lookupId, t('lookupAdmin.inspectNeedsKeyAndSite'));
      return;
    }

    setBusy(lookup.lookupId, true);
    setNotice(lookup.lookupId, '');
    const result = await inspectLookupSource(surveyId, status.credentialId, lookup.sourceSiteId);
    setBusy(lookup.lookupId, false);
    if (!result.ok) {
      setNotice(lookup.lookupId, result.message);
      return;
    }

    inspections = { ...inspections, [lookup.lookupId]: result.value };
  }

  async function checkProblems() {
    problemsError = '';
    const result = await loadLookupProblems(surveyId);
    if (result.ok) {
      problems = result.value;
      problemsChecked = true;
    } else {
      problemsError = result.message;
    }
  }

  function columnsOf(lookup: LookupDefinition) {
    return inspections[lookup.lookupId]?.columns ?? [];
  }

  function setKey(lookup: LookupDefinition, index: number, patch: Partial<LookupDefinition['keys'][number]>) {
    replace(lookup.lookupId, (item) => ({
      ...item,
      keys: item.keys.map((key, position) => (position === index ? { ...key, ...patch } : key)),
    }));
  }

  function pickKeyColumn(lookup: LookupDefinition, index: number, name: string) {
    const column = columnsOf(lookup).find((item) => item.name === name);
    const kind = keyKindOf(column);
    setKey(lookup, index, {
      sourceColumn: name,
      ...(kind ? { sourceColumnKind: kind } : {}),
    });
  }

  function setPrefix(lookup: LookupDefinition, index: number, allowed: boolean) {
    setKey(lookup, index, { allowPrefix: allowed, matchMode: allowed ? 'Prefix' : 'Exact' });
  }

  function setTarget(lookup: LookupDefinition, index: number, patch: Partial<LookupDefinition['targets'][number]>) {
    replace(lookup.lookupId, (item) => ({
      ...item,
      targets: item.targets.map((target, position) => (position === index ? { ...target, ...patch } : target)),
    }));
  }

  function setDisplay(
    lookup: LookupDefinition,
    index: number,
    patch: Partial<NonNullable<LookupDefinition['displayFields']>[number]>,
  ) {
    replace(lookup.lookupId, (item) => ({
      ...item,
      displayFields: (item.displayFields ?? []).map((field, position) =>
        position === index ? { ...field, ...patch } : field,
      ),
    }));
  }

  function incompatible(lookup: LookupDefinition, index: number): boolean {
    const target = lookup.targets[index];
    if (!target) {
      return false;
    }
    const column = columnsOf(lookup).find((item) => item.name === target.sourceColumn);
    const question = allQuestions.find((item) => item.questionId === target.questionId);
    return !!column && !!question && !isCompatibleTarget(question.type, column);
  }

  function numberNotNullable(lookup: LookupDefinition, name: string): boolean {
    const column = columnsOf(lookup).find((item) => item.name === name);
    return column?.kind === 'Number' && !column.nullable;
  }

  const anchors = $derived(freeAnchorQuestions(definition));
  let newAnchor = $state('');
  const KINDS: LookupColumnKind[] = ['Text', 'Number'];
</script>

<section class="lookups">
  <h2>{t('lookupAdmin.title')}</h2>
  <p class="hint">{t('lookupAdmin.intro')}</p>
  <p class="warning">{t('lookupAdmin.publicDataWarning')}</p>
  <p class="warning">{t('lookupAdmin.realDataWarning')}</p>
  {#if !canManage}
    <p class="hint">{t('lookupAdmin.readOnly')}</p>
  {/if}
  {#if loadError}<p class="warning" role="alert">{loadError}</p>{/if}

  {#each lookups as lookup (lookup.lookupId)}
    {@const status = statusOf(lookup)}
    {@const inspection = inspections[lookup.lookupId]}
    {@const names = columnNames(columnsOf(lookup), [])}
    <fieldset class="lookup" disabled={!canManage}>
      <legend>
        {t('lookupAdmin.lookupHeading', { question: questionTitle(lookup.anchorQuestionId) })}
      </legend>

      <label>
        {t('lookupAdmin.buttonLabel')}
        <input
          type="text"
          value={text(lookup.buttonLabel ?? undefined, editing)}
          oninput={(event) =>
            replace(lookup.lookupId, (item) => ({
              ...item,
              buttonLabel: withText(item.buttonLabel ?? undefined, event.currentTarget.value, editing),
            }))}
        />
      </label>
      <p class="hint">{t('lookupAdmin.buttonLabelHint')}</p>

      <label>
        {t('lookupAdmin.sourceSite')}
        <input
          type="number"
          min="1"
          value={lookup.sourceSiteId ?? ''}
          oninput={(event) =>
            replace(lookup.lookupId, (item) => ({
              ...item,
              sourceSiteId: Number(event.currentTarget.value) > 0 ? Number(event.currentTarget.value) : null,
            }))}
        />
      </label>

      <!-- ---- 秘密 UI：値は出さない。登録後は伏せる ---- -->
      <div class="credential">
        <h3>{t('lookupAdmin.credential')}</h3>
        {#if status}
          <p class="status" role="status">
            {status.isEnabled ? t('lookupAdmin.credentialConfigured') : t('lookupAdmin.credentialRevokedState')}
            <span class="hint">{t('lookupAdmin.credentialUpdated', { at: status.updatedAt })}</span>
          </p>
        {:else}
          <p class="hint">{t('lookupAdmin.credentialNone')}</p>
        {/if}
        <label>
          {status ? t('lookupAdmin.credentialReenter') : t('lookupAdmin.credentialInput')}
          <input
            type="password"
            autocomplete="off"
            spellcheck="false"
            value={apiKeys[lookup.lookupId] ?? ''}
            oninput={(event) => (apiKeys = { ...apiKeys, [lookup.lookupId]: event.currentTarget.value })}
          />
        </label>
        {#if !status && unused.length > 0}
          <label>
            {t('lookupAdmin.credentialReuse')}
            <select bind:value={reusePick[lookup.lookupId]} data-testid="credential-reuse-select">
              <option value="">{t('lookupAdmin.credentialReusePick')}</option>
              {#each unused as item (item.credentialId)}
                <option value={item.credentialId}>
                  {t('lookupAdmin.unusedItem', {
                    id: item.credentialId.slice(0, 8),
                    state: item.isEnabled ? t('lookupAdmin.credentialConfigured') : t('lookupAdmin.credentialRevokedState'),
                  })} - {item.updatedAt}
                </option>
              {/each}
            </select>
          </label>
          <button
            type="button"
            class="secondary"
            disabled={busy[lookup.lookupId] || (reusePick[lookup.lookupId] ?? '') === ''}
            onclick={() => reuse(lookup)}
          >
            {t('lookupAdmin.credentialReuseButton')}
          </button>
        {/if}
        <p class="hint">{t('lookupAdmin.credentialHint')}</p>
        <div class="row">
          <button
            type="button"
            disabled={busy[lookup.lookupId] || (apiKeys[lookup.lookupId] ?? '').trim() === ''}
            onclick={() => saveKey(lookup)}
          >
            {status ? t('lookupAdmin.credentialRotate') : t('lookupAdmin.credentialRegister')}
          </button>
          {#if status}
            <button
              type="button"
              class="secondary"
              disabled={busy[lookup.lookupId]}
              onclick={() => toggleEnabled(lookup, !status.isEnabled)}
            >
              {status.isEnabled ? t('lookupAdmin.credentialRevoke') : t('lookupAdmin.credentialResume')}
            </button>
            <button type="button" class="secondary" disabled={busy[lookup.lookupId]} onclick={() => removeKey(lookup)}>
              {t('lookupAdmin.credentialDelete')}
            </button>
          {/if}
        </div>
        <p class="hint">{t('lookupAdmin.credentialDuplicateHint')}</p>
        {#if notices[lookup.lookupId]}<p class="hint" role="status">{notices[lookup.lookupId]}</p>{/if}
      </div>

      <!-- ---- 接続試験 ---- -->
      <div class="inspect">
        <button type="button" class="secondary" disabled={busy[lookup.lookupId]} onclick={() => inspect(lookup)}>
          {t('lookupAdmin.test')}
        </button>
        {#if inspection}
          <div role="status">
            {#if inspection.ok}
              <p class="hint">{t('lookupAdmin.testOk', { count: inspection.columns.length })}</p>
              {#if inspection.isIntegrated}<p class="warning">{t('lookupAdmin.siteIntegrated')}</p>{/if}
              {#if inspection.inheritsPermission}<p class="warning">{t('lookupAdmin.siteInherits')}</p>{/if}
            {:else}
              <p class="warning">{t(inspectFailureKey(inspection.failure))}</p>
            {/if}
          </div>
        {/if}
      </div>

      <!-- ---- N 個のキー ---- -->
      <h3>{t('lookupAdmin.keys')}</h3>
      {#each lookup.keys as key, index (index)}
        <div class="grid">
          <label>
            {t('lookupAdmin.keyQuestion')}
            <select
              value={key.questionId}
              onchange={(event) => setKey(lookup, index, { questionId: event.currentTarget.value })}
            >
              <option value="">{t('lookupAdmin.choose')}</option>
              {#each keyQuestionOptions(definition, lookup.anchorQuestionId) as question (question.questionId)}
                <option value={question.questionId}>{text(question.title, editing) || question.questionId}</option>
              {/each}
            </select>
          </label>
          <label>
            {t('lookupAdmin.sourceColumn')}
            {#if names.length > 0}
              <select value={key.sourceColumn} onchange={(event) => pickKeyColumn(lookup, index, event.currentTarget.value)}>
                <option value="">{t('lookupAdmin.choose')}</option>
                {#each columnNames(columnsOf(lookup), [key.sourceColumn]) as name (name)}
                  <option value={name}>{name}</option>
                {/each}
              </select>
            {:else}
              <input
                type="text"
                value={key.sourceColumn}
                oninput={(event) => setKey(lookup, index, { sourceColumn: event.currentTarget.value })}
              />
            {/if}
          </label>
          <label>
            {t('lookupAdmin.columnKind')}
            <select
              value={key.sourceColumnKind ?? ''}
              onchange={(event) =>
                setKey(lookup, index, {
                  sourceColumnKind: (event.currentTarget.value || null) as LookupColumnKind | null,
                })}
            >
              <option value="">{t('lookupAdmin.choose')}</option>
              {#each KINDS as kind (kind)}
                <option value={kind}>{kind === 'Text' ? t('lookupAdmin.kindText') : t('lookupAdmin.kindNumber')}</option>
              {/each}
            </select>
          </label>
          <label class="inline">
            <input
              type="checkbox"
              checked={key.allowPrefix ?? false}
              disabled={key.sourceColumnKind !== 'Text'}
              onchange={(event) => setPrefix(lookup, index, event.currentTarget.checked)}
            />
            {t('lookupAdmin.prefix')}
          </label>
          <button
            type="button"
            class="secondary"
            onclick={() =>
              replace(lookup.lookupId, (item) => ({ ...item, keys: item.keys.filter((_, position) => position !== index) }))}
          >
            {t('lookupAdmin.remove')}
          </button>
        </div>
      {/each}
      <button
        type="button"
        class="secondary"
        disabled={lookup.keys.length >= MAX_KEYS}
        onclick={() =>
          replace(lookup.lookupId, (item) => ({
            ...item,
            keys: [...item.keys, { questionId: '', sourceColumn: '', sourceColumnKind: null, matchMode: 'Exact', allowPrefix: false }],
          }))}
      >
        {t('lookupAdmin.addKey', { max: MAX_KEYS })}
      </button>
      <p class="hint">{t('lookupAdmin.prefixHint')}</p>

      <!-- ---- M 個の入力先 ---- -->
      <h3>{t('lookupAdmin.targets')}</h3>
      {#each lookup.targets as target, index (index)}
        <div class="grid">
          <label>
            {t('lookupAdmin.targetColumn')}
            {#if names.length > 0}
              <select value={target.sourceColumn} onchange={(event) => setTarget(lookup, index, { sourceColumn: event.currentTarget.value })}>
                <option value="">{t('lookupAdmin.choose')}</option>
                {#each columnNames(columnsOf(lookup), [target.sourceColumn]) as name (name)}
                  <option value={name}>{name}</option>
                {/each}
              </select>
            {:else}
              <input
                type="text"
                value={target.sourceColumn}
                oninput={(event) => setTarget(lookup, index, { sourceColumn: event.currentTarget.value })}
              />
            {/if}
          </label>
          <label>
            {t('lookupAdmin.targetQuestion')}
            <select value={target.questionId} onchange={(event) => setTarget(lookup, index, { questionId: event.currentTarget.value })}>
              <option value="">{t('lookupAdmin.choose')}</option>
              {#each targetQuestionOptions(definition, lookup.anchorQuestionId) as question (question.questionId)}
                <option value={question.questionId}>{text(question.title, editing) || question.questionId}</option>
              {/each}
            </select>
          </label>
          <button
            type="button"
            class="secondary"
            onclick={() =>
              replace(lookup.lookupId, (item) => ({ ...item, targets: item.targets.filter((_, position) => position !== index) }))}
          >
            {t('lookupAdmin.remove')}
          </button>
          {#if incompatible(lookup, index)}<p class="warning">{t('lookupAdmin.targetIncompatible')}</p>{/if}
          {#if numberNotNullable(lookup, target.sourceColumn)}<p class="warning">{t('lookupAdmin.numberNotNullable')}</p>{/if}
        </div>
      {/each}
      <button
        type="button"
        class="secondary"
        disabled={lookup.targets.length >= MAX_TARGETS}
        onclick={() =>
          replace(lookup.lookupId, (item) => ({ ...item, targets: [...item.targets, { sourceColumn: '', questionId: '' }] }))}
      >
        {t('lookupAdmin.addTarget', { max: MAX_TARGETS })}
      </button>
      <p class="hint">{t('lookupAdmin.partialHint')}</p>

      <!-- ---- モーダルの表示列（表示 ID は固定） ---- -->
      <h3>{t('lookupAdmin.displayFields')}</h3>
      {#each lookup.displayFields ?? [] as field, index (field.displayId)}
        <div class="grid">
          <label>
            {t('lookupAdmin.displayColumn')}
            {#if names.length > 0}
              <select value={field.sourceColumn} onchange={(event) => setDisplay(lookup, index, { sourceColumn: event.currentTarget.value })}>
                <option value="">{t('lookupAdmin.choose')}</option>
                {#each columnNames(columnsOf(lookup), [field.sourceColumn]) as name (name)}
                  <option value={name}>{name}</option>
                {/each}
              </select>
            {:else}
              <input
                type="text"
                value={field.sourceColumn}
                oninput={(event) => setDisplay(lookup, index, { sourceColumn: event.currentTarget.value })}
              />
            {/if}
          </label>
          <label>
            {t('lookupAdmin.displayId')}
            <input type="text" value={field.displayId} readonly />
          </label>
          <label>
            {t('lookupAdmin.heading')}
            <input
              type="text"
              value={text(field.heading ?? undefined, editing)}
              oninput={(event) =>
                setDisplay(lookup, index, { heading: withText(field.heading ?? undefined, event.currentTarget.value, editing) })}
            />
          </label>
          <button
            type="button"
            class="secondary"
            onclick={() =>
              replace(lookup.lookupId, (item) => ({
                ...item,
                displayFields: (item.displayFields ?? []).filter((_, position) => position !== index),
              }))}
          >
            {t('lookupAdmin.remove')}
          </button>
        </div>
      {/each}
      <button
        type="button"
        class="secondary"
        onclick={() =>
          replace(lookup.lookupId, (item) => ({
            ...item,
            displayFields: [
              ...(item.displayFields ?? []),
              { displayId: nextDisplayId((item.displayFields ?? []).map((field) => field.displayId)), sourceColumn: '', heading: null },
            ],
          }))}
      >
        {t('lookupAdmin.addDisplay')}
      </button>
      <p class="hint">{t('lookupAdmin.displayHint')}</p>

      <h3>{t('lookupAdmin.allowedColumns')}</h3>
      <p class="hint">{t('lookupAdmin.allowedHint')}</p>
      <p><code>{lookup.allowedColumns.join(', ') || '—'}</code></p>

      <label>
        {t('lookupAdmin.maxResults')}
        <input
          type="number"
          min="1"
          max={SYSTEM_MAX_RESULTS}
          value={lookup.maxResults ?? 10}
          oninput={(event) =>
            replace(lookup.lookupId, (item) => ({ ...item, maxResults: Number(event.currentTarget.value) || 10 }))}
        />
      </label>
      <p class="hint">{t('lookupAdmin.maxResultsHint', { max: SYSTEM_MAX_RESULTS })}</p>

      <button type="button" class="secondary danger" onclick={() => remove(lookup.lookupId)}>
        {t('lookupAdmin.removeLookup')}
      </button>
    </fieldset>
  {/each}

  {#if canManage}
    <div class="row add">
      <label>
        {t('lookupAdmin.anchor')}
        <select bind:value={newAnchor}>
          <option value="">{t('lookupAdmin.choose')}</option>
          {#each anchors as question (question.questionId)}
            <option value={question.questionId}>{text(question.title, editing) || question.questionId}</option>
          {/each}
        </select>
      </label>
      <button
        type="button"
        disabled={newAnchor === ''}
        onclick={() => {
          add(newAnchor);
          newAnchor = '';
        }}
      >
        {t('lookupAdmin.add')}
      </button>
    </div>
  {/if}

  {#if canManage && unused.length > 0}
    <div class="unused" data-testid="unused-credentials">
      <h3>{t('lookupAdmin.unusedTitle')}</h3>
      <p class="hint">{t('lookupAdmin.unusedHint')}</p>
      <ul>
        {#each unused as item (item.credentialId)}
          <li class="row">
            <span>
              {t('lookupAdmin.unusedItem', {
                id: item.credentialId.slice(0, 8),
                state: item.isEnabled ? t('lookupAdmin.credentialConfigured') : t('lookupAdmin.credentialRevokedState'),
              })}
              <span class="hint">{t('lookupAdmin.credentialUpdated', { at: item.updatedAt })}</span>
            </span>
            <button type="button" class="secondary" disabled={unusedBusy} onclick={() => removeUnused(item)}>
              {t('lookupAdmin.unusedDelete')}
            </button>
          </li>
        {/each}
      </ul>
      {#if unusedNotice}<p class="hint" role="status">{unusedNotice}</p>{/if}
    </div>
  {:else if unusedNotice}
    <p class="hint" role="status">{unusedNotice}</p>
  {/if}

  <div class="check">
    <button type="button" class="secondary" onclick={checkProblems}>{t('lookupAdmin.checkProblems')}</button>
    <p class="hint">{t('lookupAdmin.checkProblemsHint')}</p>
    {#if problemsError}<p class="warning" role="alert">{problemsError}</p>{/if}
    {#if problemsChecked}
      <div role="status">
        {#if problems.length === 0}
          <p class="hint">{t('lookupAdmin.noProblems')}</p>
        {:else}
          <ul>
            {#each problems as problem, index (index)}
              <li class="warning">
                {problem.lookupId ?? ''}: {t(problemMessageKey(problem))}
                {#if problem.detail}<code>{problem.detail}</code>{/if}
              </li>
            {/each}
          </ul>
        {/if}
      </div>
    {/if}
  </div>
</section>

<style lang="scss">
  .lookups {
    margin-block: var(--space-6);
    padding: var(--space-4);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
  }

  h2 {
    margin-block: 0 var(--space-2);
    font-size: 1.1rem;
  }

  h3 {
    margin-block: var(--space-4) var(--space-2);
    font-size: 1rem;
  }

  fieldset.lookup {
    margin-block: var(--space-4);
    padding: var(--space-3);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
  }

  label {
    display: block;
    margin-block: var(--space-2);
    &.inline {
      display: flex;
      gap: var(--space-2);
      align-items: center;
    }
  }

  input[type='text'],
  input[type='number'],
  input[type='password'],
  select {
    display: block;
    width: 100%;
    margin-block-start: var(--space-1);
    padding: 0.4rem var(--space-2);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-sm);
    font: inherit;
    box-sizing: border-box;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
    gap: var(--space-2);
    align-items: end;
    margin-block: var(--space-2);
    padding-block-end: var(--space-2);
    border-bottom: 1px dashed var(--border);
  }

  .row {
    display: flex;
    flex-wrap: wrap;
    gap: var(--space-2);
    align-items: end;
  }

  .hint {
    margin-block: var(--space-1);
    color: var(--muted);
    font-size: 0.85rem;
  }

  .warning {
    margin-block: var(--space-2);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    background: var(--warning-surface);
    color: var(--warning-text);
    font-size: 0.85rem;
  }

  ul {
    padding: 0;
    list-style: none;
  }

  .credential,
  .inspect,
  .unused,
  .check {
    margin-block-start: var(--space-3);
  }
</style>
