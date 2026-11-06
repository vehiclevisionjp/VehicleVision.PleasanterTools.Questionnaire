import type { LookupPublicDefinition } from '../../lib/lookup';
import type { MessageKey } from './i18n/messages';
import type {
  LookupColumnKind,
  LookupDefinition,
  LookupInspectColumn,
  LookupPublishProblem,
  Page,
  Question,
  SurveyDefinition,
} from './types';

/**
 * ルックアップ設定画面の部品（Issue #880・仕様書 3.2・8 章）。
 *
 * **ここは体験のための補助。** 何を許すか・公開してよいかを決めるのはサーバ側
 * （`LookupValidator`・`LookupColumnValidator`・`LookupPublishValidator`）。
 */

/** キー項目の最大数（N）。サーバの `LookupLimits.MaxKeys` と同じ。 */
export const MAX_KEYS = 5;
/** 入力先項目の最大数（M）。 */
export const MAX_TARGETS = 10;
/** 最大候補数の既定とシステム上限。 */
export const DEFAULT_MAX_RESULTS = 10;
export const SYSTEM_MAX_RESULTS = 20;

/** 入力先にできる設問の形式（複数選択・日付などは対象外）。 */
const TARGET_TYPES = new Set(['Text', 'Radio', 'Dropdown', 'Scale']);

function pageOf(definition: SurveyDefinition, questionId: string): Page | undefined {
  return definition.pages.find((page) =>
    page.questions.some((question) => question.questionId === questionId),
  );
}

/** 同じページにある、キーにできる設問（1 行テキストだけ）。起点が無ければ空。 */
export function keyQuestionOptions(
  definition: SurveyDefinition,
  anchorQuestionId: string,
): Question[] {
  return (
    pageOf(definition, anchorQuestionId)?.questions.filter((question) => question.type === 'Text') ??
    []
  );
}

/** 同じページにある、入力先にできる設問。 */
export function targetQuestionOptions(
  definition: SurveyDefinition,
  anchorQuestionId: string,
): Question[] {
  return (
    pageOf(definition, anchorQuestionId)?.questions.filter((question) =>
      TARGET_TYPES.has(question.type),
    ) ?? []
  );
}

/** ボタンの起点にできる設問と、まだ使われていないもの。 */
export function freeAnchorQuestions(definition: SurveyDefinition): Question[] {
  const used = new Set((definition.lookups ?? []).map((lookup) => lookup.anchorQuestionId));
  return definition.pages
    .flatMap((page) => page.questions)
    .filter((question) => !used.has(question.questionId) && question.type === 'Text');
}

