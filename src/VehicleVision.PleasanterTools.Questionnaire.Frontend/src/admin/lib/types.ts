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
  | 'Note'
  | 'Embed'
  | 'Confirm';

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
  'Embed',
  'Confirm',
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
  return type === 'Note' || type === 'Embed';
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
  /**
   * 説明文の書き方（Issue #267）。
   *
   * **無ければプレーン。** 過去の説明文に含まれる `*` や `#` の見え方を変えない。
   * `Note` は従来どおり常に記法として扱う。
   */
  descriptionFormat?: 'Plain' | 'Markup';
  maxLength?: number;
  placeholder?: LocalizedText;
  /** 全角の ASCII 英数字・記号を半角へ変換する。既定は無効。 */
  convertFullWidthAsciiToHalfWidth?: boolean;
  /** 半角カナを全角へ変換する。既定は無効。 */
  convertHalfWidthKanaToFullWidth?: boolean;
  /** 全角空白を半角へ変換する。既定は無効。 */
  convertFullWidthSpacesToHalfWidth?: boolean;
  /** 前後の空白を取り除く。既定は無効。 */
  trimWhitespace?: boolean;
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
   * 入力の形式（記述式・段落だけ）。
   *
   * **サーバ側と回答画面には元からあったが、管理画面から設定できなかった**
   * （Issue #189 で気付いて足した）。
   * **`Email` にした記述式（1 行）だけが、自動返信の宛先に選べる。**
   */
  format?: 'None' | 'Email' | 'Url';
  /**
   * 入力の形式を正規表現で確かめる（Issue #102）。
   *
   * **値の全体が合うかを見る。** 前後は暗黙に固定される。
   * ⚠️ **サーバ側は後退戻りしない照合器で照合する**ので、
   * 先読み・後方参照・原子グループは使えない。**公開の前に弾かれる。**
   */
  pattern?: string;
  /** 合わないときに出す文言。**正規表現そのものは回答者へ見せない。** */
  patternMessage?: LocalizedText;
  /**
   * 選択肢の順序を回答者ごとに入れ替えるか（Issue #103）。
   *
   * **「その他」は入れ替えず、必ず末尾に置く。**
   * **並び順は 1 回の回答の中で固定する。**
   */
  shuffleChoices?: boolean;
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
   * **配信元は運用側の設定でしか増やせない。** 許されていないホストは
   * 下書きの保存の時点で断られる（`GET /api/admin/surveys/embed-options` で一覧を出す）。
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
  /**
   * ページの中の設問の順序を回答者ごとに入れ替えるか（Issue #103）。
   *
   * ⚠️ **出し分けの条件を持つ設問があるページでは指定できない**（公開時に弾かれる）。
   * **説明文ブロックは動かさない。**
   */
  shuffleQuestions?: boolean;
}

/**
 * 回答者への自動返信メール（Issue #189）。
 *
 * ⚠️ **既定は送らない。** 完全匿名が前提のアプリで、回答者のメールアドレスを扱う
 * 唯一の機能なので、明示的に有効にしたときだけ送る。
 *
 * **宛先はメールアドレス形式の設問への回答から採る。**
 * アンケートに宛先を書く欄が無ければ送りようが無く、勝手に集める経路も作らない。
 */
export interface AutoReplySettings {
  enabled: boolean;
  /** 宛先にする設問。**メールアドレス形式の記述式（1 行）に限る。** */
  toQuestionId?: string | null;
  subject?: LocalizedText;
  /** 本文。**平文。** 書式は持たない。 */
  body?: LocalizedText;
  /** 差出人の表示名。**アドレスはサーバの全体設定から変えない。** */
  fromName?: LocalizedText;
  /** 返信先。未設定ならサーバの全体設定を使う。 */
  replyToAddress?: string | null;
  /** BCC。未設定なら付けない。 */
  bccAddress?: string | null;
  /** 再編集リンクの有効日数。**既定 7 日。** 受付期間の終了は超えない。 */
  editLinkDays?: number;
}

/** 回答後の配布資産に使う引換券の期限（Issue #318）。 */
export interface AssetDeliverySettings {
  expiration: 'AcceptTo' | 'DaysAfterResponse' | 'CompletedOnly';
  /** 回答からの日数。受付終了が無い場合の既定日数にも使う。 */
  days: number;
}

