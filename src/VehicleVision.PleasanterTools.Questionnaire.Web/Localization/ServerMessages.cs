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
        var spanish = new Dictionary<string, string>
        {
            [ServerMessageKeys.InvalidCredentials] = "El ID de inicio de sesión o el valor introducido no es correcto.",
            [ServerMessageKeys.LoginIdAndPasswordRequired] = "Introduzca su ID de inicio de sesión y contraseña.",
            [ServerMessageKeys.AdministratorAlreadyExists] = "Ya se ha registrado un administrador.",
            [ServerMessageKeys.LoginTemporarilyLocked] = "Demasiados intentos. El inicio de sesión no está disponible durante un tiempo; inténtelo de nuevo más tarde.",
            [ServerMessageKeys.EnrollmentRestartRequired] = "Vuelva a iniciar el registro.",
            [ServerMessageKeys.TotpCodeMismatch] = "El código no coincide. Compruebe el número mostrado en su aplicación de autenticación.",
            [ServerMessageKeys.TwoFactorDisabled] = "La autenticación de dos factores está desactivada. Pida a su administrador que cambie la configuración para registrarla.",
            [ServerMessageKeys.TwoFactorRequired] = "La autenticación de dos factores es obligatoria, por lo que no se puede eliminar.",
            [ServerMessageKeys.TwoFactorNotEnrolled] = "La autenticación de dos factores no está configurada.",
            [ServerMessageKeys.PasswordTooShort] = "Use una contraseña de al menos {0} caracteres.",
            [ServerMessageKeys.PasswordSameAsLoginId] = "La contraseña no puede ser igual al ID de inicio de sesión.",
            [ServerMessageKeys.PasswordPolicyMismatch] = "La contraseña no cumple las condiciones requeridas.",
            [ServerMessageKeys.RoleNotSupported] = "El rol debe ser Administrator, SurveyAdministrator, UserAdministrator, Editor o Auditor.",
            [ServerMessageKeys.AdminUserNotFound] = "No se encontró ese administrador.",
            [ServerMessageKeys.DuplicateLoginId] = "Ese ID de inicio de sesión ya está en uso.",
            [ServerMessageKeys.InvalidInput] = "Compruebe lo que ha introducido.",
            [ServerMessageKeys.SelfNotAllowed] = "No puede hacer esto en su propia cuenta. Pida ayuda a otro administrador.",
            [ServerMessageKeys.LastAdministrator] = "No hay ningún otro administrador que pueda iniciar sesión. Primero agregue otro administrador y confirme que puede iniciar sesión.",
            [ServerMessageKeys.OperationTemporarilyLocked] = "Demasiados intentos. Esta acción no está disponible durante un tiempo; inténtelo de nuevo más tarde.",
            [ServerMessageKeys.InvitationInvalid] = "No se puede usar esta invitación. Solicite una nueva invitación.",
            [ServerMessageKeys.CurrentPasswordRejected] = "Su contraseña actual no es correcta.",
            [ServerMessageKeys.UnsupportedLanguage] = "Ese idioma no es compatible.",
            [ServerMessageKeys.AdminSessionNotFound] = "No se encontró esa sesión.",
            [ServerMessageKeys.CurrentSessionCannotBeRevoked] = "La sesión actual no se puede cerrar desde aquí. Cierre sesión en su lugar.",
            [ServerMessageKeys.ResponseNotificationMailSubject] = "Nuevas respuestas de encuesta",
            [ServerMessageKeys.ResponseNotificationMailBody] = "La encuesta \"{0}\" recibió {1} respuesta(s) nueva(s).\n"
                + "Período (UTC): de {2} a {3}\n\n"
                + "Consulte el contenido de las respuestas en Pleasanter.",
            [ServerMessageKeys.RemovedSurvey] = "Encuesta eliminada",
            [ServerMessageKeys.SurveyTitleRequired] = "Introduzca un título.",
            [ServerMessageKeys.PleasanterSiteIdRequired] = "Especifique el ID del sitio de Pleasanter.",
            [ServerMessageKeys.DefinitionAndMappingRequired] = "Se requieren tanto la definición como la asignación.",
            [ServerMessageKeys.SurveyUpdatedByOther] = "Otra persona actualizó esta encuesta. Vuelva a cargarla.",
            [ServerMessageKeys.QuestionImportSourceInvalid] = "No se puede abrir la encuesta de origen. Compruebe si se eliminó, archivó o modificó.",
            [ServerMessageKeys.QuestionImportSelectionRequired] = "Seleccione al menos una pregunta para importar.",
            [ServerMessageKeys.PublishBlockedByMapping] = "No se puede publicar. Primero corrija los problemas de la asignación.",
            [ServerMessageKeys.PublishBlockedByFlow] = "No se puede publicar. Primero corrija los problemas de la ramificación.",
            [ServerMessageKeys.PublishBlockedBySettings] = "No se puede publicar. Algunas preguntas tienen configuraciones que ninguna respuesta puede satisfacer.",
            [ServerMessageKeys.AutoReplyTestSubjectPrefix] = "[Prueba] ",
            [ServerMessageKeys.AutoReplyTestLoginIdNotEmail] = "No se puede enviar un mensaje de prueba porque su ID de inicio de sesión no es una dirección de correo electrónico.",
            [ServerMessageKeys.AutoReplyTestMailDisabled] = "No se puede enviar un mensaje de prueba porque el envío de correo no está habilitado en el servidor.",
            [ServerMessageKeys.AutoReplyTestQueueFailed] = "No se pudo poner en cola el mensaje de prueba.",
            [ServerMessageKeys.InvitationMailSubject] = "Ha sido invitado a la administración de cuestionarios",
            [ServerMessageKeys.InvitationMailBody] = "Ha sido invitado a la pantalla de administración de cuestionarios.\n\n"
                + "Abra la siguiente URL y elija su contraseña.\n"
                + "{0}\n\n"
                + "Caduca: {1} (UTC)\n\n"
                + "Si no esperaba este mensaje, descártelo sin abrir la URL.",
            [ServerMessageKeys.PublishBlockedByAutoReply] = "No se puede publicar. La configuración de respuesta automática no puede enviar un correo electrónico.",
            [ServerMessageKeys.NoAnswerableQuestion] = "No hay ninguna pregunta que se pueda responder.",
            [ServerMessageKeys.NotPublishedYet] = "Esta encuesta aún no se ha publicado.",
            [ServerMessageKeys.VersionAlreadyPublished] = "Esta versión ya se ha publicado. Vuelva a cargar la página e inténtelo de nuevo.",
            [ServerMessageKeys.InvalidSurveyStatus] = "Esta acción no está disponible en el estado actual. Vuelva a cargar la página.",
            [ServerMessageKeys.SurveyArchived] = "Esta encuesta está archivada. Restáurela antes de realizar cambios.",
            [ServerMessageKeys.InvalidArchiveState] = "El estado del archivo ya ha cambiado. Vuelva a cargar la página.",
            [ServerMessageKeys.SurveyDeleteRequiresArchive] = "Solo se pueden eliminar permanentemente las encuestas archivadas. Archive primero esta encuesta.",
            [ServerMessageKeys.SurveyDeleteTitleMismatch] = "El título introducido no coincide con el título de la encuesta.",
            [ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery] = "Esta encuesta no se puede eliminar permanentemente mientras haya respuestas o correos pendientes o en proceso de envío.",
            [ServerMessageKeys.SiteIdLockedAfterPublish] = "El ID del sitio de Pleasanter no se puede cambiar después de la publicación en producción.",
            [ServerMessageKeys.SiteIdBlockedByPendingResponses] = "El ID del sitio de Pleasanter no se puede cambiar mientras haya respuestas pendientes de envío.",
            [ServerMessageKeys.DuplicateSiteIdMustDiffer] = "Especifique un ID de sitio de Pleasanter distinto del sitio donde escribe la encuesta original.",
            [ServerMessageKeys.SurveyIsTemplate] = "Esta es una plantilla. Las plantillas no se pueden publicar. Primero cree una encuesta a partir de ella.",
            [ServerMessageKeys.TemplateSourceRequired] = "Especifique la encuesta a partir de la cual crear una plantilla.",
            [ServerMessageKeys.ThemeColorInvalid] = "Especifique los colores con el formato #rrggbb.",
            [ServerMessageKeys.EmbedHostNotAllowed] = "La URL incrustada no está en los orígenes permitidos. Pida a un administrador que agregue el origen.",
            [ServerMessageKeys.HeaderImageRejected] = "No se puede usar esa imagen. Elija una imagen PNG, JPEG, GIF o WebP de hasta 2 MB.",
            [ServerMessageKeys.ContentAssetRejected] = "No se puede usar ese recurso. Compruebe los tipos de archivo permitidos y el límite de tamaño.",
            [ServerMessageKeys.ContentAssetLimitReached] = "Esta encuesta ha alcanzado su límite de recursos.",
            [ServerMessageKeys.AssetScannerUnavailable] = "No se puede guardar el recurso porque el análisis de virus no está disponible. Póngase en contacto con un administrador.",
            [ServerMessageKeys.ResponseLimitMustBePositive] = "El límite de respuestas debe ser al menos 1. Déjelo vacío si no desea establecer un límite.",
            [ServerMessageKeys.ResponseLimitReached] = "Se ha alcanzado el límite de respuestas ({0} de {1}). Aumente el límite antes de reanudar.",
            [ServerMessageKeys.ResponseTokenRequired] = "Especifique qué respuesta desea devolver.",
            [ServerMessageKeys.DeadLetterNotFound] = "No se encontró esa respuesta. Es posible que ya se haya devuelto o que se haya enviado.",
        };

        // **言語ごとの辞書で受ける。** 3 言語目を足すときに、
        // この関数だけを直せば済むようにしておく（Issue #195）
        void AddAll(string key, IReadOnlyDictionary<string, string> byLanguage) =>
            catalog.Add(key, new LocalizedText(byLanguage));

        // **2 言語ぶんの書き方は残す。** 既存の 46 件を書き換えない
        void Add(string key, string ja, string en)
        {
            var byLanguage = new Dictionary<string, string>
            {
                [SupportedLanguages.Default] = ja,
                ["en"] = en,
            };
            if (spanish.TryGetValue(key, out var translation))
            {
                byLanguage["es"] = translation;
            }

            AddAll(key, byLanguage);
        }

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
