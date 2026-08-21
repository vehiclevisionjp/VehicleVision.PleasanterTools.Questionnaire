import type { AltchaChallenge } from './altcha';
import { DEFAULT_LANGUAGE, type Language } from './i18n/language';
import type { SurveyTheme } from './theme';

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
  | 'Grid'
  | 'CheckboxGrid'
  | 'Ranking'
  | 'Note'
  | 'Embed';

/** 言語コードをキーにした表示文字列。 */
export type LocalizedText = Record<string, string>;

/**
 * ページを離れるときの行き先（Issue #41）。
 *
 * **値が無い項目はサーバが落として返す**ので、どれも省略可で書く。
 */
export interface PageTransition {
  kind?: 'Next' | 'Page' | 'Submit';
  pageId?: string | null;
}

/** 表示条件の比べ方。 */
export type ConditionOperator =
  | 'Equals'
  | 'NotEquals'
  | 'Contains'
  | 'Answered'
  | 'NotAnswered'
  | 'GreaterThan'
  | 'LessThan';

/** 条件 1 つ。 */
export interface ConditionRule {
  questionId: string;
  operator: ConditionOperator;
  value?: string | null;
}

/** 設問を出す条件。 */
export interface VisibilityCondition {
  match?: 'All' | 'Any';
  rules?: ConditionRule[];
}

export interface Choice {
  value: string;
  label: LocalizedText;
  /** 「その他」（自由記述を伴う選択肢）か。 */
  isOther: boolean;
  /** これを選んだときの行き先。**無ければページ末尾の行き先に従う。** */
  next?: PageTransition | null;
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
  /**
   * 入力の形式を正規表現で確かめる（Issue #102）。
   *
   * **値の全体が合うかを見る。** 前後は暗黙に固定される。
   * ⚠️ **サーバ側は後退戻りしない照合器で照合する。**
   * ここ（画面側）は素の `RegExp` なので、**判定が食い違うことがあり得る。**
   * 受け付けるかどうかを決めるのはサーバ側。
   */
  pattern?: string;
  /** 正規表現に合わないときに出す文言。**正規表現そのものは見せない。** */
  patternMessage?: LocalizedText;
  /**
   * 選べる数の下限・上限（Issue #101）。**複数選ぶ設問だけ。**
   *
   * **未回答には効かない。**「答えないか、下限まで選ぶか」であり、
   * 答えさせたいなら `isRequired` を立てる。
   */
  minSelections?: number;
  maxSelections?: number;
  /**
   * 選択肢の順序を回答者ごとに入れ替えるか（Issue #103）。
   *
   * **「その他」は入れ替えず、必ず末尾に置く。**
   * **並び順は 1 回の回答の中で固定する**（戻っても位置が変わらない）。
   */
  shuffleChoices?: boolean;
  maxFileCount?: number;
  maxFileSizeBytes?: number;
  /**
   * グリッドの行（Issue #74）。
   *
   * **列（選択肢）は `choices` の方。** 行はここ。
   * **1 行が 1 つの入力になる**ので、行を増やすほど Pleasanter の列を食う。
   */
  rows?: GridRow[];
  /**
   * 埋め込み（Issue #104 / #107）。**`type === 'Embed'` のときだけ使う。**
   *
   * **配信元は運用側の設定でしか増やせない。** 保存の時点で弾かれるので、
   * ここに入っている URL は許された配信元のものだけ。
   * **ただし設定は後から狭められる**ので、出す前にもう一度確かめる。
   */
  embed?: EmbedSource | null;
}

/** 埋め込みの出し方（Issue #104 / #107）。 */
export type EmbedKind = 'Image' | 'Frame';

/** 設問の間へ差し込む埋め込み 1 つ。 */
export interface EmbedSource {
  kind: EmbedKind;
  /** 埋め込み先。**絶対 URL の `https:` のみ。** */
  url: string;
  /** 画像では `alt`、外部ページでは `title` に使う。 */
  alternativeText?: LocalizedText | null;
  /** 幅に対する高さの比。**外部ページで使う。** */
  aspectRatio?: number;
}

/** グリッドの行 1 つ（Issue #74）。 */
export interface GridRow {
  /** 行の識別子。**設問の中で一意。** マッピングはこれで行を指す。 */
  rowId: string;
  label: LocalizedText;
}

/** 説明文ブロックの段落の種類。**知らない値は出さない**（Issue #108）。 */
export type NoteBlockKind = 'Paragraph' | 'Heading' | 'BulletList' | 'NumberedList';

/** 説明文ブロックの文字装飾の種類。 */
export type NoteInlineKind = 'Text' | 'Bold' | 'Italic' | 'Link';

/** 説明文ブロックの中の文字列 1 片。 */
export interface NoteInline {
  kind: NoteInlineKind;
  /** 表示する文字列。**平文。記法もタグも含まない。** */
  text: string;
  /** リンク先。**サーバが `https:` だけを通している。** */
  href?: string | null;
}

/** 箇条書きの項目 1 つ。 */
export interface NoteListItem {
  inlines: NoteInline[];
}

/** 説明文ブロックの段落 1 つ。 */
export interface NoteBlock {
  kind: NoteBlockKind;
  inlines: NoteInline[];
  items: NoteListItem[];
  /** 見出しの深さ（2 〜 4）。 */
  level: number;
}

export interface Question {
  questionId: string;
  type: QuestionType;
  title: LocalizedText;
  description?: LocalizedText;
  isRequired: boolean;
  choices: Choice[];
  settings: QuestionSettings;
  /** この設問を出す条件。**無ければ常に出す。** */
  visibleWhen?: VisibilityCondition | null;
  /**
   * 説明文ブロックの本文を、書式の付いた形にしたもの（Issue #108）。
   *
   * **`description` をサーバが記法として読んだ結果。** 言語コードが鍵。
   * **画面はこちらしか見ない。** 記法の解釈はサーバにしか無い。
   */
  noteBlocks?: Record<string, NoteBlock[]> | null;
}

