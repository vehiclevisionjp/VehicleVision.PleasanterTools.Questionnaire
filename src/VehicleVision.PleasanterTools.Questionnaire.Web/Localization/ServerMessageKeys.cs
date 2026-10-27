namespace VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

/// <summary>サーバが画面へ返す文言の鍵。</summary>
/// <remarks>
/// <para>
/// **鍵はここにだけ書く。** 呼ぶ側は文字列を直接書かない。
/// </para>
/// <para>
/// **ここの定数と <see cref="ServerMessages"/> のカタログは
/// 厳密に一致していなければならない。** 定数を足してカタログへ入れ忘れても、
/// カタログにだけあって定数が無くても、単体テスト
/// （<c>ServerMessagesTests</c>）で落ちる。
/// **足し忘れに気付ける形にしておくのがこの分け方の目的**
/// （<c>_documents/多言語対応方針.md</c> 4 章）。
/// </para>
/// </remarks>
public static class ServerMessageKeys
{
    // ---- 認証 ---------------------------------------------------------------

    /// <summary>**段階を区別しない文言。** 利用者名の総当たりに使わせない。</summary>
    public const string InvalidCredentials = "auth.invalidCredentials";

    public const string LoginIdAndPasswordRequired = "auth.loginIdAndPasswordRequired";

    public const string AdministratorAlreadyExists = "auth.administratorAlreadyExists";

    public const string LoginTemporarilyLocked = "auth.loginTemporarilyLocked";

    public const string EnrollmentRestartRequired = "auth.enrollmentRestartRequired";

    public const string TotpAlreadyEnrolled = "auth.totpAlreadyEnrolled";

    public const string TotpCodeMismatch = "auth.totpCodeMismatch";

    /// <summary>2 要素認証を無効にしているため、登録できない。</summary>
    public const string TwoFactorDisabled = "auth.twoFactorDisabled";

    /// <summary>2 要素認証が必須なので、自分では解除できない。</summary>
    public const string TwoFactorRequired = "auth.twoFactorRequired";

    /// <summary>2 要素認証を登録していない。</summary>
    public const string TwoFactorNotEnrolled = "auth.twoFactorNotEnrolled";

    /// <summary>パスワードが短い。**最低の長さを差し込む**（`{0}`）。</summary>
    public const string PasswordTooShort = "auth.passwordTooShort";

    /// <summary>パスワードがログイン ID と同じ。</summary>
    public const string PasswordSameAsLoginId = "auth.passwordSameAsLoginId";

    /// <summary>設定された条件に合わない。**文言を書いていない条件のときに使う。**</summary>
    public const string PasswordPolicyMismatch = "auth.passwordPolicyMismatch";

    // ---- 管理者の管理 -------------------------------------------------------

    public const string RoleNotSupported = "users.roleNotSupported";

    public const string AdminUserNotFound = "users.notFound";

    public const string DuplicateLoginId = "users.duplicateLoginId";

    public const string InvalidInput = "users.invalidInput";

    public const string SelfNotAllowed = "users.selfNotAllowed";

    public const string LastAdministrator = "users.lastAdministrator";

    public const string OperationTemporarilyLocked = "users.operationTemporarilyLocked";

    /// <summary>**理由を分けない。** トークンの当たり外れを外から確かめさせない。</summary>
    public const string InvitationInvalid = "users.invitationInvalid";

    public const string CurrentPasswordRejected = "users.currentPasswordRejected";

    public const string UnsupportedLanguage = "users.unsupportedLanguage";

    public const string AdminSessionNotFound = "sessions.notFound";

    public const string CurrentSessionCannotBeRevoked = "sessions.currentCannotBeRevoked";

    // ---- アンケート ---------------------------------------------------------

    public const string SurveyTitleRequired = "surveys.titleRequired";

    public const string PleasanterSiteIdRequired = "surveys.pleasanterSiteIdRequired";

    public const string DefinitionAndMappingRequired = "surveys.definitionAndMappingRequired";

    public const string SurveyUpdatedByOther = "surveys.updatedByOther";

    public const string QuestionImportSourceInvalid = "surveys.questionImportSourceInvalid";

    public const string QuestionImportSelectionRequired = "surveys.questionImportSelectionRequired";

    public const string PublishBlockedByMapping = "surveys.publishBlockedByMapping";

