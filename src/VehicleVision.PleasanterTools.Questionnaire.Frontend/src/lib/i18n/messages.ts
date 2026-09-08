import { DEFAULT_LANGUAGE, interpolate, type Language } from './language';

/**
 * 回答画面の文言。
 *
 * **日本語のカタログが鍵の一覧そのもの。**
 * 英語は `Record<MessageKey, string>` として書くので、
 * **足し忘れると型検査（`npm run check` / `npm run build`）で落ちる**
 * （`_documents/多言語対応方針.md` 4 章）。
 *
 * **管理画面の文言と混ぜない。** 束を分けてあるので、
 * 混ぜると回答者へ管理画面の文言まで配ることになる。
 */
export const ja = {
  // ---- 画面の状態 ----------------------------------------------------------
  'status.loading': '読み込んでいます…',

  // ---- 受け付けられない -----------------------------------------------------
  'rejected.notFound.title': 'アンケートが見つかりません',
  'rejected.closed.title': '受付時間外です',
  'rejected.notStarted': 'このアンケートはまだ受付を開始していません。',
  'rejected.closed': 'このアンケートの受付は終了しました。',
  'rejected.suspended': 'このアンケートは現在受付を停止しています。',
  'rejected.notFound': 'このアンケートは見つかりませんでした。URL をご確認ください。',
  'rejected.rejected': '送信を受け付けられませんでした。ページを開き直してお試しください。',
  'rejected.tooManyRequests': '送信が混み合っています。少し時間を置いてお試しください。',
  'error.badUrl.title': 'URL が正しくありません',
  'error.badUrl': 'アンケートの URL をご確認ください。',

  // ---- 既に回答済み ---------------------------------------------------------
  'answered.title': 'この端末では回答済みです',
  'answered.canEdit': '前回の回答を編集できます。',
  'answered.cannotRead': '前回の回答内容を読み込めませんでした。',
  'answered.editingNotAllowed': 'このアンケートは回答の編集を受け付けていません。',
  'answered.answerAgain': '新しい回答として送信する',
  'answered.answerAgainNote':
    '「新しい回答として送信する」を選ぶと、前回とは別の回答として登録されます。',

  // ---- 完了 -----------------------------------------------------------------
  'completed.title': '回答を受け付けました',
  'completed.thanks': 'ご協力ありがとうございました。',
  'completed.edit': '回答を編集する',
  'completed.answerAgain': '別の回答を送信する',

  // ---- 回答 -----------------------------------------------------------------
  'form.progressLabel': '回答の進み具合',
  'form.pageCount': '{current} / {total} ページ',
  'form.back': '戻る',
  'form.next': '次へ',
  'form.submit': '送信する',
  'form.submitting': '送信しています…',
  'form.languageLabel': '言語',
  'form.trapLabel': 'この欄は入力しないでください',
  'draft.found': '前回の続きがこの端末に残っています。',
  'draft.foundNote':
    'この端末を他の人と共有している場合は、内容を確かめてから再開してください。残さない場合は「破棄する」を押してください。',
  'draft.restore': '続きから再開する',
  'draft.discard': '破棄する',
  'draft.restored': '前回の続きを読み込みました。',
  'draft.discarded': '端末に残っていた下書きを消しました。',

  // ---- 設問 -----------------------------------------------------------------
  'question.required': '必須',
  'question.selectPlaceholder': '選択してください',
  'question.otherText': 'その他の内容',
  'question.unsupported': 'この設問形式には対応していません（{type}）',
  'question.ratingLabel': '{value} / {max}',
  'question.gridRowHeader': '項目',
  'question.gridRowLabel': '{row}: {choice}',
  'question.rankingLead': '選択すると順位が付きます。もう一度押すと解除されます。',
  // ---- 埋め込み（Issue #104 / #107） ------------------------------------------
  'embed.notice':
    'このページには外部サイトの内容（{hosts}）が含まれます。表示すると、そのサイトへ接続します。',
  'embed.blocked': 'この埋め込みは表示できません。',
  'question.rankingUnranked': '未選択',
  'question.rankingRank': '{rank} 位',
  'question.rankingUp': '順位を上げる',
  'question.rankingDown': '順位を下げる',
  'question.rankingClear': '順位を外す',
  'question.rankingStatus': '{label}: {rank} 位 / {total} 件中',
  'question.fileCountLimit': '{count} 件まで',
  'question.fileSizeLimitMegabytes': '1 件 {megabytes} MB まで',
  'question.fileSizeLimitBytes': '1 件 {bytes} バイトまで',
  'question.selectionRange': '{minimum} 個以上 {maximum} 個以下で選んでください',
  'question.selectionMinimum': '{minimum} 個以上選んでください',
  'question.selectionMaximum': '{maximum} 個まで選べます',

  // ---- 送信できなかった -----------------------------------------------------
  'submit.tooManyRequests': '送信が混み合っています。少し時間を置いてもう一度お試しください。',
  'submit.rejected': '送信を受け付けられませんでした。もう一度「送信する」を押してください。',
  'submit.insecureContext':
    'この画面は安全な接続（https）で開いてください。今の状態では送信できません。',
  'submit.failed': '送信できませんでした。入力内容はそのままです。少し時間を置いてもう一度お試しください。',

  // ---- 添付 -----------------------------------------------------------------
  'attachment.defaultName': '添付ファイル',
  'attachment.extensionNotAllowed': '{name}: この種類のファイルは受け付けていません',
  'attachment.contentDoesNotMatchExtension': '{name}: ファイルの中身が拡張子と一致しません',
  'attachment.tooLarge': '{name}: ファイルが大きすぎます',
  'attachment.totalTooLarge': '添付の合計サイズが大きすぎます',
  'attachment.tooMany': '添付できる個数を超えています',
  'attachment.invalidFileName': '{name}: ファイル名に使えない文字が含まれています',
  'attachment.unknown': '{name}: 受け付けられない添付です',

  // ---- 画面側の検証 ---------------------------------------------------------
  'validation.required': '回答してください',
  'validation.fileRequired': 'ファイルを選んでください',
  'validation.fileCount': 'ファイルは {count} 件までです',
  'validation.fileTooLarge': '{name}: ファイルが大きすぎます',
  'validation.singleValueOnly': '回答は 1 つだけ選んでください',
  'validation.rowRequired': 'すべての行に回答してください',
  'validation.duplicateRank': '同じ項目に 2 つ以上の順位が付いています',
  'validation.tooFewSelections': '{minimum} 個以上選んでください',
  'validation.tooManySelections': '{maximum} 個までしか選べません',
  'validation.pattern': '指定された形式で入力してください',
  'validation.tooLong': '{max} 文字以内で入力してください',
  'validation.email': 'メールアドレスの形式で入力してください',
  'validation.url': 'http:// または https:// で始まる URL を入力してください',
  'validation.notANumber': '数値で入力してください',
  'validation.minimum': '{minimum} 以上で入力してください',
  'validation.maximum': '{maximum} 以下で入力してください',
  'validation.date': '日付を選んでください',

  // ---- サーバが返した検証エラー ---------------------------------------------
  // **符号で返ってくる**（`Core/Validation/ValidationErrorCode.cs`）。
  // 文言はここで当てる
  'serverValidation.Required': '回答してください',
  'serverValidation.TooLong': '文字数が多すぎます',
  'serverValidation.UnknownChoice': '選択肢にない値が選ばれています',
  'serverValidation.MultipleValuesNotAllowed': '回答は 1 つだけ選んでください',
  'serverValidation.NotANumber': '数値で入力してください',
  'serverValidation.OutOfRange': '入力できる範囲を超えています',
  'serverValidation.NotADateTime': '日付・時刻の形式が正しくありません',
  'serverValidation.InvalidEmail': 'メールアドレスの形式で入力してください',
  'serverValidation.InvalidUrl': 'http:// または https:// で始まる URL を入力してください',
  'serverValidation.UnknownQuestion': 'この設問は受け付けられません',
  'serverValidation.AnswerNotAllowed': 'この項目は回答できません',
  'serverValidation.OtherTextNotAllowed': '「その他」を選んでいないため自由記述は送れません',
  'serverValidation.TooFewSelections': '選んだ数が足りません',
  'serverValidation.TooManySelections': '選べる数を超えています',
  'serverValidation.PatternMismatch': '指定された形式で入力してください',
  'serverValidation.TooLarge': '送信内容が大きすぎます',
  'serverValidation.ScannerUnavailable': '添付ファイルの検査ができないため、いま受け付けられません',
  'serverValidation.unknown': '入力をご確認ください',
} as const;

