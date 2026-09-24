export const ja = {
  // ---- 画面の状態 ----------------------------------------------------------
  'status.loading': '読み込んでいます…',
  'test.banner': 'これはテスト公開中のアンケートです。送信した回答は Pleasanter へ実際に登録されます。',

  // ---- 受け付けられない -----------------------------------------------------
  'rejected.notFound.title': 'アンケートが見つかりません',
  'rejected.closed.title': '受付時間外です',
  'rejected.notStarted': 'このアンケートはまだ受付を開始していません。',
  'rejected.closed': 'このアンケートの受付は終了しました。',
  'rejected.suspended': 'このアンケートは現在受付を停止しています。',
  'rejected.embeddingNotAllowed.title': '埋め込みでは回答できません',
  'rejected.embeddingNotAllowed': 'このアンケートは埋め込みでは回答できません。別の画面で開いてください。',
  'rejected.embeddingNotAllowed.open': '別の画面で開く',
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
  'assetHistory.notice':
    '資料を受け取ると、受け取った資料と日時を回答に結び付けて記録します。送信元 IP や端末の情報は記録しません。',

  // ---- 回答 -----------------------------------------------------------------
  'form.progressLabel': '回答の進み具合',
  'form.pageCount': '{current} / {total} ページ',
  'form.back': '戻る',
  'form.next': '次へ',
  'form.submit': '送信する',
  'form.submitting': '送信しています…',
  'form.languageLabel': '言語',
  'form.trapLabel': 'この欄は入力しないでください',
  'readability.group': '読みやすさ',
  'readability.fontSize': '文字',
  'readability.fontSize.standard': '標準',
  'readability.fontSize.large': '大',
  'readability.fontSize.extraLarge': '特大',
  'readability.colorMode': '配色',
  'readability.colorMode.creator': '作成者のテーマ',
  'readability.colorMode.highContrast': '高コントラスト',
  'readability.colorMode.dark': 'ダーク',
  // ---- アクセス解析の告知（Issue #162）----------------------------------------
  // **伏せない。** 外部サービスへ送っていることは、送っている画面で伝える
  'analytics.notice': 'このページでは、利用状況の計測に外部サービス（{provider}）を使用しています。',
  // ---- 外部の CAPTCHA（Issue #164）--------------------------------------------
  'captcha.notice': 'このページでは、機械的な送信を防ぐために外部サービス（{provider}）を使用しています。',
  'captcha.required': '「私はロボットではありません」の確認を済ませてください。',
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
  'question.normalizationNotice': '入力後、設定に従って文字の全角・半角や前後の空白を自動で変換します。',

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
  'validation.boolean': 'チェックの状態が正しくありません',
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
  'serverValidation.NotABoolean': 'チェックの状態が正しくありません',
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
