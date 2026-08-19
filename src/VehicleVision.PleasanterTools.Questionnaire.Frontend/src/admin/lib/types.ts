import { DEFAULT_LANGUAGE, type Language } from '../../lib/i18n/language';
import { ja, type MessageKey } from './i18n/messages';

/** 言語コードをキーにした表示文字列。**器は最初から用意する。** */
export type LocalizedText = Record<string, string>;

export type QuestionType =
  | 'Text'
  | 'Paragraph'
  | 'Radio'
  | 'Checkbox'
  | 'Dropdown'
  | 'Scale'
  | 'Rating'
  | 'Date'
  | 'Time'
  | 'File'
  | 'Note';

/**
 * 画面に出す並び。**説明文ブロックは最後**（回答を持たないため）
 *
 * **文言はここに持たない。** 鍵から引く（`_documents/多言語対応方針.md` 4 章）。
 */
export const questionTypes: QuestionType[] = [
  'Text',
  'Paragraph',
  'Radio',
  'Checkbox',
  'Dropdown',
  'Scale',
  'Rating',
  'Date',
  'Time',
  'File',
  'Note',
];

/** 設問の形式の文言の鍵。**形式を足すと鍵が無くなり、型検査で落ちる。** */
export function questionTypeKey(type: QuestionType): MessageKey {
  return `questionType.${type}`;
}

/** 選択肢を持つ形式か。 */
export function hasChoices(type: QuestionType): boolean {
  return type === 'Radio' || type === 'Checkbox' || type === 'Dropdown';
}

/** 回答を持たない表示専用の要素か。 */
export function isDisplayOnly(type: QuestionType): boolean {
  return type === 'Note';
}

export interface Choice {
  value: string;
  label: LocalizedText;
  isOther?: boolean;
}

export interface QuestionSettings {
  maxLength?: number;
  placeholder?: LocalizedText;
  scaleMinimum?: number;
  scaleMaximum?: number;
  scaleMinimumLabel?: LocalizedText;
  scaleMaximumLabel?: LocalizedText;
  maxFileCount?: number;
  maxFileSizeBytes?: number;
}

export interface Question {
  questionId: string;
  type: QuestionType;
  title: LocalizedText;
  description?: LocalizedText;
  isRequired: boolean;
  choices: Choice[];
  settings: QuestionSettings;
}

export interface Page {
  pageId: string;
  title?: LocalizedText;
  description?: LocalizedText;
  questions: Question[];
}

export interface SurveyDefinition {
  surveyId: string;
  version: number;
  title: LocalizedText;
  description?: LocalizedText;
  confirmationMessage?: LocalizedText;
  displayMode: 'Paged' | 'OneQuestionPerPage';
  showProgress: boolean;
  allowEditingAfterSubmit: boolean;
  pages: Page[];
}

export type QuestionPort = 'Value' | 'OtherText' | 'FileNames' | 'Files';

/**
 * 添付列か。
 *
 * **列名の決まりは Pleasanter 側のもの。** 画面では選び分けのためだけに使い、
 * 最終的な判定はサーバ側で行う。
 */
export function isAttachmentColumn(column: string): boolean {
  return /^Attachments([A-Z]|[0-9]{3})$/.test(column);
}

export interface MappingSource {
  questionId: string;
  port: QuestionPort;
}

export interface MappingConverter {
  operation: string;
  config: Record<string, string>;
}

/**
 * Pleasanter の列 1 本への割り当て。
 *
 * **取り得る形は `1:0:1` か `N:1:1` だけ。** 入力が複数なら変換が要る。
 */
export interface ColumnAssignment {
  targetColumn: string;
  sources: MappingSource[];
  converter?: MappingConverter | null;
}

export interface MappingDefinition {
  assignments: ColumnAssignment[];
}

export interface SurveyDraft {
  definition: SurveyDefinition;
  mapping: MappingDefinition;
  /** **保存時に照合する版。** 合わなければ他の人が更新している */
  revision: number;
}

export interface SurveySummary {
  surveyId: string;
  publicId: string;
  title: string;
  pleasanterSiteId: number;
  status: number;
  publishedVersion: number | null;
  updatedAt: string;
}