    public const string NoAnswerableQuestion = "surveys.noAnswerableQuestion";

    /// <summary>分岐が壊れていて公開できない。</summary>
    public const string PublishBlockedByFlow = "surveys.publishBlockedByFlow";

    /// <summary>設問の設定が矛盾していて公開できない（Issue #101）。</summary>
    public const string PublishBlockedBySettings = "surveys.publishBlockedBySettings";

    /// <summary>完了メールの試し送信で件名へ付ける印（Issue #319）。</summary>
    public const string AutoReplyTestSubjectPrefix = "autoReply.test.subjectPrefix";

    public const string AutoReplyTestLoginIdNotEmail = "autoReply.test.loginIdNotEmail";

    public const string AutoReplyTestMailDisabled = "autoReply.test.mailDisabled";

    public const string AutoReplyTestQueueFailed = "autoReply.test.queueFailed";

    /// <summary>招待メールの件名（Issue #189）。</summary>
    public const string InvitationMailSubject = "invitation.mail.subject";

    /// <summary>招待メールの本文（Issue #189）。**{0} が URL、{1} が期限。**</summary>
    public const string InvitationMailBody = "invitation.mail.body";

    /// <summary>回答通知メールの件名（Issue #357）。</summary>
    public const string ResponseNotificationMailSubject = "responseNotification.mail.subject";

    /// <summary>
    /// 回答通知メールの本文（Issue #357）。
    /// **{0} がアンケート名、{1} が件数、{2} と {3} が UTC の集計期間。**
    /// </summary>
    public const string ResponseNotificationMailBody = "responseNotification.mail.body";

    /// <summary>削除済みで題名を取得できないアンケートの表示名。</summary>
    public const string RemovedSurvey = "responseNotification.mail.removedSurvey";

    /// <summary>自動返信の設定が送れる形になっていなくて公開できない（Issue #189）。</summary>
    public const string PublishBlockedByAutoReply = "surveys.publishBlockedByAutoReply";

    public const string NotPublishedYet = "surveys.notPublishedYet";

    /// <summary>その版は既にある。**同時に 2 人が公開を押した場合など。**</summary>
    public const string VersionAlreadyPublished = "surveys.versionAlreadyPublished";

    public const string InvalidSurveyStatus = "surveys.invalidStatus";

    /// <summary>アーカイブ済みのアンケートを変更しようとした。</summary>
    public const string SurveyArchived = "surveys.archived";

    /// <summary>既にアーカイブ済み、または既に復元済み。</summary>
    public const string InvalidArchiveState = "surveys.invalidArchiveState";

    public const string SurveyDeleteRequiresArchive = "surveys.deleteRequiresArchive";

    public const string SurveyDeleteTitleMismatch = "surveys.deleteTitleMismatch";

    public const string SurveyDeleteBlockedByPendingDelivery =
        "surveys.deleteBlockedByPendingDelivery";

    public const string SiteIdLockedAfterPublish = "surveys.siteIdLockedAfterPublish";

    public const string SiteIdBlockedByPendingResponses = "surveys.siteIdBlockedByPendingResponses";

    /// <summary>複製先に元と同じサイトを指定した。**1 アンケート = 1 サイト。**</summary>
    public const string DuplicateSiteIdMustDiffer = "surveys.duplicateSiteIdMustDiffer";

    /// <summary>
    /// テンプレートに対してアンケートの操作をしようとした（Issue #58）。
    /// **テンプレートは書き込み先を持たないので公開できない。**
    /// </summary>
    public const string SurveyIsTemplate = "surveys.isTemplate";

    // ---- テンプレート -------------------------------------------------------

    /// <summary>テンプレートの元にするアンケートを指定していない。</summary>
    public const string TemplateSourceRequired = "templates.sourceRequired";
    /// テーマの色の形が違う（Issue #56）。
    /// **受け付けるのは <c>#rgb</c> と <c>#rrggbb</c> だけ。**
    /// </summary>
    public const string ThemeColorInvalid = "surveys.themeColorInvalid";

    /// <summary>
    /// 埋め込み先が、運用側の許した配信元に入っていない（Issue #104 / #107）。
    /// **どこを許すかは <c>QUESTIONNAIRE_EMBED_ALLOWEDHOSTS</c> が決める。**
    /// </summary>
    public const string EmbedHostNotAllowed = "surveys.embedHostNotAllowed";

