using System.Collections.Frozen;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

/// <summary>サーバが画面へ返す文言。</summary>
/// <remarks>
/// <para>
/// **入れ物は <see cref="LocalizedText"/> を使い回す。**
/// 未翻訳のときに <c>ja</c> へ落ちる決まりを、設問と画面の文言で 2 通り持たない
/// （<c>_documents/多言語対応方針.md</c> 1 章）。
/// </para>
/// <para>
/// **ここに出てくるのは管理画面向けの文言だけ。**
/// 回答画面へ返す応答は理由の符号（<c>notStarted</c> など）しか持たず、
/// 文言はブラウザの中で組み立てる。**サーバは回答者の言語を知らない**
/// （<c>_documents/多言語対応方針.md</c> 3 章）。
/// </para>
/// </remarks>
public static class ServerMessages
{
    private static readonly FrozenDictionary<string, LocalizedText> Catalog = Build();

    /// <summary>カタログに載っている鍵。**テストから照合するために公開している。**</summary>
    public static IReadOnlyCollection<string> Keys => Catalog.Keys;

    /// <summary>指定した言語の文言を返す。</summary>
    /// <remarks>
    /// **知らない鍵でも落とさない。** 鍵そのものを返す。
    /// 鍵とカタログの食い違いは単体テストで先に落ちるので、
    /// ここで例外を投げても直る場所が増えるだけ。
    /// </remarks>
    public static string Get(string key, string? language) =>
        Catalog.TryGetValue(key, out var text) ? text.Get(language) : key;

    /// <summary>差し込みのある文言を返す。</summary>
    /// <remarks>
    /// **書式は言語に依存させない**（<see cref="CultureInfo.InvariantCulture"/>）。
    /// ここで差し込むのは設定値の数字であって、読み手向けに整形する数量ではない。
    /// </remarks>
    public static string Get(string key, string? language, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, Get(key, language), arguments);

    private static FrozenDictionary<string, LocalizedText> Build()
    {
        var catalog = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);

        void Add(string key, string ja, string en) =>
            catalog.Add(key, new LocalizedText(new Dictionary<string, string>
            {
                [SupportedLanguages.Default] = ja,
                ["en"] = en,
            }));

        // ---- 認証 -----------------------------------------------------------
        Add(
            ServerMessageKeys.InvalidCredentials,
            "ログイン ID または入力内容が正しくありません。",
            "The sign-in ID or the value you entered is not correct.");

        Add(
            ServerMessageKeys.LoginIdAndPasswordRequired,
            "ログイン ID と合言葉を入力してください。",
            "Enter your sign-in ID and passphrase.");

        Add(
            ServerMessageKeys.AdministratorAlreadyExists,
            "管理者は既に登録されています。",
            "An administrator has already been registered.");

        Add(
            ServerMessageKeys.LoginTemporarilyLocked,
            "試行が続いたため、しばらくログインできません。時間を置いてお試しください。",
            "Too many attempts. Sign-in is unavailable for a while; please try again later.");

        Add(
            ServerMessageKeys.EnrollmentRestartRequired,
            "登録をやり直してください。",
            "Start the registration over.");

        Add(
            ServerMessageKeys.TotpCodeMismatch,
            "数字が合いません。認証アプリの表示をご確認ください。",
            "That code does not match. Check the number shown in your authenticator app.");

        Add(
            ServerMessageKeys.PasswordTooShort,
            "合言葉は {0} 文字以上にしてください。",
            "Use a passphrase of at least {0} characters.");

        // ---- 管理者の管理 ---------------------------------------------------
        Add(
            ServerMessageKeys.RoleMustBeEditorOrAdministrator,
            "役割は Editor か Administrator を指定してください。",
            "The role must be either Editor or Administrator.");

        Add(
            ServerMessageKeys.AdminUserNotFound,
            "その管理者は見つかりません。",
            "That administrator was not found.");

        Add(
            ServerMessageKeys.DuplicateLoginId,
            "そのログイン ID は既に使われています。",
            "That sign-in ID is already taken.");

        Add(
            ServerMessageKeys.InvalidInput,
            "入力をご確認ください。",
            "Check what you entered.");

        Add(
            ServerMessageKeys.SelfNotAllowed,
            "自分自身には行えません。別の管理者に依頼してください。",
            "You cannot do this to your own account. Ask another administrator.");

        Add(
            ServerMessageKeys.LastAdministrator,
            "他にログインできる管理者が居ません。"
            + "先に別の管理者を追加し、その管理者がログインできることを確かめてください。",
            "No other administrator can sign in. Add another administrator first "
            + "and confirm that they can sign in.");

