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

/** 画面に出す並び。**説明文ブロックは最後**（回答を持たないため） */
export const questionTypes: { value: QuestionType; label: string }[] = [
  { value: 'Text', label: '短い文章' },
  { value: 'Paragraph', label: '長い文章' },
  { value: 'Radio', label: '単一選択' },
  { value: 'Checkbox', label: '複数選択' },
  { value: 'Dropdown', label: 'プルダウン' },
  { value: 'Scale', label: '尺度' },
  { value: 'Rating', label: '星' },
  { value: 'Date', label: '日付' },
  { value: 'Time', label: '時刻' },
  { value: 'File', label: '添付' },
  { value: 'Note', label: '説明文（回答なし）' },
];

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

export type QuestionPort = 'Value' | 'OtherText' | 'FileNames';

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

export const surveyStatusLabels: Record<number, string> = {
  0: '下書き',
  1: '公開中',
  2: '停止中',
};

export interface MappingProblem {
  code: string;
  targetColumn?: string | null;
  detail?: string | null;
  isBlocking: boolean;
}

export const problemMessages: Record<string, string> = {
  InvalidShape: '入力が複数あるのに変換がありません（または入力がありません）',
  DuplicateTargetColumn: '同じ列への割り当てが重複しています',
  MissingTargetColumn: '書き込み先の列が指定されていません',
  QuestionNotInDefinition: '存在しない設問を入力にしています',
  DisplayOnlyQuestionAsSource: '説明文ブロックは入力にできません',
  ReservedColumn: '予約列は書き込み先にできません',
  EmptyScript: 'スクリプトが空です',
  UnmappedQuestion: 'どの列にも割り当てられていません（Pleasanter に残りません）',
};

export interface AdminSession {
  authenticated: boolean;
  setupRequired: boolean;
  loginId?: string | null;
  role?: string | null;
  pending?: boolean;
  pendingLoginId?: string | null;
  /** **途中状態のときだけ意味がある。** 2 要素をまだ登録していない */
  needsEnrollment?: boolean;
}

/** 既定の言語の文字列を取り出す。 */
export function text(value: LocalizedText | undefined): string {
  return value?.['ja'] ?? '';
}

/** 既定の言語へ書き込む。**器は多言語のまま保つ。** */
export function withText(value: LocalizedText | undefined, next: string): LocalizedText {
  return { ...(value ?? {}), ja: next };
}
