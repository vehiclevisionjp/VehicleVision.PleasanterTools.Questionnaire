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
            "ログイン ID とパスワードを入力してください。",
            "Enter your sign-in ID and password.");

        Add(
            ServerMessageKeys.AdministratorAlreadyExists,
            "管理者はすでに登録されています。",
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
            "コードが一致しません。認証アプリの表示をご確認ください。",
            "That code does not match. Check the number shown in your authenticator app.");

        Add(
            ServerMessageKeys.TwoFactorDisabled,
            "2 要素認証は無効に設定されています。登録するには、管理者に設定の変更を依頼してください。",
            "Two-factor authentication is turned off. Ask your operator to change the setting first.");

        Add(
            ServerMessageKeys.TwoFactorRequired,
            "2 要素認証が必須に設定されているため、解除できません。",
            "Two-factor authentication is required, so it cannot be removed.");

        Add(
            ServerMessageKeys.TwoFactorNotEnrolled,
            "2 要素認証は登録されていません。",
            "Two-factor authentication is not set up.");

        Add(
            ServerMessageKeys.PasswordTooShort,
            "パスワードは {0} 文字以上にしてください。",
            "Use a password of at least {0} characters.");

        Add(
            ServerMessageKeys.PasswordSameAsLoginId,
            "パスワードにログイン ID と同じ文字列は使えません。",
            "The password cannot be the same as the sign-in ID.");

        Add(
            ServerMessageKeys.PasswordPolicyMismatch,
            "パスワードが決められた条件を満たしていません。",
            "The password does not meet the required conditions.");

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
            "そのログイン ID はすでに使われています。",
            "That sign-in ID is already taken.");

        Add(
            ServerMessageKeys.InvalidInput,
            "入力をご確認ください。",
            "Check what you entered.");

        Add(
            ServerMessageKeys.SelfNotAllowed,
            "自分自身に対しては実行できません。別の管理者に依頼してください。",
            "You cannot do this to your own account. Ask another administrator.");

        Add(
            ServerMessageKeys.LastAdministrator,
            "他にログインできる管理者がいません。"
            + "先に別の管理者を追加し、その管理者がログインできることを確かめてください。",
            "No other administrator can sign in. Add another administrator first "
            + "and confirm that they can sign in.");

        Add(
            ServerMessageKeys.OperationTemporarilyLocked,
            "試行が続いたため、しばらく操作できません。時間を置いてお試しください。",
            "Too many attempts. This action is unavailable for a while; please try again later.");

        Add(
            ServerMessageKeys.InvitationInvalid,
            "招待が使用できません。招待をやり直してください。",
            "That invitation cannot be used. Ask for a new invitation.");

        Add(
            ServerMessageKeys.CurrentPasswordRejected,
            "今のパスワードが正しくありません。",
            "Your current password is not correct.");

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
            "定義と割り当ての両方が必要です。",
            "Both the definition and the mapping are required.");

        Add(
            ServerMessageKeys.SurveyUpdatedByOther,
            "他の人がこのアンケートを更新しました。再読み込みしてください。",
            "Someone else updated this survey. Reload it.");

        Add(
            ServerMessageKeys.PublishBlockedByMapping,
            "公開できません。割り当ての不備を修正してください。",
            "Cannot publish. Fix the problems in the mapping first.");

        Add(
            ServerMessageKeys.PublishBlockedByFlow,
            "公開できません。分岐の不備を修正してください。",
            "Cannot publish. Fix the problems in the branching first.");

        Add(
            ServerMessageKeys.PublishBlockedBySettings,
            "公開できません。設問の設定に、回答できない指定があります。",
            "Cannot publish. Some questions have settings that no answer can satisfy.");

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
            "この版はすでに公開されています。再読み込みしてから、もう一度お試しください。",
            "This version has already been published. Reload and try again.");

        Add(
            ServerMessageKeys.DuplicateSiteIdMustDiffer,
            "複製先には、元とは別の Pleasanter のサイト ID を指定してください。",
            "Specify a Pleasanter site ID other than the one the original survey writes to.");

        Add(
            ServerMessageKeys.SurveyIsTemplate,
            "これはテンプレートです。テンプレートは公開できません。"
                + "テンプレートからアンケートを作成してください。",
            "This is a template. Templates cannot be published. Create a survey from it first.");

        // ---- テンプレート ---------------------------------------------------
        Add(
            ServerMessageKeys.TemplateSourceRequired,
            "テンプレートの元にするアンケートを指定してください。",
            "Specify the survey to make a template from.");

        // ---- テーマ ---------------------------------------------------------
        Add(
            ServerMessageKeys.ThemeColorInvalid,
            "色は #rrggbb の形式で指定してください。",
            "Specify colors in the #rrggbb form.");

        Add(
            ServerMessageKeys.EmbedHostNotAllowed,
            "埋め込み先が、許可された配信元に入っていません。管理者へ配信元の追加を依頼してください。",
            "The embedded URL is not in the allowed sources. Ask an administrator to add it.");

        Add(
            ServerMessageKeys.HeaderImageRejected,
            "この画像は使用できません。PNG・JPEG・GIF・WebP の 2 MB 以内の画像を選んでください。",
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
            "その回答は見つかりません。すでに送信待ちに戻されたか、送信が完了した可能性があります。",
            "That response was not found. It may have already been put back, or it may have been sent.");

        return catalog.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