function randomSuffix(): string {
  const bytes = new Uint8Array(4);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

/** 新しいルックアップ。**検索 ID は自動採番。** */
export function newLookup(anchorQuestionId: string): LookupDefinition {
  return {
    lookupId: `lookup-${randomSuffix()}`,
    anchorQuestionId,
    buttonLabel: null,
    sourceKind: 'Pleasanter',
    sourceSiteId: null,
    credentialId: null,
    keys: [],
    targets: [],
    allowedColumns: [],
    displayFields: [],
    maxResults: DEFAULT_MAX_RESULTS,
  };
}

/** 表示 ID の形（英字で始まる英数字・下線、最大 32 文字）。サーバの検証と同じ。 */
export function isValidDisplayId(id: string): boolean {
  return /^[A-Za-z][A-Za-z0-9_]{0,31}$/.test(id);
}

/**
 * 次の表示 ID。**自動採番で、画面からは編集させない**（参照元の列名と無関係な名前にするため。Issue #835）。
 * 既存の中で使われていない `field1`、`field2`… の最小のもの。
 */
export function nextDisplayId(existing: readonly string[]): string {
  const used = new Set(existing);
  for (let number = 1; ; number += 1) {
    const candidate = `field${number}`;
    if (!used.has(candidate)) {
      return candidate;
    }
  }
}

/**
 * 返却を許可する列。**入力元列とモーダルの表示列の和集合だけ**（最小権限。余計な列を足さない）。
 * 並びは出現順で、重複と空は除く。
 */
export function deriveAllowedColumns(lookup: LookupDefinition): string[] {
  const names = [
    ...lookup.targets.map((target) => target.sourceColumn),
    ...(lookup.displayFields ?? []).map((field) => field.sourceColumn),
  ].filter((name) => name.trim() !== '');
  return Array.from(new Set(names));
}

/** 許可列を入力元・表示列から作り直した定義。 */
export function withDerivedAllowedColumns(lookup: LookupDefinition): LookupDefinition {
  return { ...lookup, allowedColumns: deriveAllowedColumns(lookup) };
}

/** キーにできる列か（選択肢のない文字列列、または整数だけの数値列）。サーバの検証と同じ見立て。 */
export function isKeyColumn(column: LookupInspectColumn): boolean {
  if (column.hasChoices) {
    return false;
  }
  return column.kind === 'Text' || (column.kind === 'Number' && column.decimalPlaces === 0);
}

/** キーの列の種類として定義へ固定する値。キーにできない種類は `null`。 */
export function keyKindOf(column: LookupInspectColumn | undefined): LookupColumnKind | null {
  return column && isKeyColumn(column) ? column.kind : null;
}

/** 入力先の設問の形式に合う列か。 */
export function isCompatibleTarget(questionType: string, column: LookupInspectColumn): boolean {
  switch (questionType) {
    case 'Text':
      return column.kind === 'Text' || column.kind === 'Number';
    case 'Radio':
    case 'Dropdown':
      return column.kind === 'Text';
    case 'Scale':
      return column.kind === 'Number';
    default:
      return false;
  }
}

/** 選択肢に出す列の名前。取れた列 → 現在使っている名前の順で、重複は 1 つにする。 */
export function columnNames(
  columns: readonly LookupInspectColumn[],
  inUse: readonly string[],
): string[] {
  return Array.from(new Set([...columns.map((column) => column.name), ...inUse.filter((n) => n !== '')]));
}

/** 公開前の不備の符号に対応する文言の鍵。**知らない符号は汎用の文言。** */
export function problemMessageKey(problem: LookupPublishProblem): MessageKey {
  const key = `lookupAdmin.problem.${problem.code}` as MessageKey;
  return PROBLEM_KEYS.has(key) ? key : 'lookupAdmin.problem.unknown';
}

const PROBLEM_KEYS = new Set<string>([
  'lookupAdmin.problem.LookupIdInvalid',
  'lookupAdmin.problem.SourceKindUnsupported',
  'lookupAdmin.problem.SourceSiteInvalid',
  'lookupAdmin.problem.CredentialMissing',
  'lookupAdmin.problem.AnchorNotFound',
  'lookupAdmin.problem.AnchorDuplicated',
  'lookupAdmin.problem.KeyCountInvalid',
  'lookupAdmin.problem.KeyQuestionNotFound',
  'lookupAdmin.problem.KeyQuestionTypeUnsupported',
  'lookupAdmin.problem.KeyDuplicated',
  'lookupAdmin.problem.KeyPageMismatch',
  'lookupAdmin.problem.PrefixNotAllowed',
  'lookupAdmin.problem.TargetCountInvalid',
  'lookupAdmin.problem.TargetQuestionNotFound',
  'lookupAdmin.problem.TargetQuestionTypeUnsupported',
  'lookupAdmin.problem.TargetConflict',
  'lookupAdmin.problem.TargetPageMismatch',
  'lookupAdmin.problem.AllowedColumnsInvalid',
  'lookupAdmin.problem.TargetColumnNotAllowed',
  'lookupAdmin.problem.DisplayColumnNotAllowed',
  'lookupAdmin.problem.DisplayIdInvalid',
  'lookupAdmin.problem.DisplayIdDuplicated',
  'lookupAdmin.problem.DisplayColumnsLegacy',
  'lookupAdmin.problem.MaxResultsInvalid',
  'lookupAdmin.problem.KeyColumnKindInvalid',
  'lookupAdmin.problem.CredentialNotFound',
  'lookupAdmin.problem.CredentialDisabled',
  'lookupAdmin.problem.SourceUnavailable',
  'lookupAdmin.problem.SourceSiteIntegrated',
  'lookupAdmin.problem.SourceSiteInheritsPermission',
  'lookupAdmin.problem.ColumnNotFound',
  'lookupAdmin.problem.KeyColumnTypeUnsupported',
  'lookupAdmin.problem.KeyColumnKindMismatch',
  'lookupAdmin.problem.PrefixColumnNotText',
  'lookupAdmin.problem.NumberColumnNotNullable',
  'lookupAdmin.problem.TargetColumnTypeIncompatible',
]);

/** 接続試験の失敗の分類に対応する文言の鍵。**知らない分類は汎用の文言。** */
export function inspectFailureKey(failure: string | null | undefined): MessageKey {
  switch (failure) {
    case 'Unauthorized':
      return 'lookupAdmin.failure.unauthorized';
    case 'Forbidden':
      return 'lookupAdmin.failure.forbidden';
    case 'NotFound':
      return 'lookupAdmin.failure.notFound';
    case 'CredentialMissing':
    case 'CredentialDisabled':
    case 'CredentialUndecryptable':
      return 'lookupAdmin.failure.credential';
    case 'InsecureTransport':
    case 'InvalidDefinition':
      return 'lookupAdmin.failure.config';
    case 'ConnectionFailed':
    case 'RateLimited':
    case 'ServerError':
    case 'Timeout':
    case 'TemporarilyUnavailable':
      return 'lookupAdmin.failure.unreachable';
    default:
      return 'lookupAdmin.failure.other';
  }
}

/**
 * 管理プレビュー用の公開 DTO。
 *
 * **回答画面の公開 DTO（サーバが `LookupPublicDefinition` で作る）とは別に、管理画面が下書きから作る**
 * （仕様書 U9。共通化しない）。ここから参照元・資格情報・列名は持ち出さない（操作に要る情報だけ）。
 */
export function toPreviewDefinition(lookup: LookupDefinition): LookupPublicDefinition {
  return {
    lookupId: lookup.lookupId,
    anchorQuestionId: lookup.anchorQuestionId,
    buttonLabel: lookup.buttonLabel ?? null,
    keyQuestionIds: lookup.keys.map((key) => key.questionId),
    targetQuestionIds: lookup.targets.map((target) => target.questionId),
    displayFields: (lookup.displayFields ?? []).map((field) => ({
      displayId: field.displayId,
      heading: field.heading ?? null,
    })),
  };
}

/** 下書きの定義からプレビュー用の一覧を作る。**起点・キー・入力先がそろったものだけ。** */
export function previewDefinitions(definition: SurveyDefinition): LookupPublicDefinition[] {
  const known = new Set(definition.pages.flatMap((page) => page.questions.map((q) => q.questionId)));
  return (definition.lookups ?? [])
    .map(toPreviewDefinition)
    .filter(
      (lookup) =>
        lookup.keyQuestionIds.length > 0 &&
        lookup.targetQuestionIds.length > 0 &&
        [lookup.anchorQuestionId, ...lookup.keyQuestionIds, ...lookup.targetQuestionIds].every((id) =>
          known.has(id),
        ),
    );
}
