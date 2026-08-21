import { DEFAULT_LANGUAGE, type Language } from '../../lib/i18n/language';
import type { SurveyTheme } from '../../lib/theme';
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
  | 'Grid'
  | 'CheckboxGrid'
  | 'Ranking'
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
  'Grid',
  'CheckboxGrid',
  'Ranking',
  'Note',
];

/** 設問の形式の文言の鍵。**形式を足すと鍵が無くなり、型検査で落ちる。** */
export function questionTypeKey(type: QuestionType): MessageKey {
  return `questionType.${type}`;
}

/**
 * 選択肢を持つ形式か。
 *
 * **グリッドとランキングも選択肢を持つ。** グリッドは列、ランキングは並べる項目。
 */
export function hasChoices(type: QuestionType): boolean {
  return (
    type === 'Radio' ||
    type === 'Checkbox' ||
    type === 'Dropdown' ||
    type === 'Grid' ||
    type === 'CheckboxGrid' ||
    type === 'Ranking'
  );
}

/** 行を持つ形式か（Issue #74）。**列は `choices` の方。** */
export function hasRows(type: QuestionType): boolean {
  return type === 'Grid' || type === 'CheckboxGrid';
}

/**
 * マッピングの入力を複数出す形式か（Issue #74）。
 *
 * **グリッドは行ごと、ランキングは項目ごと。**
 * どちらも 1 設問が Pleasanter の列を複数食い得る。
 */
export function hasRowPorts(type: QuestionType): boolean {
  return hasRows(type) || type === 'Ranking';
}

/**
 * マッピングで指せる行（または項目）の識別子と文言。
 *
 * **サーバの `Question.RowPortIds` と同じ並び。** 食い違うと、
 * 画面で選べた行がサーバで「その設問に無い行」として弾かれる。
 */
export function rowPorts(question: Question): { rowId: string; label: LocalizedText }[] {
  if (hasRows(question.type)) {
    return question.settings.rows ?? [];
  }

  if (question.type === 'Ranking') {
    // **ランキングは選択肢そのものが入力になる。** 入る値は順位
    return question.choices.map((choice) => ({ rowId: choice.value, label: choice.label }));
  }

  return [];
}

/** 回答を持たない表示専用の要素か。 */
export function isDisplayOnly(type: QuestionType): boolean {
  return type === 'Note';
}

/**
 * ページを離れるときの行き先の種類。
 *
 * **値はサーバの列挙そのもの**（`Core/Definitions/Branching.cs`）。
 * JSON では文字列で載るので、綴りを変えると読めなくなる。
 */
export type PageTransitionKind = 'Next' | 'Page' | 'Submit';

/**
 * 行き先 1 つ。
 *
 * **`pageId` はサーバが `null` のとき落として返す。** `?` を外さないこと。
 */
export interface PageTransition {
  kind: PageTransitionKind;
  pageId?: string | null;
}

/** 表示条件の比べ方。**値はサーバの列挙そのもの。** */
export type ConditionOperator =
  | 'Equals'
  | 'NotEquals'
  | 'Contains'
  | 'Answered'
  | 'NotAnswered'
  | 'GreaterThan'
  | 'LessThan';

/** 画面に出す並び。**値を持たない比べ方は最後。** */
export const conditionOperators: ConditionOperator[] = [
  'Equals',
  'NotEquals',
  'Contains',
  'GreaterThan',
  'LessThan',
  'Answered',
  'NotAnswered',
];

/** 比べ方の文言の鍵。**比べ方を足すと鍵が無くなり、型検査で落ちる。** */
export function conditionOperatorKey(operator: ConditionOperator): MessageKey {
  return `conditionOperator.${operator}`;
}

/** 比べる値が要る比べ方か。 */
export function needsConditionValue(operator: ConditionOperator): boolean {
  return operator !== 'Answered' && operator !== 'NotAnswered';
}

/** 選択肢の中から選ばせる比べ方か。**無い選択肢を書かせないため。** */
export function picksFromChoices(operator: ConditionOperator): boolean {
  return operator === 'Equals' || operator === 'NotEquals';
}