export interface Page {
  pageId: string;
  title?: LocalizedText;
  description?: LocalizedText;
  questions: Question[];
  /** このページを終えたときの行き先。**無ければ次のページへ。** */
  next?: PageTransition | null;
  /**
   * ページの中の設問の順序を回答者ごとに入れ替えるか（Issue #103）。
   *
   * ⚠️ **出し分けの条件を持つ設問があるページでは指定できない**（公開時に弾かれる）。
   * **説明文ブロックは動かさない。**
   */
  shuffleQuestions?: boolean;
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
  /** 回答画面の見た目（Issue #56）。**無ければ既定の見た目。** */
  theme?: SurveyTheme | null;
}

export interface FormResponse {
  publicId: string;
  definition: SurveyDefinition;
  /**
   * このアンケートが proof-of-work を要るとしているか（Issue #66）。
   *
   * **要否を伝えるのはこの口だけ。** 課題を出す口（`.../ticket`）は
   * 要否に関わらず課題を返す（出し分けると公開 ID の実在が漏れる）。
   *
   * **分からなければ「要る」として扱うこと**（`?? true`）。
   * 解かずに送って断られるより、要らない計算をする方が軽い。
   * **どちらにせよ、受け付けるかどうかを決めるのはサーバ側。**
   */
  requiresProofOfWork?: boolean;
  /**
   * 回答の下書きを端末へ残してよいか（Issue #59）。
   *
   * **下書きはサーバへ送らない**ので、届くのは可否だけ。
   * **届かなければ「残さない」。** 端末は共有され得るので、
   * 分からないときは残さない側へ倒す。
   */
  allowsDraft?: boolean;
}

/** 送信する回答 1 件。 */
export interface PayloadAnswer {
  questionId: string;
  values: string[];
  otherText?: string;
  fileNames?: string[];
  /**
   * 行ごとの回答（Issue #74）。
   *
   * **グリッドの回答はここにしか入っていない。**
   * 落とすと、答えたのに未回答として弾かれる。
   */
  rows?: Record<string, string[]>;
}

/** サーバが発行する送信チケットと回答トークン。 */
export interface Ticket {
  /** この端末の回答を指すトークン。**サーバが決める。** */
  responseToken: string;
  /** 送信時にそのまま返す署名付きのチケット。 */
  ticket: string;
  /**
   * proof-of-work の課題（Issue #55）。
   *
   * **切っているときは付いてこない。** サーバは null の項目を落として返すので、
   * `!== null` では守れない。
   */
  altcha?: AltchaChallenge | null;
}

/**
 * 受付を断られた理由。
 *
 * - `rejected` は bot 対策で断られたとき（**理由の内訳は返らない**）
 * - `tooManyRequests` はレート制限。画面側で付ける
 */
export type RejectionReason =
  | 'notStarted'
  | 'closed'
  | 'suspended'
  | 'notFound'
  | 'rejected'
  | 'tooManyRequests';

/** 設問の回答（画面が持つ形）。 */
export interface AnswerState {
  values: string[];
  otherText: string;
  /**
   * 行ごとの回答（Issue #74）。行の識別子 → その行で選ばれた値。
   *
   * **グリッドはこちらだけを使い、`values` は使わない。**
   * **ランキングは逆で、`values` に順位の順で並べる。**
   */
  rows?: Record<string, string[]>;
  /**
   * 添付ファイル。**送信のたびに選び直してもらう。**
   * 一度送った添付をブラウザ側で持ち続けられないため（File は保存できない）。
   */
  files?: File[];
}

/**
 * 表示文字列を取り出す。無ければ既定の言語、それも無ければ空文字。
 *
 * **言語は必ず渡す。** 既定値を持たせると、
 * 言語を渡し忘れた場所が日本語のまま静かに残る。
 */
export function text(value: LocalizedText | undefined, language: Language): string {
  if (!value) return '';
  return value[language] ?? value[DEFAULT_LANGUAGE] ?? '';
}

/**
 * 選択肢を持つ形式か。
 *
 * **グリッドとランキングも選択肢を持つ。** グリッドは列、ランキングは並べる項目。
 */
export function hasChoices(question: Question): boolean {
  return (
    question.type === 'Radio' ||
    question.type === 'Checkbox' ||
    question.type === 'Dropdown' ||
    question.type === 'Grid' ||
    question.type === 'CheckboxGrid' ||
    question.type === 'Ranking'
  );
}

/** 行を持つ形式か（Issue #74）。**回答は `rows` に入る。** */
export function hasRows(question: Question): boolean {
  return question.type === 'Grid' || question.type === 'CheckboxGrid';
}

/** 1 行に複数選べる形式か。 */
export function allowsMultiplePerRow(question: Question): boolean {
  return question.type === 'CheckboxGrid';
}

/**
 * 選べる数の下限・上限を持てる形式か（Issue #101）。
 *
 * **複数選ぶチェックボックスだけ。** ランキングは並べた順が答えで、
 * 「いくつ選ぶか」とは別の話なので対象にしない。**サーバ側と同じ線引き。**
 */
export function hasSelectionRange(question: Question): boolean {
  return question.type === 'Checkbox' || question.type === 'CheckboxGrid';
}

/** その行で選ばれている値。**無ければ空。** */
export function rowValues(answer: AnswerState | undefined, rowId: string): string[] {
  return answer?.rows?.[rowId] ?? [];
}

/** 回答を持たない表示専用の要素か。 */
export function isDisplayOnly(question: Question): boolean {
  return question.type === 'Note' || question.type === 'Embed';
}