    /// <summary>
    /// ヘッダ画像を受け付けられない。
    /// **理由の内訳は文言にしない**（拡張子・中身・大きさのどれで落ちたかは別で返す）。
    /// </summary>
    public const string HeaderImageRejected = "surveys.headerImageRejected";

    /// <summary>本文・完了画面用の配布資産を受け付けられない。</summary>
    public const string ContentAssetRejected = "surveys.contentAssetRejected";

    /// <summary>アンケートへ保存できる配布資産の件数を超えている。</summary>
    public const string ContentAssetLimitReached = "surveys.contentAssetLimitReached";

    /// <summary>配布資産をウイルス検査できない。</summary>
    public const string AssetScannerUnavailable = "surveys.assetScannerUnavailable";

    /// <summary>回答数の上限に 0 以下を指定した。**上限を外すなら未指定にする。**</summary>
    public const string ResponseLimitMustBePositive = "surveys.responseLimitMustBePositive";

    /// <summary>
    /// 上限に達しているので再開できない。**受付数（`{0}`）と上限（`{1}`）を差し込む。**
    /// </summary>
    public const string ResponseLimitReached = "surveys.responseLimitReached";
    // ---- 送信状況 -----------------------------------------------------------

    /// <summary>戻す回答を指定していない。</summary>
    public const string ResponseTokenRequired = "outbox.responseTokenRequired";

    /// <summary>
    /// 戻す先が見つからない。**既に誰かが戻した / 送信できて消えた場合を含む。**
    /// </summary>
    public const string DeadLetterNotFound = "outbox.deadLetterNotFound";

    // ---- 設定・保守・アンケートの応答（Issue #605） ------------------------------

    /// <summary>設定値が要求に無い。</summary>
    public const string SettingsValuesRequired = "settings.valuesRequired";

    /// <summary>接続確認に要る Pleasanter の URL と API キーが無い。</summary>
    public const string PleasanterConnectionRequired = "settings.pleasanterConnectionRequired";

    /// <summary>Pleasanter へ接続できなかった。</summary>
    public const string PleasanterConnectionFailed = "settings.pleasanterConnectionFailed";

    /// <summary>メール設定の試験送信の宛先（ログイン ID）がメールアドレスではない。</summary>
    public const string SettingsTestSendLoginIdNotEmail = "settings.testSendLoginIdNotEmail";

    /// <summary>メンテナンスのメッセージが長すぎる。**上限（`{0}`）を差し込む。**</summary>
    public const string MaintenanceMessageTooLong = "maintenance.messageTooLong";

    /// <summary>管理対象ではない設定の鍵が指定された。**設定の鍵（`{0}`）を差し込む。**</summary>
    public const string SettingKeyNotManaged = "settings.keyNotManaged";

    /// <summary>外部設定で固定されている設定を画面から変えようとした。**項目名ではなく設定の鍵（`{0}`）で示す**（言語ごとの項目名を例外に持たせない）。</summary>
    public const string SettingFixedExternally = "settings.fixedExternally";

    /// <summary>proof-of-work の探索下限が探索上限を超えている。</summary>
    public const string ProofOfWorkRangeInvalid = "settings.proofOfWorkRangeInvalid";

    /// <summary>リンク先のサイトを Pleasanter から引けなかった。</summary>
    public const string LinkedSitesUnreachable = "surveys.linkedSitesUnreachable";

    /// <summary>落とし先の言語が対応外。</summary>
    public const string FallbackLanguageUnsupported = "surveys.fallbackLanguageUnsupported";

    /// <summary>Pleasanter がサイトの作成・更新を断り、理由も返さなかった。</summary>
    public const string PleasanterSiteWriteFailed = "surveys.pleasanterSiteWriteFailed";

    // ---- 設定の検証（Issue #607） --------------------------------------------

    /// <summary>設定値が長すぎる。**設定の鍵（`{0}`）と上限（`{1}`）を差し込む。**</summary>
    public const string SettingValueTooLong = "settings.valueTooLong";

    /// <summary>真偽値として読めない。**設定の鍵（`{0}`）を差し込む。**</summary>
    public const string SettingNotBoolean = "settings.notBoolean";