/** 複数の条件のまとめ方。 */
export type ConditionMatch = 'All' | 'Any';

/** 条件 1 つ。**`value` はサーバが `null` のとき落として返す。** */
export interface ConditionRule {
  questionId: string;
  operator: ConditionOperator;
  value?: string | null;
}

/**
 * 表示条件。
 *
 * **`isEmpty` はサーバの計算プロパティ。** 応答には載るが、送るときは要らない。
 */
export interface VisibilityCondition {
  match: ConditionMatch;
  rules: ConditionRule[];
}

export interface Choice {
  value: string;
  label: LocalizedText;
  isOther?: boolean;
  /**
   * これを選んだときの行き先（Issue #41）。
   *
   * **無ければページ末尾の行き先に従う。** 単一選択にしか置けない。
   */
  next?: PageTransition | null;
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
  /**
   * 選べる数の下限・上限（Issue #101）。**複数選ぶ設問だけ。**
   *
   * **未回答には効かない。**「答えないか、下限まで選ぶか」であり、
   * 答えさせたいなら `isRequired` を立てる。
   */
  minSelections?: number;
  maxSelections?: number;
  /**
   * グリッドの行（Issue #74）。
   *
   * **列（選択肢）は `choices` の方。** 行はここ。
   * **1 行が 1 つの入力になる**ので、行を増やすほど Pleasanter の列を食う。
   */
  rows?: GridRow[];
}

/** グリッドの行 1 つ（Issue #74）。 */
export interface GridRow {
  /** 行の識別子。**設問の中で一意。** マッピングはこれで行を指す。 */
  rowId: string;
  label: LocalizedText;
}

export interface Question {
  questionId: string;
  type: QuestionType;
  title: LocalizedText;
  description?: LocalizedText;
  isRequired: boolean;
  choices: Choice[];
  settings: QuestionSettings;
  /**
   * この設問を出す条件（Issue #41）。
   *
   * **無ければ常に出す。** 参照できるのは自分より前の設問だけ。
   */
  visibleWhen?: VisibilityCondition | null;
}

export interface Page {
  pageId: string;
  title?: LocalizedText;
  description?: LocalizedText;
  questions: Question[];
  /**
   * このページを終えたときの行き先（Issue #41）。
   *
   * **無ければ次のページへ。** 選択肢の行き先が優先される。
   */
  next?: PageTransition | null;
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
  /**
   * 回答画面の見た目（Issue #56）。
   *
   * **無ければ既定の見た目。** 検査と書体の並びは回答画面と同じものを使う
   * （`lib/theme.ts`）。2 か所に持つと片方だけ緩んでも気付けない。
   */
  theme?: SurveyTheme | null;
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
  /**
   * どの行（または順位を付ける項目）から取るか（Issue #74）。
   *
   * **グリッドとランキングでだけ使う。** 行を持つ設問で選ばないと、
   * 行をまたいだ値がまとめて 1 列へ入る（公開時に弾かれる）。
   *
   * **既定の提案は「行ごとに 1 列」。** ただし複数の行を選んで
   * `join` で 1 列へまとめてもよく、**決めるのは使う人。**
   */
  rowId?: string;
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
  /**
   * 公開済みの版。**一度も公開していなければ「無い」。**
   *
   * **`null` ではなく「無い」**。サーバは null のプロパティを落として返すので
   * （`DefaultIgnoreCondition`）、`?` を外すと未公開のアンケートが
   * 公開済みとして描かれる。判定は {@link isPublished} を使うこと。
   */
  publishedVersion?: number | null;
  updatedAt: string;
  /**
   * 止まっている理由（Issue #53）。**止まっていなければ「無い」。**
   *
   * サーバは null のプロパティを落として返すので、`?` を外さないこと。
   */
  suspendedReason?: number | null;
  /** 止めた時刻。 */
  suspendedAt?: string | null;
  /** 受け付ける回答の上限。**「無い」なら上限なし。** */
  responseLimit?: number | null;
  /**
   * 回答の送信に proof-of-work を課すか（Issue #66）。
   *
   * **「無い」なら課す。** 既定は有効で、切るのは明示的に切ったときだけ。
   */
  requireProofOfWork?: boolean;
  /**
   * 回答の下書きを端末へ残すか（Issue #59）。
   *
   * **既定は無効**なので、分からないときは無効側に倒す。
   */
  allowDraft?: boolean;
  /**
   * 受け付けた回答の件数。
   *
   * **まだ Pleasanter へ届いていない分も含む。**
   * 回答者には受付完了と伝えているので、届いたかどうかで数え方を変えない。
   */
  responseCount: number;
}