        Add(
            ServerMessageKeys.OperationTemporarilyLocked,
            "試行が続いたため、しばらく操作できません。時間を置いてお試しください。",
            "Too many attempts. This action is unavailable for a while; please try again later.");

        Add(
            ServerMessageKeys.InvitationInvalid,
            "招待が使えません。招待をやり直してください。",
            "That invitation cannot be used. Ask for a new invitation.");

        Add(
            ServerMessageKeys.CurrentPasswordRejected,
            "今の合言葉が正しくありません。",
            "Your current passphrase is not correct.");

        Add(
            ServerMessageKeys.UnsupportedLanguage,
            "対応していない言語です。",
            "That language is not supported.");

        // ---- アンケート -----------------------------------------------------
        Add(
            ServerMessageKeys.SurveyTitleRequired,
            "題名を入力してください。",
            "Enter a title.");

        Add(
            ServerMessageKeys.PleasanterSiteIdRequired,
            "Pleasanter のサイト ID を指定してください。",
            "Specify the Pleasanter site ID.");

        Add(
            ServerMessageKeys.DefinitionAndMappingRequired,
            "定義とマッピングの両方が要ります。",
            "Both the definition and the mapping are required.");

        Add(
            ServerMessageKeys.SurveyUpdatedByOther,
            "他の人がこのアンケートを更新しました。読み直してください。",
            "Someone else updated this survey. Reload it.");

        Add(
            ServerMessageKeys.PublishBlockedByMapping,
            "公開できません。マッピングの不備を直してください。",
            "Cannot publish. Fix the problems in the mapping first.");

        Add(
            ServerMessageKeys.PublishBlockedByFlow,
            "公開できません。分岐の不備を直してください。",
            "Cannot publish. Fix the problems in the branching first.");

        Add(
            ServerMessageKeys.NoAnswerableQuestion,
            "回答できる設問がありません。",
            "There is no question that can be answered.");

        Add(
            ServerMessageKeys.NotPublishedYet,
            "まだ公開されていません。",
            "This survey has not been published yet.");

        Add(
            ServerMessageKeys.VersionAlreadyPublished,
            "この版は既に公開されています。読み直してからもう一度お試しください。",
            "This version has already been published. Reload and try again.");

        Add(
            ServerMessageKeys.DuplicateSiteIdMustDiffer,
            "複製先には、元とは別の Pleasanter のサイト ID を指定してください。",
            "Specify a Pleasanter site ID other than the one the original survey writes to.");

        Add(
            ServerMessageKeys.SurveyIsTemplate,
            "これはテンプレートです。テンプレートは公開できません。"
                + "テンプレートからアンケートを作ってください。",
            "This is a template. Templates cannot be published. Create a survey from it first.");

        // ---- テンプレート ---------------------------------------------------
        Add(
            ServerMessageKeys.TemplateSourceRequired,
            "テンプレートの元にするアンケートを指定してください。",
            "Specify the survey to make a template from.");

        // ---- テーマ ---------------------------------------------------------
        Add(
            ServerMessageKeys.ThemeColorInvalid,
            "色は #rrggbb の形で指定してください。",
            "Specify colors in the #rrggbb form.");

        Add(
            ServerMessageKeys.HeaderImageRejected,
            "この画像は使えません。PNG・JPEG・GIF・WebP の 2 MB 以内の画像を選んでください。",
            "That image cannot be used. Choose a PNG, JPEG, GIF or WebP image of up to 2 MB.");

        // ---- 回答数の上限 ---------------------------------------------------
        Add(
            ServerMessageKeys.ResponseLimitMustBePositive,
            "回答数の上限は 1 以上にしてください。上限を設けない場合は空欄にしてください。",
            "The response limit must be at least 1. Leave it empty for no limit.");

        Add(
            ServerMessageKeys.ResponseLimitReached,
            "回答数が上限に達しています（{0} / {1} 件）。"
            + "再開するには、先に上限を引き上げてください。",
            "The response limit has been reached ({0} of {1}). "
            + "Raise the limit before resuming.");

        // ---- 送信状況 -------------------------------------------------------
        Add(
            ServerMessageKeys.ResponseTokenRequired,
            "戻す回答を指定してください。",
            "Specify which response to put back.");

        Add(
            ServerMessageKeys.DeadLetterNotFound,
            "その回答は見つかりません。既に送信待ちへ戻されたか、送信できた可能性があります。",
            "That response was not found. It may have already been put back, or it may have been sent.");

        return catalog.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