/** 文言の鍵。**日本語のカタログが一覧そのもの。** */
export type MessageKey = keyof typeof ja;

/**
 * 英語の文言。
 *
 * **`Record<MessageKey, string>` にしてあるので、
 * 鍵を足して訳を忘れると型検査で落ちる。**
 */
export const en: Record<MessageKey, string> = {
  'status.loading': 'Loading…',

  'rejected.notFound.title': 'Survey not found',
  'rejected.closed.title': 'Not accepting responses',
  'rejected.notStarted': 'This survey is not open for responses yet.',
  'rejected.closed': 'This survey is no longer accepting responses.',
  'rejected.suspended': 'This survey is currently paused.',
  'rejected.notFound': 'This survey could not be found. Please check the URL.',
  'rejected.rejected': 'Your response could not be accepted. Please reopen the page and try again.',
  'rejected.tooManyRequests': 'The service is busy. Please wait a moment and try again.',
  'error.badUrl.title': 'That URL is not valid',
  'error.badUrl': 'Please check the survey URL.',

  'answered.title': 'You have already responded on this device',
  'answered.canEdit': 'You can edit your previous response.',
  'answered.cannotRead': 'Your previous response could not be loaded.',
  'answered.editingNotAllowed': 'This survey does not allow editing a response.',
  'answered.answerAgain': 'Send a new response',
  'answered.answerAgainNote':
    'Choosing "Send a new response" records it separately from your previous one.',

  'completed.title': 'Your response has been received',
  'completed.thanks': 'Thank you for your time.',
  'completed.edit': 'Edit my response',
  'completed.answerAgain': 'Send another response',

  'form.progressLabel': 'Progress',
  'form.pageCount': 'Page {current} of {total}',
  'form.back': 'Back',
  'form.next': 'Next',
  'form.submit': 'Submit',
  'form.submitting': 'Submitting…',
  'form.languageLabel': 'Language',
  'form.trapLabel': 'Leave this field empty',
  'draft.found': 'An unfinished answer is saved on this device.',
  'draft.foundNote':
    'If you share this device, check the content before resuming. Choose Discard to remove it.',
  'draft.restore': 'Resume',
  'draft.discard': 'Discard',
  'draft.restored': 'Your unfinished answer has been loaded.',
  'draft.discarded': 'The draft saved on this device has been removed.',

  'question.required': 'required',
  'question.selectPlaceholder': 'Select an option',
  'question.otherText': 'Please specify',
  'question.unsupported': 'This question type is not supported yet ({type})',
  'question.ratingLabel': '{value} of {max}',
  'question.gridRowHeader': 'Item',
  'question.gridRowLabel': '{row}: {choice}',
  'question.rankingLead': 'Choose items to rank them. Choose again to remove.',
  'embed.notice':
    'This page includes content from external sites ({hosts}). Displaying it connects to those sites.',
  'embed.blocked': 'This embedded content cannot be displayed.',
  'question.rankingUnranked': 'Not ranked',
  'question.rankingRank': 'Rank {rank}',
  'question.rankingUp': 'Move up',
  'question.rankingDown': 'Move down',
  'question.rankingClear': 'Remove rank',
  'question.rankingStatus': '{label}: rank {rank} of {total}',
  'question.fileCountLimit': 'Up to {count} files',
  'question.fileSizeLimitMegabytes': 'Up to {megabytes} MB per file',
  'question.fileSizeLimitBytes': 'Up to {bytes} bytes per file',
  'question.selectionRange': 'Choose between {minimum} and {maximum} options',
  'question.selectionMinimum': 'Choose at least {minimum} options',
  'question.selectionMaximum': 'Choose up to {maximum} options',

  'submit.tooManyRequests': 'The service is busy. Please wait a moment and submit again.',
  'submit.rejected': 'Your response was not accepted. Please press "Submit" again.',
  'submit.insecureContext':
    'Please open this page over a secure connection (https). It cannot be submitted as is.',
  'submit.failed':
    'We could not submit your response. Your answers are still here — please wait a moment and try again.',

  'attachment.defaultName': 'Attachment',
  'attachment.extensionNotAllowed': '{name}: this kind of file is not accepted',
  'attachment.contentDoesNotMatchExtension': '{name}: the contents do not match the file extension',
  'attachment.tooLarge': '{name}: the file is too large',
  'attachment.totalTooLarge': 'The attachments are too large in total',
  'attachment.tooMany': 'Too many attachments',
  'attachment.invalidFileName': '{name}: the file name contains characters that cannot be used',
  'attachment.unknown': '{name}: this attachment cannot be accepted',

  'validation.required': 'Please answer this question',
  'validation.fileRequired': 'Please choose a file',
  'validation.fileCount': 'Choose at most {count} files',
  'validation.fileTooLarge': '{name}: the file is too large',
  'validation.singleValueOnly': 'Please choose only one answer',
  'validation.rowRequired': 'Please answer every row',
  'validation.duplicateRank': 'The same item has more than one rank',
  'validation.tooFewSelections': 'Choose at least {minimum} options',
  'validation.tooManySelections': 'Choose at most {maximum} options',
  'validation.pattern': 'Enter the value in the requested format',
  'validation.tooLong': 'Use at most {max} characters',
  'validation.email': 'Enter a valid email address',
  'validation.url': 'Enter a URL starting with http:// or https://',
  'validation.notANumber': 'Enter a number',
  'validation.minimum': 'Enter {minimum} or more',
  'validation.maximum': 'Enter {maximum} or less',
  'validation.date': 'Please choose a date',

  'serverValidation.Required': 'Please answer this question',
  'serverValidation.TooLong': 'That is too long',
  'serverValidation.UnknownChoice': 'That option is not one of the choices',
  'serverValidation.MultipleValuesNotAllowed': 'Please choose only one answer',
  'serverValidation.NotANumber': 'Enter a number',
  'serverValidation.OutOfRange': 'That value is out of range',
  'serverValidation.NotADateTime': 'That is not a valid date or time',
  'serverValidation.InvalidEmail': 'Enter a valid email address',
  'serverValidation.InvalidUrl': 'Enter a URL starting with http:// or https://',
  'serverValidation.UnknownQuestion': 'This question cannot be accepted',
  'serverValidation.AnswerNotAllowed': 'This item cannot be answered',
  'serverValidation.OtherTextNotAllowed':
    'You can only add free text when "Other" is selected',
  'serverValidation.TooLarge': 'What you sent is too large',
  'serverValidation.TooFewSelections': 'You have not chosen enough options',
  'serverValidation.TooManySelections': 'You have chosen too many options',
  'serverValidation.PatternMismatch': 'Enter the value in the requested format',
  'serverValidation.ScannerUnavailable':
    'Attachments cannot be checked right now, so we cannot accept this yet',
  'serverValidation.unknown': 'Please check what you entered',
};

const CATALOGS: Record<Language, Record<MessageKey, string>> = { ja, en };

/**
 * サーバが返した検証エラーの符号に対する鍵。
 *
 * **知らない符号でも落とさない。** サーバ側に符号が増えたときは、
 * 当たり障りのない文言へ落として画面を止めない。
 */
export function serverValidationKey(code: string): MessageKey {
  const key = `serverValidation.${code}`;
  return (key in ja ? key : 'serverValidation.unknown') as MessageKey;
}

/** 文言を引く関数。 */
export type Translate = (
  key: MessageKey,
  parameters?: Record<string, string | number>,
) => string;

/**
 * その言語の文言を引く関数を作る。
 *
 * **翻訳が無ければ既定の言語へ落ちる**（`_documents/多言語対応方針.md` 1 章）。
 */
export function translator(language: Language): Translate {
  const catalog = CATALOGS[language] ?? CATALOGS[DEFAULT_LANGUAGE];

  return (key, parameters) =>
    interpolate(catalog[key] || CATALOGS[DEFAULT_LANGUAGE][key], parameters, language);
}