export interface SurveyDefinition {
  surveyId: string;
  version: number;
  /** 訳が無いときに回答者へ表示する言語。 */
  fallbackLanguage: Language;
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
  /**
   * 回答者への自動返信メール（Issue #189）。
   *
   * **無ければ送らない。** 件名と本文は定義の一部なので、**公開した版で固定される。**
   */
  autoReply?: AutoReplySettings | null;
  /** 無ければ「アンケートの受付期間に合わせる」。 */
  assetDelivery?: AssetDeliverySettings | null;
  pages: Page[];
}

export type QuestionPort = 'Value' | 'OtherText' | 'FileNames' | 'Files';
export type MappingSystemValue =
  | 'EventType'
  | 'OccurredAt'
  | 'AssetFileName'
  | 'AssetId'
  | 'ReferenceId'
  | 'SurveyTitle';

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
  systemValue?: MappingSystemValue | null;
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
  assetHistorySiteId?: number;
  assetHistoryMapping?: MappingDefinition | null;
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
   * 回答画面を運用側が許可した親サイトへ埋め込んでよいか（Issue #334）。
   * **既定は無効。**
   */
  allowEmbedding?: boolean;
  /**
   * 受け付けた回答の件数。
   *
   * **まだ Pleasanter へ届いていない分も含む。**
   * 回答者には受付完了と伝えているので、届いたかどうかで数え方を変えない。
   */
  responseCount: number;
  /** テスト公開中に受け付けた回答の件数。 */
  testResponseCount: number;
  /** アーカイブした時刻。**無ければ通常のアンケート。** */
  archivedAt?: string | null;
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
 * 回答用 URL を開ける状態か。
 *
 * **版の有無だけで決めない。** テスト公開から下書きへ戻しても版は残るため、
 * 状態を見ないと下書きの URL を誤って出してしまう。
 */
