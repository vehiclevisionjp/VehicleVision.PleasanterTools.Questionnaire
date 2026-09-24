using System.Collections.Frozen;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

/// <summary>サーバが画面へ返す文言。</summary>
/// <remarks>
/// <para>
/// **入れ物は <see cref="LocalizedText"/> を使い回す。**
/// ただし、画面自身の文言は未翻訳のときに <c>en</c>、<c>ja</c> の順で落とす
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
    private const string UiFallbackLanguage = "en";

    private static readonly FrozenDictionary<string, LocalizedText> Catalog = Build();

    /// <summary>カタログに載っている鍵。**テストから照合するために公開している。**</summary>
    public static IReadOnlyCollection<string> Keys => Catalog.Keys;

    /// <summary>指定した言語の文言を返す。</summary>
    /// <remarks>
    /// **知らない鍵でも落とさない。** 鍵そのものを返す。
    /// 鍵とカタログの食い違いは単体テストで先に落ちるので、
    /// ここで例外を投げても直る場所が増えるだけ。
    /// </remarks>
    public static string Get(string key, string? language)
    {
        if (!Catalog.TryGetValue(key, out var text))
        {
            return key;
        }

        var requested = SupportedLanguages.Normalize(language);
        if (text.TryGet(requested, out var translated) && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }

        if (text.TryGet(UiFallbackLanguage, out var english) && !string.IsNullOrEmpty(english))
        {
            return english;
        }

        return text.Get(LocalizedText.DefaultLanguage);
    }

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
        var zh = new Dictionary<string, string>
        {
            [ServerMessageKeys.InvalidCredentials] = "登录 ID 或输入内容不正确。",
            [ServerMessageKeys.LoginIdAndPasswordRequired] = "请输入登录 ID 和密码。",
            [ServerMessageKeys.AdministratorAlreadyExists] = "管理员已注册。",
            [ServerMessageKeys.LoginTemporarilyLocked] = "尝试次数过多，暂时无法登录。请稍后再试。",
            [ServerMessageKeys.EnrollmentRestartRequired] = "请重新开始注册。",
            [ServerMessageKeys.TotpCodeMismatch] = "代码不匹配。请确认身份验证器应用显示的号码。",
            [ServerMessageKeys.TwoFactorDisabled] = "双重身份验证已被禁用。若要注册，请请求管理员更改设置。",
            [ServerMessageKeys.TwoFactorRequired] = "双重身份验证为必需设置，因此无法解除。",
            [ServerMessageKeys.TwoFactorNotEnrolled] = "尚未注册双重身份验证。",
            [ServerMessageKeys.PasswordTooShort] = "密码至少需要 {0} 个字符。",
            [ServerMessageKeys.PasswordSameAsLoginId] = "密码不能与登录 ID 相同。",
            [ServerMessageKeys.PasswordPolicyMismatch] = "密码不符合规定的条件。",
            [ServerMessageKeys.RoleNotSupported] = "角色必须指定为 Administrator、SurveyAdministrator、UserAdministrator、Editor 或 Auditor 之一。",
            [ServerMessageKeys.AdminUserNotFound] = "未找到该管理员。",
            [ServerMessageKeys.DuplicateLoginId] = "该登录 ID 已被使用。",
            [ServerMessageKeys.InvalidInput] = "请检查输入内容。",
            [ServerMessageKeys.SelfNotAllowed] = "无法对自己的帐户执行此操作。请请求其他管理员协助。",
            [ServerMessageKeys.LastAdministrator] = "没有其他能够登录的管理员。请先添加其他管理员，并确认该管理员能够登录。",
            [ServerMessageKeys.OperationTemporarilyLocked] = "尝试次数过多，暂时无法执行此操作。请稍后再试。",
            [ServerMessageKeys.InvitationInvalid] = "该邀请无法使用。请重新发送邀请。",
            [ServerMessageKeys.CurrentPasswordRejected] = "当前密码不正确。",
            [ServerMessageKeys.UnsupportedLanguage] = "不支持该语言。",
            [ServerMessageKeys.AdminSessionNotFound] = "未找到该会话。",
            [ServerMessageKeys.CurrentSessionCannotBeRevoked] = "无法在此结束当前使用的会话。请改为注销。",
            [ServerMessageKeys.ResponseNotificationMailSubject] = "问卷有新的回答",
            [ServerMessageKeys.ResponseNotificationMailBody] = "问卷“{0}”收到了 {1} 条新回答。\n"
                + "统计期间（UTC）：{2} ～ {3}\n\n"
                + "请在 Pleasanter 中查看回答内容。",
            [ServerMessageKeys.RemovedSurvey] = "已删除的问卷",
            [ServerMessageKeys.SurveyTitleRequired] = "请输入标题。",
            [ServerMessageKeys.PleasanterSiteIdRequired] = "请指定 Pleasanter 的站点 ID。",
            [ServerMessageKeys.DefinitionAndMappingRequired] = "定义和映射均为必填项。",
            [ServerMessageKeys.SurveyUpdatedByOther] = "其他人已更新此问卷。请重新加载。",
            [ServerMessageKeys.QuestionImportSourceInvalid] = "无法打开导入来源问卷。请确认它是否已被删除、存档或更改。",
            [ServerMessageKeys.QuestionImportSelectionRequired] = "请至少选择一个要导入的问题。",
            [ServerMessageKeys.PublishBlockedByMapping] = "无法发布。请先修正映射中的问题。",
            [ServerMessageKeys.PublishBlockedByFlow] = "无法发布。请先修正分支中的问题。",
            [ServerMessageKeys.PublishBlockedBySettings] = "无法发布。某些问题的设置无法满足任何回答。",
            [ServerMessageKeys.AutoReplyTestSubjectPrefix] = "【测试发送】",
            [ServerMessageKeys.AutoReplyTestLoginIdNotEmail] = "登录 ID 不是电子邮件地址，因此无法发送测试邮件。",
            [ServerMessageKeys.AutoReplyTestMailDisabled] = "服务器端未启用邮件发送，因此无法发送测试邮件。",
            [ServerMessageKeys.AutoReplyTestQueueFailed] = "无法将测试邮件加入发送队列。",
            [ServerMessageKeys.InvitationMailSubject] = "问卷管理页面邀请",
            [ServerMessageKeys.InvitationMailBody] = "您已收到问卷管理页面的邀请。\n\n"
                + "请打开以下 URL 并设置密码。\n"
                + "{0}\n\n"
                + "到期时间：{1} (UTC)\n\n"
                + "如果您未曾预期收到此邮件，请勿打开 URL 并删除此邮件。",
            [ServerMessageKeys.PublishBlockedByAutoReply] = "无法发布。自动回复邮件的设置无法发送电子邮件。",
            [ServerMessageKeys.NoAnswerableQuestion] = "没有可回答的问题。",
            [ServerMessageKeys.NotPublishedYet] = "尚未发布。",
            [ServerMessageKeys.VersionAlreadyPublished] = "此版本已发布。请重新加载后再试。",
            [ServerMessageKeys.InvalidSurveyStatus] = "当前状态下无法执行此操作。请重新加载。",
            [ServerMessageKeys.SurveyArchived] = "此问卷已存档。请先恢复后再进行更改。",
            [ServerMessageKeys.InvalidArchiveState] = "存档状态已发生变化。请重新加载。",
            [ServerMessageKeys.SurveyDeleteRequiresArchive] = "只有已存档的问卷可以永久删除。请先存档此问卷。",
            [ServerMessageKeys.SurveyDeleteTitleMismatch] = "输入的标题与问卷标题不一致。",
            [ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery] = "仍有等待发送或正在发送的回答或邮件，因此无法永久删除。",
            [ServerMessageKeys.SiteIdLockedAfterPublish] = "正式发布后的问卷无法更改 Pleasanter 站点 ID。",
            [ServerMessageKeys.SiteIdBlockedByPendingResponses] = "仍有等待发送的回答，因此无法更改 Pleasanter 站点 ID。",
            [ServerMessageKeys.DuplicateSiteIdMustDiffer] = "请为副本指定与原问卷不同的 Pleasanter 站点 ID。",
            [ServerMessageKeys.SurveyIsTemplate] = "这是模板。模板无法发布。请先从模板创建问卷。",
            [ServerMessageKeys.TemplateSourceRequired] = "请指定要作为模板来源的问卷。",
            [ServerMessageKeys.ThemeColorInvalid] = "颜色请指定为 #rrggbb 格式。",
            [ServerMessageKeys.EmbedHostNotAllowed] = "嵌入目标不在允许的来源中。请请求管理员添加来源。",
            [ServerMessageKeys.HeaderImageRejected] = "无法使用此图像。请选择大小不超过 2 MB 的 PNG、JPEG、GIF 或 WebP 图像。",
            [ServerMessageKeys.ContentAssetRejected] = "无法使用此资源。请确认允许的文件类型和大小限制。",
            [ServerMessageKeys.ContentAssetLimitReached] = "此问卷已达到可保存资源的上限。",
            [ServerMessageKeys.AssetScannerUnavailable] = "由于无法使用病毒扫描，无法保存资源。请联系管理员。",
            [ServerMessageKeys.ResponseLimitMustBePositive] = "回答数上限必须至少为 1。若不设上限，请留空。",
            [ServerMessageKeys.ResponseLimitReached] = "回答数已达到上限（{0} / {1} 条）。请先提高上限后再继续。",
            [ServerMessageKeys.ResponseTokenRequired] = "请指定要恢复的回答。",
            [ServerMessageKeys.DeadLetterNotFound] = "未找到该回答。它可能已恢复为等待发送状态，或已发送完成。",
        };

        // **言語ごとの辞書で受ける。** 3 言語目を足すときに、
        // この関数だけを直せば済むようにしておく（Issue #195）
        void AddAll(string key, IReadOnlyDictionary<string, string> byLanguage) =>
            catalog.Add(key, new LocalizedText(byLanguage));

        // **2 言語ぶんの書き方は残す。** 既存の 46 件を書き換えない
        void Add(string key, string ja, string en) =>
            AddAll(key, new Dictionary<string, string>
            {
                [SupportedLanguages.Default] = ja,
                ["en"] = en,
                ["zh"] = zh[key],
            });

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
            ServerMessageKeys.RoleNotSupported,
            "役割は Administrator / SurveyAdministrator / UserAdministrator / Editor / Auditor のいずれかを指定してください。",
            "The role must be one of Administrator, SurveyAdministrator, UserAdministrator, Editor or Auditor.");

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

        Add(
            ServerMessageKeys.AdminSessionNotFound,
            "そのセッションは見つかりません。",
            "That session was not found.");

        Add(
            ServerMessageKeys.CurrentSessionCannotBeRevoked,
            "現在使っているセッションはここから終了できません。ログアウトしてください。",
            "The current session cannot be ended here. Sign out instead.");

        Add(
            ServerMessageKeys.ResponseNotificationMailSubject,
            "アンケートに新しい回答があります",
            "New survey responses");

        Add(
            ServerMessageKeys.ResponseNotificationMailBody,
            "アンケート「{0}」に新しい回答が {1} 件届きました。\n"
            + "集計期間（UTC）: {2} ～ {3}\n\n"
            + "回答内容は Pleasanter で確認してください。",
            "The survey \"{0}\" received {1} new response(s).\n"
            + "Period (UTC): {2} to {3}\n\n"
            + "View the response content in Pleasanter.");

        Add(
            ServerMessageKeys.RemovedSurvey,
            "削除されたアンケート",
            "Deleted survey");

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
            ServerMessageKeys.QuestionImportSourceInvalid,
            "取り込み元のアンケートを開けません。削除・アーカイブ・変更されていないか確認してください。",
            "The source survey cannot be opened. Check whether it was deleted, archived, or changed.");

        Add(
            ServerMessageKeys.QuestionImportSelectionRequired,
            "取り込む設問を 1 つ以上選んでください。",
            "Select at least one question to import.");

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

        Add(ServerMessageKeys.AutoReplyTestSubjectPrefix, "【試し送信】", "[Test] ");
        Add(
            ServerMessageKeys.AutoReplyTestLoginIdNotEmail,
            "ログイン ID がメールアドレスではないため、試し送信できません。",
            "Your sign-in ID is not an email address, so a test message cannot be sent.");
        Add(
            ServerMessageKeys.AutoReplyTestMailDisabled,
            "メールの送信がサーバ側で有効になっていないため、試し送信できません。",
            "Mail sending is not enabled on the server, so a test message cannot be sent.");
        Add(
            ServerMessageKeys.AutoReplyTestQueueFailed,
            "試し送信を送信待ちへ登録できませんでした。",
            "The test message could not be queued.");

        // ---- 招待メール（Issue #189）----------------------------------------
        // **平文で送る。** 書式は持たない（OutgoingMail が text/plain）
        Add(
            ServerMessageKeys.InvitationMailSubject,
            "アンケート管理画面への招待",
            "You have been invited to the questionnaire admin");

        Add(
            ServerMessageKeys.InvitationMailBody,
            "アンケートの管理画面への招待が届いています。\n\n"
            + "次の URL を開き、パスワードを決めてください。\n"
            + "{0}\n\n"
            + "期限: {1} (UTC)\n\n"
            + "このメールに心当たりが無ければ、URL を開かずに破棄してください。",
            "You have been invited to the questionnaire admin screen.\n\n"
            + "Open the following URL and choose your password.\n"
            + "{0}\n\n"
            + "Expires: {1} (UTC)\n\n"
            + "If you were not expecting this, discard this message without opening the URL.");

        Add(
            ServerMessageKeys.PublishBlockedByAutoReply,
            "公開できません。自動返信メールの設定では、メールを送れません。",
            "Cannot publish. The auto-reply settings cannot send an email.");

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
            ServerMessageKeys.InvalidSurveyStatus,
            "現在の状態ではこの操作を行えません。再読み込みしてください。",
            "This action is not available in the current state. Reload the page.");

        Add(
            ServerMessageKeys.SurveyArchived,
            "このアンケートはアーカイブ済みです。復元してから変更してください。",
            "This survey is archived. Restore it before making changes.");

        Add(
            ServerMessageKeys.InvalidArchiveState,
            "アーカイブの状態がすでに変わっています。再読み込みしてください。",
            "The archive state has already changed. Reload the page.");

        Add(
            ServerMessageKeys.SurveyDeleteRequiresArchive,
            "完全に削除できるのはアーカイブ済みのアンケートだけです。先にアーカイブしてください。",
            "Only archived surveys can be permanently deleted. Archive this survey first.");

        Add(
            ServerMessageKeys.SurveyDeleteTitleMismatch,
            "入力した題名がアンケートの題名と一致しません。",
            "The title you entered does not match the survey title.");

        Add(
            ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery,
            "送信待ちまたは送信中の回答・メールが残っているため、完全に削除できません。",
            "This survey cannot be permanently deleted while responses or mail are pending or being sent.");

        Add(
            ServerMessageKeys.SiteIdLockedAfterPublish,
            "本公開したアンケートの Pleasanter サイト ID は変更できません。",
            "The Pleasanter site ID cannot be changed after production publication.");

        Add(
            ServerMessageKeys.SiteIdBlockedByPendingResponses,
            "送信待ちの回答が残っているため、Pleasanter サイト ID を変更できません。",
            "The Pleasanter site ID cannot be changed while responses are waiting to be sent.");

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

        Add(
            ServerMessageKeys.ContentAssetRejected,
            "この配布物は使用できません。許可された形式と容量を確認してください。",
            "That asset cannot be used. Check the allowed file types and size limit.");

        Add(
            ServerMessageKeys.ContentAssetLimitReached,
            "このアンケートへ保存できる配布物の上限に達しています。",
            "This survey has reached its asset limit.");

        Add(
            ServerMessageKeys.AssetScannerUnavailable,
            "ウイルス検査を利用できないため、配布物を保存できません。管理者へ連絡してください。",
            "The asset cannot be saved because virus scanning is unavailable. Contact an administrator.");

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