/**
 * アンケート一覧の 1 ページ（Issue #79）。
 *
 * **総数は入っていない。** 増え続ける表なので、サーバは数えない。
 * 「次があるか」だけを持つ。
 */
export interface SurveyPage {
  items: SurveySummary[];
  hasMore: boolean;
}

/**
 * 一度でも公開したことがあるか。
 *
 * **`!== null` では守れない。** サーバは null のプロパティを落として返すので、
 * 未公開のときは `null` ではなく `undefined` になる。
 */
export function isPublished(survey: SurveySummary): boolean {
  return (survey.publishedVersion ?? null) !== null;
}

/**
 * 一覧に出すテンプレートの要約（Issue #58）。
 *
 * **サイト ID も公開用 ID も無い。** テンプレートは書き込み先を持たず、
 * 公開もされないので、サーバ側の型（`SurveyTemplateSummary`）にも入っていない。
 */
export interface SurveyTemplateSummary {
  templateId: string;
  title: string;
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

/**
 * 止まっている理由の文言の鍵（Issue #53）。
 *
 * **値はサーバの列挙そのもの**（`Data/SurveySnapshotStore.cs` の
 * `SurveySuspendedReason`）。**0 は使わない**ので、`null` と取り違えない。
 *
 * **理由が付いていない停止もある**（この機能より前に止めたもの）。
 * その場合は `null` を返し、画面は理由を出さない。
 */
export function suspendedReasonKey(reason: number | null | undefined): MessageKey | null {
  switch (reason) {
    case 1:
      return 'status.suspendedManually';
    case 2:
      return 'status.suspendedByLimit';
    default:
      return null;
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

/**
 * 管理者への知らせ 1 件（Issue #80）。
 *
 * **回答の中身も送信元も入っていない。** 出るのは「何が」「どのアンケートで」
 * 「何回」「いつ」だけ。
 *
 * **サーバは null のプロパティを落として返す。**
 * `?` を外すと、型検査は通るのに画面が真っ白になる。
 */
export interface AdminNotification {
  id: string;
  /** 種類の名前。**知らない値は `Unknown` で届く。** */
  kind: string;
  /** 紐づくアンケート。**全体に関わる知らせでは無い。** */
  surveyId?: string | null;
  /** アンケートの題名。**消えたアンケートでは無い。** */
  surveyTitle?: string | null;
  count: number;
  firstOccurredAt: string;
  lastOccurredAt: string;
  /** 既読にした日時。**未読では無い。** */
  readAt?: string | null;
}

/** 知らせの 1 ページ。 */
export interface AdminNotificationPage {
  items: AdminNotification[];
  /** 次のページがあるか。**総数は数えない。** */
  hasMore: boolean;
  /** 未読の合計。**行数ではなく起きた回数。** */
  unreadCount: number;
}

/**
 * 送信の滞留の状況。
 *
 * **回答の中身は入っていない**（`_documents/データモデル設計.md` 2.5）。
 * 出るのは件数と滞留の時刻だけ。
 *
 * **1 件も無い時刻は `null` ではなく「無い」**。サーバは null のプロパティを
 * 落として返すので、`?` を外すと画面が真っ白になる。
 */
export interface OutboxStatus {
  /** 送信待ちの件数。**デッドレターは含まない。** */
  pendingCount: number;
  /** 送信待ちのうち、最も古い受付時刻。**いつから詰まっているか。** */
  oldestPendingAt?: string | null;
  deadLetterCount: number;
  /** デッドレターのうち、最も古い最終試行の時刻。 */
  oldestDeadLetterAt?: string | null;
  /**
   * 滞留による受付停止の状態（Issue #72）。
   *
   * **サーバは null のプロパティを落として返す**ので省略可で受ける。
   */
  backlog?: BacklogGuard | null;
}

/**
 * 滞留による受付停止の状態（Issue #72）。
 *
 * **「送信待ちが多い」と「そのせいで受付を止めている」は別のこと。**
 * 件数だけでは、止まっているかどうかが読み取れない。
 */
export interface BacklogGuard {
  /** 閾値が設定されているか。**無効なら受付は止まらない。** */
  enabled: boolean;
  /** 滞留の総件数。**送信待ちとデッドレターの合計。** */
  total: number;
  /** 全体の上限。**0 なら段そのものが無効。** */
  totalLimit: number;
  /** 全アンケートの受付を止めているか。 */
  totalBlocked: boolean;
  /** アンケート単位の上限。 */
  perSurveyLimit: number;
  /** 滞留で止まっているアンケートの本数。 */
  blockedSurveyCount: number;
  /** 最後に数えた時刻。**一度も数えていなければ届かない。** */
  sampledAt?: string | null;
}

/**
 * 添付を弾いた記録 1 件（Issue #39）。
 *
 * **送信元もファイル名も届かない。** 回答者は完全匿名という前提を、
 * サーバ側の型でも守っている。
 */
export interface AttachmentRejectionEntry {
  occurredAt: string;
  surveyId: string;
  /** アンケートの題名。**消えたアンケートでは届かない。** */
  surveyTitle?: string | null;
  /** どの設問か。**設問に紐づかない理由では届かない。** */
  questionId?: string | null;
  /** 弾いた理由。**文言は画面が持つ**（サーバは番号だけ返す）。 */
  reason: number;
  /** その送信で、その理由に当たった件数。 */
  fileCount: number;
}

/** 添付を弾いた記録の 1 ページ。 */
export interface AttachmentRejectionPage {
  entries: AttachmentRejectionEntry[];
  hasMore: boolean;
  /** 直近に弾いた件数。**行数ではなくファイルの数。** */
  recentCount: number;
  /** 「直近」が何日か。 */
  recentDays: number;
}

/**
 * 弾いた理由の文言の鍵（Issue #39）。
 *
 * **番号は `Core/Attachments/AttachmentRejectionReason` の並び順そのもの。**
 * 並びを変えると意味がずれるので、あちらへ足すときは末尾に足すこと。
 */
export function attachmentRejectionReasonKey(reason: number): MessageKey {
  const keys: MessageKey[] = [
    'attachmentRejection.ExtensionNotAllowed',
    'attachmentRejection.ContentDoesNotMatchExtension',
    'attachmentRejection.TooLarge',
    'attachmentRejection.TooMany',
    'attachmentRejection.TotalTooLarge',
    'attachmentRejection.InvalidFileName',
    'attachmentRejection.Infected',
    'attachmentRejection.ScannerUnavailable',
  ];

  // **知らない番号でも落とさない。** サーバ側が先に増えることがある
  return keys[reason] ?? 'attachmentRejection.Unknown';
}

/**
 * 送信できなかった回答 1 件。
 *
 * **回答本文は届かない。** サーバ側の型にも入る場所が無い。
 */
export interface DeadLetterEntry {
  responseToken: string;
  surveyId: string;
  /** アンケートの題名。**消えたアンケートでは無い。** */
  surveyTitle?: string | null;
  surveyVersion: number;
  retryCount: number;
  /** 最後に失敗した理由。**そのまま出さず、必ず逃がして描くこと。** */
  lastError?: string | null;
  /** 回答を受け付けた時刻。**ここからずっと届いていない。** */
  receivedAt: string;
  lastAttemptAt: string;
}

/** 送信できなかった回答の 1 ページ。 */
export interface DeadLetterPage {
  entries: DeadLetterEntry[];
  /** 次のページがあるか。**総数は数えない。** */
  hasMore: boolean;
}