export function isPublished(survey: SurveySummary): boolean {
  return survey.archivedAt == null
    && (survey.status === 1 || survey.status === 2 || survey.status === 3);
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

/** 設問の取り込み元にあるページ。**ページ自体は取り込まない。** */
export interface QuestionImportPage {
  pageId: string;
  title?: LocalizedText | null;
  questions: Pick<Question, 'questionId' | 'type' | 'title'>[];
}

/** 取り込み元から選べる設問。 */
export interface QuestionImportSource {
  pages: QuestionImportPage[];
}

/** 設問を取り込み用に写した結果。 */
export interface QuestionImportResult {
  questions: Question[];
  removedChoiceTransitions: number;
  removedVisibilityConditions: number;
  removedAssetReferences: number;
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
    case 3:
      return 'status.testPublished';
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

/**
 * 管理画面でできることの単位（Issue #160）。
 *
 * **画面はこれで出し分ける。**「Administrator かどうか」で分けると、
 * 役割を増やすたびに画面を直すことになる。
 * **サーバ側でも同じ権限で判定している**ので、隠すのは押せない釦を出さないため。
 */
export type AdminPermission =
  | 'surveys.read'
  | 'surveys.write'
  | 'surveys.publish'
  | 'surveys.delete'
  | 'templates.read'
  | 'templates.write'
  | 'outbox.read'
  | 'outbox.requeue'
  | 'notifications.read'
  | 'audit.read'
  | 'users.read'
  | 'users.write'
  | 'users.resetTwoFactor'
  | 'settings.saml'
  | 'settings.manage'
  | 'maintenance.manage';

export interface AppSettings {
  fields: AppSettingField[];
  publishedSurveyCount: number;
  isPleasanterConfigured: boolean;
}

export interface AppSettingField {
  key: string;
  type: 'string' | 'boolean' | 'integer';
  value: string | null;
  hasValue: boolean;
  isSecret: boolean;
  isFixed: boolean;
  defaultValue: string;
  isDefault: boolean;
  labelJa: string;
  labelEn: string;
  descriptionJa: string;
  descriptionEn: string;
  minimum: number | null;
  maximum: number | null;
  maximumLength: number | null;
  showPreview: boolean;
}

export interface SamlSettings {
  enabled: boolean;
  entityId: string;
  idpEntityId: string;
  singleSignOnUrl: string;
  idpCertificate: string;
  unknownUser: string;
  registerRole: string;
  loginIdSource: string;
  loginIdClaim: string;
  buttonLabel: string;
  singleLogoutUrl: string;
  fixedFields: Record<SamlSettingField, boolean>;
}

export type SamlSettingField =
  | 'enabled'
  | 'entityId'
  | 'idpEntityId'
  | 'singleSignOnUrl'
  | 'idpCertificate'
  | 'unknownUser'
  | 'registerRole'
  | 'loginIdSource'
  | 'loginIdClaim'
  | 'buttonLabel'
  | 'singleLogoutUrl';

/**
 * 管理者の一覧の 1 行（Issue #156）。
 *
 * **秘密は入らない。** パスワードのハッシュも 2 要素の共有鍵も返らず、
 * 「登録済みか」だけが分かる。
 */
export interface AdminUserRow {
  adminUserId: string;
  loginId: string;
  /** `Administrator` / `Editor` / `SurveyAdministrator` / `UserAdministrator` / `Auditor` */
  role: string;
  isDisabled: boolean;
  /** 2 要素を登録しているか。**共有鍵そのものは返らない** */
  hasTotp: boolean;
  /** まだ招待を受け取っていない（＝一度も入っていない） */
  invitationPending: boolean;
  /**
   * **止め忘れを見つける唯一の手掛かり。**
   *
   * ⚠️ **一度も入っていない人では、この項目そのものが来ない。**
   * サーバの JSON は `null` を省く設定（`DefaultIgnoreCondition = WhenWritingNull`）。
   * `null` だけを見ていると `undefined` を取りこぼす（実際に踏んだ）。
   */
  lastLoginAt?: string | null;
  createdAt: string;
}

/** 出した招待。**トークンを受け取れるのはこの時だけ。** */
export interface IssuedInvitation {
  adminUserId: string;
  invitationToken: string;
  expiresAt: string;
  /**
   * 招待のメールを積めたか（Issue #189）。
   *
   * **送れていなければ、下に出ている URL を手で渡す必要がある。**
   * メールの設定が無い・ログイン ID がメールアドレスでない場合は送られない。
   */
  mailSent?: boolean;
}

export interface AdminSession {
  authenticated: boolean;
  /** 自分の管理者 ID（Issue #156）。**自分自身への操作を止めるために要る** */
  adminUserId?: string | null;
  setupRequired: boolean;
  loginId?: string | null;
  role?: string | null;
  /** その役割が持つ権限。**古いサーバでは来ない**ので、無い場合も扱えるようにしておく */
  permissions?: AdminPermission[];
  pending?: boolean;
  pendingLoginId?: string | null;
  /** **途中状態のときだけ意味がある。** 2 要素をまだ登録していない */
  needsEnrollment?: boolean;

  /**
   * 2 要素認証の方針（Issue #154）。`Required` / `Optional` / `Disabled`。
   *
   * **画面で「登録する／解除する」を出し分けるために要る。**
   */
  twoFactor?: string;

  /** 自分が 2 要素を登録しているか（Issue #154）。 */
  hasTotp?: boolean;

  /** SAML でのログインが使えるか（Issue #166）。**既定は無効** */
  samlEnabled?: boolean;

  /** パスワードログインと招待受取に proof-of-work が必要か（Issue #252）。 */
  captchaEnabled?: boolean;

  /** SAML の釦に出す文字。`null` なら決まった文言を使う */
  samlLabel?: string | null;

  /**
   * IdP へログアウトを頼めるか（Issue #191）。
   *
   * **SAML で入った人で、かつ IdP の単一ログアウトが設定されているときだけ真。**
   * 真なら、ログアウトは `/api/admin/saml/logout` へ行く（IdP 側も落とす）。
   */
  samlSingleLogout?: boolean;

  /**
   * サーバ側でメールを送れる状態か（Issue #189）。
   *
   * **自動返信を設定しただけで「送っているつもり」にさせないためのもの。**
   * 接続先も資格情報も返らない。**認証済みのときだけ載る。**
   */
  mailEnabled?: boolean;
  /** 新しい回答の 24 時間ごとのまとめ通知をメールで受け取るか。**既定は無効。** */
  responseNotificationEnabled?: boolean;
  /** ログイン ID を、本人宛て試し送信のメールアドレスとして使えるか。 */
  autoReplyTestRecipientAvailable?: boolean;

  /**
   * 利用者ごとの表示言語。`null` は「まだ選んでいない」。
   *
   * **画面の初期値を決めるためのもの**（`_documents/多言語対応方針.md` 2 章）。
   */
  language?: string | null;
}

/** サーバ側に残っているログイン済み端末 1 件。 */
export interface AdminSessionRow {
  adminSessionId: string;
  current: boolean;
  createdAt: string;
  expiresAt: string;
  ipAddress?: string | null;
  userAgent?: string | null;
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
export function displayText(
  value: LocalizedText | undefined,
  language: Language,
  fallbackLanguage: Language = DEFAULT_LANGUAGE,
): string {
  return value?.[language] ?? value?.[fallbackLanguage] ?? '';
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
