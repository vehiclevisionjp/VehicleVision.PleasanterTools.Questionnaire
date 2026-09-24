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

        // **言語ごとの辞書で受ける。** 3 言語目を足すときに、
        // この関数だけを直せば済むようにしておく（Issue #195）
        void AddAll(string key, IReadOnlyDictionary<string, string> byLanguage) =>
            catalog.Add(key, new LocalizedText(byLanguage));

        var german = new Dictionary<string, string>
        {
            [ServerMessageKeys.InvalidCredentials] = "Die Anmelde-ID oder der eingegebene Wert ist nicht korrekt.",
            [ServerMessageKeys.LoginIdAndPasswordRequired] = "Geben Sie Ihre Anmelde-ID und Ihr Passwort ein.",
            [ServerMessageKeys.AdministratorAlreadyExists] = "Ein Administrator wurde bereits registriert.",
            [ServerMessageKeys.LoginTemporarilyLocked] = "Zu viele Versuche. Die Anmeldung ist für eine Weile nicht verfügbar; versuchen Sie es später erneut.",
            [ServerMessageKeys.EnrollmentRestartRequired] = "Starten Sie die Registrierung erneut.",
            [ServerMessageKeys.TotpCodeMismatch] = "Dieser Code stimmt nicht überein. Prüfen Sie die Nummer in Ihrer Authenticator-App.",
            [ServerMessageKeys.TwoFactorDisabled] = "Die Zwei-Faktor-Authentifizierung ist deaktiviert. Bitten Sie Ihren Administrator, die Einstellung zu ändern.",
            [ServerMessageKeys.TwoFactorRequired] = "Die Zwei-Faktor-Authentifizierung ist erforderlich und kann daher nicht entfernt werden.",
            [ServerMessageKeys.TwoFactorNotEnrolled] = "Die Zwei-Faktor-Authentifizierung ist nicht eingerichtet.",
            [ServerMessageKeys.PasswordTooShort] = "Verwenden Sie ein Passwort mit mindestens {0} Zeichen.",
            [ServerMessageKeys.PasswordSameAsLoginId] = "Das Passwort darf nicht mit der Anmelde-ID übereinstimmen.",
            [ServerMessageKeys.PasswordPolicyMismatch] = "Das Passwort erfüllt die erforderlichen Bedingungen nicht.",
            [ServerMessageKeys.RoleNotSupported] = "Die Rolle muss Administrator, SurveyAdministrator, UserAdministrator, Editor oder Auditor sein.",
            [ServerMessageKeys.AdminUserNotFound] = "Dieser Administrator wurde nicht gefunden.",
            [ServerMessageKeys.DuplicateLoginId] = "Diese Anmelde-ID wird bereits verwendet.",
            [ServerMessageKeys.InvalidInput] = "Überprüfen Sie Ihre Eingabe.",
            [ServerMessageKeys.SelfNotAllowed] = "Sie können dies nicht für Ihr eigenes Konto tun. Bitten Sie einen anderen Administrator.",
            [ServerMessageKeys.LastAdministrator] = "Kein anderer Administrator kann sich anmelden. Fügen Sie zuerst einen weiteren Administrator hinzu und bestätigen Sie dessen Anmeldung.",
            [ServerMessageKeys.OperationTemporarilyLocked] = "Zu viele Versuche. Diese Aktion ist für eine Weile nicht verfügbar; versuchen Sie es später erneut.",
            [ServerMessageKeys.InvitationInvalid] = "Diese Einladung kann nicht verwendet werden. Bitten Sie um eine neue Einladung.",
            [ServerMessageKeys.CurrentPasswordRejected] = "Ihr aktuelles Passwort ist nicht korrekt.",
            [ServerMessageKeys.UnsupportedLanguage] = "Diese Sprache wird nicht unterstützt.",
            [ServerMessageKeys.AdminSessionNotFound] = "Diese Sitzung wurde nicht gefunden.",
            [ServerMessageKeys.CurrentSessionCannotBeRevoked] = "Die aktuelle Sitzung kann hier nicht beendet werden. Melden Sie sich stattdessen ab.",
            [ServerMessageKeys.ResponseNotificationMailSubject] = "Neue Umfrageantworten",
            [ServerMessageKeys.ResponseNotificationMailBody] = "Die Umfrage „{0}“ hat {1} neue Antwort(en) erhalten.\nZeitraum (UTC): {2} bis {3}\n\nZeigen Sie den Antwortinhalt in Pleasanter an.",
            [ServerMessageKeys.RemovedSurvey] = "Gelöschte Umfrage",
            [ServerMessageKeys.SurveyTitleRequired] = "Geben Sie einen Titel ein.",
            [ServerMessageKeys.PleasanterSiteIdRequired] = "Geben Sie die Pleasanter-Site-ID an.",
            [ServerMessageKeys.DefinitionAndMappingRequired] = "Sowohl die Definition als auch die Zuordnung sind erforderlich.",
            [ServerMessageKeys.SurveyUpdatedByOther] = "Jemand anderes hat diese Umfrage aktualisiert. Laden Sie sie neu.",
            [ServerMessageKeys.QuestionImportSourceInvalid] = "Die Quellumfrage kann nicht geöffnet werden. Prüfen Sie, ob sie gelöscht, archiviert oder geändert wurde.",
            [ServerMessageKeys.QuestionImportSelectionRequired] = "Wählen Sie mindestens eine zu importierende Frage aus.",
            [ServerMessageKeys.PublishBlockedByMapping] = "Veröffentlichung nicht möglich. Beheben Sie zuerst die Probleme in der Zuordnung.",
            [ServerMessageKeys.PublishBlockedByFlow] = "Veröffentlichung nicht möglich. Beheben Sie zuerst die Probleme in der Verzweigung.",
            [ServerMessageKeys.PublishBlockedBySettings] = "Veröffentlichung nicht möglich. Einige Fragen haben Einstellungen, die keine Antwort erfüllen kann.",
            [ServerMessageKeys.AutoReplyTestSubjectPrefix] = "[Test] ",
            [ServerMessageKeys.AutoReplyTestLoginIdNotEmail] = "Ihre Anmelde-ID ist keine E-Mail-Adresse, daher kann keine Testnachricht gesendet werden.",
            [ServerMessageKeys.AutoReplyTestMailDisabled] = "Der E-Mail-Versand ist auf dem Server nicht aktiviert, daher kann keine Testnachricht gesendet werden.",
            [ServerMessageKeys.AutoReplyTestQueueFailed] = "Die Testnachricht konnte nicht in die Warteschlange gestellt werden.",
            [ServerMessageKeys.InvitationMailSubject] = "Sie wurden zur Umfrageverwaltung eingeladen",
            [ServerMessageKeys.InvitationMailBody] = "Sie wurden zum Verwaltungsbildschirm für Umfragen eingeladen.\n\nÖffnen Sie die folgende URL und wählen Sie Ihr Passwort.\n{0}\n\nLäuft ab: {1} (UTC)\n\nWenn Sie dies nicht erwartet haben, löschen Sie diese Nachricht, ohne die URL zu öffnen.",
            [ServerMessageKeys.PublishBlockedByAutoReply] = "Veröffentlichung nicht möglich. Die Einstellungen für automatische Antworten können keine E-Mail senden.",
            [ServerMessageKeys.NoAnswerableQuestion] = "Es gibt keine Frage, die beantwortet werden kann.",
            [ServerMessageKeys.NotPublishedYet] = "Diese Umfrage wurde noch nicht veröffentlicht.",
            [ServerMessageKeys.VersionAlreadyPublished] = "Diese Version wurde bereits veröffentlicht. Laden Sie die Seite neu und versuchen Sie es erneut.",
            [ServerMessageKeys.InvalidSurveyStatus] = "Diese Aktion ist im aktuellen Zustand nicht verfügbar. Laden Sie die Seite neu.",
            [ServerMessageKeys.SurveyArchived] = "Diese Umfrage ist archiviert. Stellen Sie sie wieder her, bevor Sie Änderungen vornehmen.",
            [ServerMessageKeys.InvalidArchiveState] = "Der Archivstatus wurde bereits geändert. Laden Sie die Seite neu.",
            [ServerMessageKeys.SurveyDeleteRequiresArchive] = "Nur archivierte Umfragen können dauerhaft gelöscht werden. Archivieren Sie diese Umfrage zuerst.",
            [ServerMessageKeys.SurveyDeleteTitleMismatch] = "Der eingegebene Titel stimmt nicht mit dem Titel der Umfrage überein.",
            [ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery] = "Diese Umfrage kann nicht dauerhaft gelöscht werden, solange Antworten oder E-Mails ausstehen oder gesendet werden.",
            [ServerMessageKeys.SiteIdLockedAfterPublish] = "Die Pleasanter-Site-ID kann nach der Produktionsveröffentlichung nicht geändert werden.",
            [ServerMessageKeys.SiteIdBlockedByPendingResponses] = "Die Pleasanter-Site-ID kann nicht geändert werden, solange Antworten auf den Versand warten.",
            [ServerMessageKeys.DuplicateSiteIdMustDiffer] = "Geben Sie eine andere Pleasanter-Site-ID an als diejenige, in die die ursprüngliche Umfrage schreibt.",
            [ServerMessageKeys.SurveyIsTemplate] = "Dies ist eine Vorlage. Vorlagen können nicht veröffentlicht werden. Erstellen Sie zuerst eine Umfrage daraus.",
            [ServerMessageKeys.TemplateSourceRequired] = "Geben Sie die Umfrage an, aus der eine Vorlage erstellt werden soll.",
            [ServerMessageKeys.ThemeColorInvalid] = "Geben Sie Farben im Format #rrggbb an.",
            [ServerMessageKeys.EmbedHostNotAllowed] = "Die eingebettete URL befindet sich nicht in den zulässigen Quellen. Bitten Sie einen Administrator, sie hinzuzufügen.",
            [ServerMessageKeys.HeaderImageRejected] = "Dieses Bild kann nicht verwendet werden. Wählen Sie ein PNG-, JPEG-, GIF- oder WebP-Bild mit höchstens 2 MB.",
            [ServerMessageKeys.ContentAssetRejected] = "Dieses Asset kann nicht verwendet werden. Prüfen Sie die zulässigen Dateitypen und die Größenbeschränkung.",
            [ServerMessageKeys.ContentAssetLimitReached] = "Diese Umfrage hat das Asset-Limit erreicht.",
            [ServerMessageKeys.AssetScannerUnavailable] = "Das Asset kann nicht gespeichert werden, da die Virenprüfung nicht verfügbar ist. Wenden Sie sich an einen Administrator.",
            [ServerMessageKeys.ResponseLimitMustBePositive] = "Die Antwortgrenze muss mindestens 1 betragen. Lassen Sie das Feld für keine Begrenzung leer.",
            [ServerMessageKeys.ResponseLimitReached] = "Die Antwortgrenze wurde erreicht ({0} von {1}). Erhöhen Sie die Grenze vor der Fortsetzung.",
            [ServerMessageKeys.ResponseTokenRequired] = "Geben Sie an, welche Antwort zurückgesetzt werden soll.",
            [ServerMessageKeys.DeadLetterNotFound] = "Diese Antwort wurde nicht gefunden. Sie wurde möglicherweise bereits zurückgestellt oder bereits gesendet.",
        };

        // **2 言語ぶんの書き方は残す。** 既存の 46 件を書き換えない
        void Add(string key, string ja, string en) =>
            AddAll(
                key,
                new Dictionary<string, string>
                {
                    [SupportedLanguages.Default] = ja,
                    ["en"] = en,
                    ["de"] = german[key],
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