    /// <summary>整数として読めない。**設定の鍵（`{0}`）を差し込む。**</summary>
    public const string SettingNotInteger = "settings.notInteger";

    /// <summary>下限を下回っている。**設定の鍵（`{0}`）と下限（`{1}`）を差し込む。**</summary>
    public const string SettingBelowMinimum = "settings.belowMinimum";

    /// <summary>上限を上回っている。**設定の鍵（`{0}`）と上限（`{1}`）を差し込む。**</summary>
    public const string SettingAboveMaximum = "settings.aboveMaximum";

    /// <summary>メール送信を有効にするのに差出人が無い。</summary>
    public const string MailFromAddressRequired = "settings.mailFromAddressRequired";

    /// <summary>メール送信を有効にするのに公開 URL が無い。</summary>
    public const string MailBaseUrlRequired = "settings.mailBaseUrlRequired";

    /// <summary>SMTP の送信方式で SMTP サーバーが無い。</summary>
    public const string SmtpHostRequired = "settings.smtpHostRequired";

    /// <summary>Amazon SES の送信方式でリージョンが無い。</summary>
    public const string SesRegionRequired = "settings.sesRegionRequired";

    /// <summary>Azure Communication Services の送信方式でエンドポイントが無い。</summary>
    public const string AcsEndpointRequired = "settings.acsEndpointRequired";

    /// <summary>送信方式が選択肢に無い。</summary>
    public const string MailTransportInvalid = "settings.mailTransportInvalid";

    /// <summary>SMTP の暗号化が選択肢に無い。</summary>
    public const string SmtpSecurityInvalid = "settings.smtpSecurityInvalid";

    /// <summary>メールアドレスの形式が正しくない。</summary>
    public const string MailAddressInvalid = "settings.mailAddressInvalid";

    /// <summary>HTTPS の絶対 URL でない。</summary>
    public const string HttpsUrlRequired = "settings.httpsUrlRequired";

    /// <summary>http または https の絶対 URL でない。</summary>
    public const string HttpUrlRequired = "settings.httpUrlRequired";

    /// <summary>Pleasanter の URL の形が正しくない。</summary>
    public const string PleasanterUrlInvalid = "settings.pleasanterUrlInvalid";

    /// <summary>Pleasanter の API バージョンが 0 以下。</summary>
    public const string PleasanterApiVersionInvalid = "settings.pleasanterApiVersionInvalid";

    /// <summary>API キー利用者のタイムゾーンが空。</summary>
    public const string PleasanterTimeZoneRequired = "settings.pleasanterTimeZoneRequired";

    /// <summary>API キー利用者のタイムゾーンを解決できない。</summary>
    public const string PleasanterTimeZoneNotFound = "settings.pleasanterTimeZoneNotFound";

    /// <summary>既定のタイムゾーンが空。</summary>
    public const string DefaultTimeZoneRequired = "settings.defaultTimeZoneRequired";

    /// <summary>タイムゾーン ID が見つからない。**指定された値（`{0}`）を差し込む。**</summary>
    public const string TimeZoneIdUnresolved = "settings.timeZoneIdUnresolved";

    /// <summary>タイムゾーン ID が壊れている。**指定された値（`{0}`）を差し込む。**</summary>
    public const string TimeZoneIdInvalid = "settings.timeZoneIdInvalid";

    /// <summary>配布資産の許可拡張子が空。</summary>
    public const string AssetExtensionsEmpty = "settings.assetExtensionsEmpty";

    /// <summary>配布資産へ許可できない拡張子。**拡張子（`{0}`）を差し込む。**</summary>
    public const string AssetExtensionNotAllowed = "settings.assetExtensionNotAllowed";

    /// <summary>回答添付の許可拡張子が空。</summary>
    public const string AttachmentExtensionsEmpty = "settings.attachmentExtensionsEmpty";

    /// <summary>回答画面の配色固定が System・Light・Dark のどれでもない。</summary>
    public const string ColorSchemeInvalid = "settings.colorSchemeInvalid";

    /// <summary>埋め込み先のホストの書き方が正しくない。**区切りは半角のカンマ**（コードがそう分けている）。</summary>
    public const string EmbedHostsInvalid = "settings.embedHostsInvalid";
}
