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

    public const string TotpCodeMismatch = "auth.totpCodeMismatch";

    /// <summary>合言葉が短い。**最低の長さを差し込む**（`{0}`）。</summary>
    public const string PasswordTooShort = "auth.passwordTooShort";

    // ---- 管理者の管理 -------------------------------------------------------

    public const string RoleMustBeEditorOrAdministrator = "users.roleMustBeEditorOrAdministrator";

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

    // ---- アンケート ---------------------------------------------------------

    public const string SurveyTitleRequired = "surveys.titleRequired";

    public const string PleasanterSiteIdRequired = "surveys.pleasanterSiteIdRequired";

    public const string DefinitionAndMappingRequired = "surveys.definitionAndMappingRequired";

    public const string SurveyUpdatedByOther = "surveys.updatedByOther";

    public const string PublishBlockedByMapping = "surveys.publishBlockedByMapping";

    public const string NoAnswerableQuestion = "surveys.noAnswerableQuestion";

    /// <summary>分岐が壊れていて公開できない。</summary>
    public const string PublishBlockedByFlow = "surveys.publishBlockedByFlow";

    public const string NotPublishedYet = "surveys.notPublishedYet";

    /// <summary>その版は既にある。**同時に 2 人が公開を押した場合など。**</summary>
    public const string VersionAlreadyPublished = "surveys.versionAlreadyPublished";

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
    /// ヘッダ画像を受け付けられない。
    /// **理由の内訳は文言にしない**（拡張子・中身・大きさのどれで落ちたかは別で返す）。
    /// </summary>
    public const string HeaderImageRejected = "surveys.headerImageRejected";
    // ---- 送信状況 -----------------------------------------------------------

    /// <summary>戻す回答を指定していない。</summary>
    public const string ResponseTokenRequired = "outbox.responseTokenRequired";

    /// <summary>
    /// 戻す先が見つからない。**既に誰かが戻した / 送信できて消えた場合を含む。**
    /// </summary>
    public const string DeadLetterNotFound = "outbox.deadLetterNotFound";
}
