/** サーバから来る設問の形式。`.Core` の QuestionType と対応する。 */
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

/** 言語コードをキーにした表示文字列。 */
export type LocalizedText = Record<string, string>;

export interface Choice {
  value: string;
  label: LocalizedText;
  /** 「その他」（自由記述を伴う選択肢）か。 */
  isOther: boolean;
}

export interface QuestionSettings {
  maxLength?: number;
  placeholder?: LocalizedText;
  defaultValue?: string;
  scaleMinimum?: number;
  scaleMaximum?: number;
  scaleMinimumLabel?: LocalizedText;
  scaleMaximumLabel?: LocalizedText;
  numberMinimum?: number;
  numberMaximum?: number;
  format?: 'None' | 'Email' | 'Url';
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
  pages: Page[];
  displayMode: 'Paged' | 'OneQuestionPerPage';
  showProgress: boolean;
  confirmationMessage?: LocalizedText;
  allowEditingAfterSubmit: boolean;
}

export interface FormResponse {
  publicId: string;
  definition: SurveyDefinition;
}

/** 送信する回答 1 件。 */
export interface PayloadAnswer {
  questionId: string;
  values: string[];
  otherText?: string;
  fileNames?: string[];
}

/** 受付を断られた理由。 */
export type RejectionReason = 'notStarted' | 'closed' | 'suspended' | 'notFound';

/** 設問の回答（画面が持つ形）。 */
export interface AnswerState {
  values: string[];
  otherText: string;
}

/** 既定の言語。**翻訳漏れでも画面を落とさない。** */
const DEFAULT_LANGUAGE = 'ja';

/** 表示文字列を取り出す。無ければ既定の言語、それも無ければ空文字。 */
export function text(value: LocalizedText | undefined, language = DEFAULT_LANGUAGE): string {
  if (!value) return '';
  return value[language] ?? value[DEFAULT_LANGUAGE] ?? '';
}

/** 選択肢を持つ形式か。 */
export function hasChoices(question: Question): boolean {
  return question.type === 'Radio' || question.type === 'Checkbox' || question.type === 'Dropdown';
}

/** 回答を持たない表示専用の要素か。 */
export function isDisplayOnly(question: Question): boolean {
  return question.type === 'Note';
}