/** アンケートの状態の文言の鍵。 */
export function surveyStatusKey(status: number): MessageKey {
  switch (status) {
    case 0:
      return 'status.draft';
    case 1:
      return 'status.published';
    case 2:
      return 'status.suspended';
    default:
      return 'status.unknown';
  }
}

export interface MappingProblem {
  code: string;
  targetColumn?: string | null;
  detail?: string | null;
  isBlocking: boolean;
}

/**
 * マッピングの不備の文言の鍵。
 *
 * **サーバ側に符号が増えたときに落とさない。**
 * 鍵が無ければ符号そのものを出す（`null` を返す）。
 */
export function problemKey(code: string): MessageKey | null {
  const key = `problem.${code}`;
  return key in ja ? (key as MessageKey) : null;
}

export interface AdminSession {
  authenticated: boolean;
  setupRequired: boolean;
  loginId?: string | null;
  role?: string | null;
  pending?: boolean;
  pendingLoginId?: string | null;
  /** **途中状態のときだけ意味がある。** 2 要素をまだ登録していない */
  needsEnrollment?: boolean;

  /**
   * 利用者ごとの表示言語。`null` は「まだ選んでいない」。
   *
   * **画面の初期値を決めるためのもの**（`_documents/多言語対応方針.md` 2 章）。
   */
  language?: string | null;
}

/**
 * その言語の文字列を取り出す。**落とさない。**
 *
 * **編集画面では既定の言語へ落とさない。** 落とすと、
 * 英語の欄に日本語が出たまま「入力済み」に見えてしまい、
 * そのまま保存すると日本語が英語として保存される。
 */
export function text(value: LocalizedText | undefined, language: Language): string {
  return value?.[language] ?? '';
}

/**
 * その言語へ書き込む。**器は多言語のまま保つ。**
 *
 * **空にした言語は鍵ごと消す。** 空文字を残すと、回答画面の
 * `LocalizedText.Get` が既定の言語へ落ちずに空文字を返す
 * （`_documents/多言語対応方針.md` 5 章）。
 */
export function withText(
  value: LocalizedText | undefined,
  next: string,
  language: Language,
): LocalizedText {
  const updated = { ...(value ?? {}) };

  if (next === '') {
    delete updated[language];
  } else {
    updated[language] = next;
  }

  return updated;
}

/** 回答画面で実際に出る文字列。**未入力なら既定の言語へ落ちる。** */
export function displayText(value: LocalizedText | undefined, language: Language): string {
  return value?.[language] ?? value?.[DEFAULT_LANGUAGE] ?? '';
}

/**
 * 管理操作の記録 1 件。
 *
 * **本文は入っていない**（`_documents/データモデル設計.md` 2.6）。
 * 残るのは宛先・結果・対象の識別子と、入口が明示して預けた補足だけ。
 *
 * **値が無い項目は `null` ではなく「無い」**。サーバは null のプロパティを
 * 落として返すので（`DefaultIgnoreCondition`）、`!== null` では守れない。
 * **`?` を外すと、型検査は通るのに画面が真っ白になる。**
 */
export interface AuditLogEntry {
  occurredAt: string;
  adminUserId?: string | null;
  /** 操作した管理者の名前。**もう居ない管理者の記録では無い。** */
  adminLoginId?: string | null;
  action: string;
  /** 結果の HTTP 状態。**列を足す前の記録では無い。** */
  statusCode?: number | null;
  targetType?: string | null;
  targetId?: string | null;
  /** 補足の JSON 文字列。**そのまま出さず、必ず逃がして描くこと。** */
  detail?: string | null;
  ipAddress?: string | null;
}

/** 記録の 1 ページ。 */
export interface AuditLogPage {
  entries: AuditLogEntry[];
  /** 次のページがあるか。**総数は数えない**（増え続ける表を毎回数えないため）。 */
  hasMore: boolean;
}

/** 記録の絞り込み。 */
export interface AuditLogFilter {
  failedOnly: boolean;
  action: string;
  from: string;
  to: string;
}
