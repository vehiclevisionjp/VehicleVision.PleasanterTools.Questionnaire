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

    private static readonly FrozenDictionary<string, string> Korean = new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "로그인 ID 또는 입력한 내용이 올바르지 않습니다.",
        [ServerMessageKeys.LoginIdAndPasswordRequired] = "로그인 ID와 비밀번호를 입력하세요.",
        [ServerMessageKeys.AdministratorAlreadyExists] = "관리자가 이미 등록되어 있습니다.",
        [ServerMessageKeys.LoginTemporarilyLocked] = "시도가 계속되어 잠시 로그인할 수 없습니다. 잠시 후 다시 시도하세요.",
        [ServerMessageKeys.EnrollmentRestartRequired] = "등록을 처음부터 다시 하세요.",
        [ServerMessageKeys.TotpCodeMismatch] = "코드가 일치하지 않습니다. 인증 앱에 표시된 번호를 확인하세요.",
        [ServerMessageKeys.TwoFactorDisabled] = "2단계 인증이 비활성화되어 있습니다. 등록하려면 관리자에게 설정 변경을 요청하세요.",
        [ServerMessageKeys.TwoFactorRequired] = "2단계 인증이 필수로 설정되어 있어 해제할 수 없습니다.",
        [ServerMessageKeys.TwoFactorNotEnrolled] = "2단계 인증이 등록되어 있지 않습니다.",
        [ServerMessageKeys.PasswordTooShort] = "비밀번호는 {0}자 이상이어야 합니다.",
        [ServerMessageKeys.PasswordSameAsLoginId] = "비밀번호에 로그인 ID와 같은 문자열을 사용할 수 없습니다.",
        [ServerMessageKeys.PasswordPolicyMismatch] = "비밀번호가 정해진 조건을 충족하지 않습니다.",
        [ServerMessageKeys.RoleNotSupported] = "역할은 Administrator / SurveyAdministrator / UserAdministrator / Editor / Auditor 중 하나를 지정하세요.",
        [ServerMessageKeys.AdminUserNotFound] = "해당 관리자를 찾을 수 없습니다.",
        [ServerMessageKeys.DuplicateLoginId] = "해당 로그인 ID는 이미 사용 중입니다.",
        [ServerMessageKeys.InvalidInput] = "입력 내용을 확인하세요.",
        [ServerMessageKeys.SelfNotAllowed] = "자신의 계정에는 실행할 수 없습니다. 다른 관리자에게 요청하세요.",
        [ServerMessageKeys.LastAdministrator] = "로그인할 수 있는 다른 관리자가 없습니다. 먼저 다른 관리자를 추가하고 로그인할 수 있는지 확인하세요.",
        [ServerMessageKeys.OperationTemporarilyLocked] = "시도가 계속되어 잠시 작업할 수 없습니다. 잠시 후 다시 시도하세요.",
        [ServerMessageKeys.InvitationInvalid] = "초대를 사용할 수 없습니다. 초대를 다시 요청하세요.",
        [ServerMessageKeys.CurrentPasswordRejected] = "현재 비밀번호가 올바르지 않습니다.",
        [ServerMessageKeys.UnsupportedLanguage] = "지원하지 않는 언어입니다.",
        [ServerMessageKeys.AdminSessionNotFound] = "해당 세션을 찾을 수 없습니다.",
        [ServerMessageKeys.CurrentSessionCannotBeRevoked] = "현재 사용 중인 세션은 여기서 종료할 수 없습니다. 대신 로그아웃하세요.",
        [ServerMessageKeys.ResponseNotificationMailSubject] = "설문 조사에 새 답변이 있습니다",
        [ServerMessageKeys.ResponseNotificationMailBody] = "설문 조사 \"{0}\"에 새 답변이 {1}건 도착했습니다.\n집계 기간(UTC): {2} ~ {3}\n\n답변 내용은 Pleasanter에서 확인하세요.",
        [ServerMessageKeys.RemovedSurvey] = "삭제된 설문 조사",
        [ServerMessageKeys.SurveyTitleRequired] = "제목을 입력하세요.",
        [ServerMessageKeys.PleasanterSiteIdRequired] = "Pleasanter 사이트 ID를 지정하세요.",
        [ServerMessageKeys.DefinitionAndMappingRequired] = "정의와 매핑이 모두 필요합니다.",
        [ServerMessageKeys.SurveyUpdatedByOther] = "다른 사용자가 이 설문 조사를 업데이트했습니다. 다시 불러오세요.",
        [ServerMessageKeys.QuestionImportSourceInvalid] = "가져올 설문 조사를 열 수 없습니다. 삭제, 보관 또는 변경되었는지 확인하세요.",
        [ServerMessageKeys.QuestionImportSelectionRequired] = "가져올 질문을 하나 이상 선택하세요.",
        [ServerMessageKeys.PublishBlockedByMapping] = "공개할 수 없습니다. 매핑의 문제를 수정하세요.",
        [ServerMessageKeys.PublishBlockedByFlow] = "공개할 수 없습니다. 분기의 문제를 수정하세요.",
        [ServerMessageKeys.PublishBlockedBySettings] = "공개할 수 없습니다. 일부 질문 설정에는 답변할 수 없는 지정이 있습니다.",
        [ServerMessageKeys.AutoReplyTestSubjectPrefix] = "【테스트 전송】",
        [ServerMessageKeys.AutoReplyTestLoginIdNotEmail] = "로그인 ID가 이메일 주소가 아니므로 테스트 전송을 할 수 없습니다.",
        [ServerMessageKeys.AutoReplyTestMailDisabled] = "서버에서 메일 전송을 활성화하지 않아 테스트 전송을 할 수 없습니다.",
        [ServerMessageKeys.AutoReplyTestQueueFailed] = "테스트 전송을 대기열에 등록할 수 없습니다.",
        [ServerMessageKeys.InvitationMailSubject] = "설문 조사 관리 화면 초대",
        [ServerMessageKeys.InvitationMailBody] = "설문 조사 관리 화면 초대가 도착했습니다.\n\n다음 URL을 열고 비밀번호를 정하세요.\n{0}\n\n만료: {1} (UTC)\n\n이 메일을 예상하지 못했다면 URL을 열지 말고 삭제하세요.",
        [ServerMessageKeys.PublishBlockedByAutoReply] = "공개할 수 없습니다. 자동 회신 메일 설정으로는 메일을 보낼 수 없습니다.",
        [ServerMessageKeys.NoAnswerableQuestion] = "답변할 수 있는 질문이 없습니다.",
        [ServerMessageKeys.NotPublishedYet] = "아직 공개되지 않았습니다.",
        [ServerMessageKeys.VersionAlreadyPublished] = "이 버전은 이미 공개되었습니다. 다시 불러온 후 다시 시도하세요.",
        [ServerMessageKeys.InvalidSurveyStatus] = "현재 상태에서는 이 작업을 수행할 수 없습니다. 다시 불러오세요.",
        [ServerMessageKeys.SurveyArchived] = "이 설문 조사는 보관되었습니다. 복원한 후 변경하세요.",
        [ServerMessageKeys.InvalidArchiveState] = "보관 상태가 이미 변경되었습니다. 다시 불러오세요.",
        [ServerMessageKeys.SurveyDeleteRequiresArchive] = "완전히 삭제할 수 있는 것은 보관된 설문 조사뿐입니다. 먼저 보관하세요.",
        [ServerMessageKeys.SurveyDeleteTitleMismatch] = "입력한 제목이 설문 조사 제목과 일치하지 않습니다.",
        [ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery] = "전송 대기 또는 전송 중인 답변이나 메일이 남아 있어 완전히 삭제할 수 없습니다.",
        [ServerMessageKeys.SiteIdLockedAfterPublish] = "정식 공개한 설문 조사의 Pleasanter 사이트 ID는 변경할 수 없습니다.",
        [ServerMessageKeys.SiteIdBlockedByPendingResponses] = "전송 대기 중인 답변이 있어 Pleasanter 사이트 ID를 변경할 수 없습니다.",
        [ServerMessageKeys.DuplicateSiteIdMustDiffer] = "복제 대상에는 원본과 다른 Pleasanter 사이트 ID를 지정하세요.",
        [ServerMessageKeys.SurveyIsTemplate] = "이것은 템플릿입니다. 템플릿은 공개할 수 없습니다. 먼저 템플릿에서 설문 조사를 만드세요.",
        [ServerMessageKeys.TemplateSourceRequired] = "템플릿의 원본으로 사용할 설문 조사를 지정하세요.",
        [ServerMessageKeys.ThemeColorInvalid] = "색상은 #rrggbb 형식으로 지정하세요.",
        [ServerMessageKeys.EmbedHostNotAllowed] = "삽입 대상이 허용된 출처에 포함되어 있지 않습니다. 관리자에게 출처 추가를 요청하세요.",
        [ServerMessageKeys.HeaderImageRejected] = "이 이미지는 사용할 수 없습니다. PNG, JPEG, GIF 또는 WebP 형식의 2 MB 이하 이미지를 선택하세요.",
        [ServerMessageKeys.ContentAssetRejected] = "이 배포 자료는 사용할 수 없습니다. 허용된 형식과 용량을 확인하세요.",
        [ServerMessageKeys.ContentAssetLimitReached] = "이 설문 조사에 저장할 수 있는 배포 자료의 한도에 도달했습니다.",
        [ServerMessageKeys.AssetScannerUnavailable] = "바이러스 검사를 사용할 수 없어 배포 자료를 저장할 수 없습니다. 관리자에게 문의하세요.",
        [ServerMessageKeys.ResponseLimitMustBePositive] = "답변 수 한도는 1 이상이어야 합니다. 한도를 두지 않으려면 비워 두세요.",
        [ServerMessageKeys.ResponseLimitReached] = "답변 수가 한도에 도달했습니다({0} / {1}건). 다시 시작하려면 먼저 한도를 늘리세요.",
        [ServerMessageKeys.ResponseTokenRequired] = "되돌릴 답변을 지정하세요.",
        [ServerMessageKeys.DeadLetterNotFound] = "해당 답변을 찾을 수 없습니다. 이미 전송 대기로 되돌렸거나 전송이 완료되었을 수 있습니다.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

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
        // ⚠️ **3 言語目からは言語ごとの辞書で足すこと。**
        // 引数を増やしていくと、**言語が 1 つ増えるたびに 67 か所すべてを書き換える**ことになり、
        // 言語ごとに分けて進めている作業が必ずぶつかる。
        var byOtherLanguage = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal)
        {
            ["de"] = german,
            ["ko"] = Korean,
        };

        void Add(string key, string ja, string en, string? vi = null)
        {
            var byLanguage = new Dictionary<string, string>
            {
                [SupportedLanguages.Default] = ja,
                ["en"] = en,
            };

            if (vi is not null)
            {
                byLanguage.Add("vi", vi);
            }

            // **訳の無い鍵は入れない。** 入れないことで en へ落ちる
            foreach (var (language, texts) in byOtherLanguage)
            {
                if (texts.TryGetValue(key, out var text))
                {
                    byLanguage[language] = text;
                }
            }

            AddAll(key, byLanguage);
        }

        // ---- 認証 -----------------------------------------------------------
        Add(
            ServerMessageKeys.InvalidCredentials,
            "ログイン ID または入力内容が正しくありません。",
            "The sign-in ID or the value you entered is not correct.",
            "ID đăng nhập hoặc nội dung đã nhập không chính xác.");

        Add(
            ServerMessageKeys.LoginIdAndPasswordRequired,
            "ログイン ID とパスワードを入力してください。",
            "Enter your sign-in ID and password.",
            "Vui lòng nhập ID đăng nhập và mật khẩu.");

        Add(
            ServerMessageKeys.AdministratorAlreadyExists,
            "管理者はすでに登録されています。",
            "An administrator has already been registered.",
            "Quản trị viên đã được đăng ký.");

        Add(
            ServerMessageKeys.LoginTemporarilyLocked,
            "試行が続いたため、しばらくログインできません。時間を置いてお試しください。",
            "Too many attempts. Sign-in is unavailable for a while; please try again later.",
            "Do đã có quá nhiều lần thử, bạn không thể đăng nhập trong một thời gian. Vui lòng thử lại sau.");

        Add(
            ServerMessageKeys.EnrollmentRestartRequired,
            "登録をやり直してください。",
            "Start the registration over.",
            "Vui lòng thực hiện lại việc đăng ký.");

        Add(
            ServerMessageKeys.TotpCodeMismatch,
            "コードが一致しません。認証アプリの表示をご確認ください。",
            "That code does not match. Check the number shown in your authenticator app.",
            "Mã không khớp. Vui lòng kiểm tra số hiển thị trong ứng dụng xác thực.");

        Add(
            ServerMessageKeys.TwoFactorDisabled,
            "2 要素認証は無効に設定されています。登録するには、管理者に設定の変更を依頼してください。",
            "Two-factor authentication is turned off. Ask your operator to change the setting first.",
            "Xác thực hai yếu tố đã bị tắt. Để đăng ký, hãy yêu cầu quản trị viên thay đổi cài đặt.");

        Add(
            ServerMessageKeys.TwoFactorRequired,
            "2 要素認証が必須に設定されているため、解除できません。",
            "Two-factor authentication is required, so it cannot be removed.",
            "Không thể hủy vì xác thực hai yếu tố là bắt buộc.");

        Add(
            ServerMessageKeys.TwoFactorNotEnrolled,
            "2 要素認証は登録されていません。",
            "Two-factor authentication is not set up.",
            "Xác thực hai yếu tố chưa được đăng ký.");

        Add(
            ServerMessageKeys.PasswordTooShort,
            "パスワードは {0} 文字以上にしてください。",
            "Use a password of at least {0} characters.",
            "Mật khẩu phải có ít nhất {0} ký tự.");

        Add(
            ServerMessageKeys.PasswordSameAsLoginId,
            "パスワードにログイン ID と同じ文字列は使えません。",
            "The password cannot be the same as the sign-in ID.",
            "Mật khẩu không thể giống với ID đăng nhập.");

        Add(
            ServerMessageKeys.PasswordPolicyMismatch,
            "パスワードが決められた条件を満たしていません。",
            "The password does not meet the required conditions.",
            "Mật khẩu không đáp ứng các điều kiện đã quy định.");

        // ---- 管理者の管理 ---------------------------------------------------
        Add(
            ServerMessageKeys.RoleNotSupported,
            "役割は Administrator / SurveyAdministrator / UserAdministrator / Editor / Auditor のいずれかを指定してください。",
            "The role must be one of Administrator, SurveyAdministrator, UserAdministrator, Editor or Auditor.",
            "Vai trò phải là một trong các giá trị Administrator, SurveyAdministrator, UserAdministrator, Editor hoặc Auditor.");

        Add(
            ServerMessageKeys.AdminUserNotFound,
            "その管理者は見つかりません。",
            "That administrator was not found.",
            "Không tìm thấy quản trị viên đó.");

        Add(
            ServerMessageKeys.DuplicateLoginId,
            "そのログイン ID はすでに使われています。",
            "That sign-in ID is already taken.",
            "ID đăng nhập đó đã được sử dụng.");

        Add(
            ServerMessageKeys.InvalidInput,
            "入力をご確認ください。",
            "Check what you entered.",
            "Vui lòng kiểm tra nội dung đã nhập.");

        Add(
            ServerMessageKeys.SelfNotAllowed,
            "自分自身に対しては実行できません。別の管理者に依頼してください。",
            "You cannot do this to your own account. Ask another administrator.",
            "Bạn không thể thực hiện thao tác này với chính mình. Vui lòng yêu cầu quản trị viên khác.");

        Add(
            ServerMessageKeys.LastAdministrator,
            "他にログインできる管理者がいません。"
            + "先に別の管理者を追加し、その管理者がログインできることを確かめてください。",
            "No other administrator can sign in. Add another administrator first "
            + "and confirm that they can sign in.",
            "Không có quản trị viên nào khác có thể đăng nhập. Hãy thêm quản trị viên khác trước và xác nhận rằng họ có thể đăng nhập.");

        Add(
            ServerMessageKeys.OperationTemporarilyLocked,
            "試行が続いたため、しばらく操作できません。時間を置いてお試しください。",
            "Too many attempts. This action is unavailable for a while; please try again later.",
            "Do đã có quá nhiều lần thử, thao tác này không thể thực hiện trong một thời gian. Vui lòng thử lại sau.");

        Add(
            ServerMessageKeys.InvitationInvalid,
            "招待が使用できません。招待をやり直してください。",
            "That invitation cannot be used. Ask for a new invitation.",
            "Không thể sử dụng lời mời này. Vui lòng yêu cầu một lời mời mới.");

        Add(
            ServerMessageKeys.CurrentPasswordRejected,
            "今のパスワードが正しくありません。",
            "Your current password is not correct.",
            "Mật khẩu hiện tại không chính xác.");

        Add(
            ServerMessageKeys.UnsupportedLanguage,
            "対応していない言語です。",
            "That language is not supported.",
            "Ngôn ngữ này không được hỗ trợ.");

        Add(
            ServerMessageKeys.AdminSessionNotFound,
            "そのセッションは見つかりません。",
            "That session was not found.",
            "Không tìm thấy phiên đó.");

        Add(
            ServerMessageKeys.CurrentSessionCannotBeRevoked,
            "現在使っているセッションはここから終了できません。ログアウトしてください。",
            "The current session cannot be ended here. Sign out instead.",
            "Không thể kết thúc phiên đang sử dụng tại đây. Vui lòng đăng xuất.");

        Add(
            ServerMessageKeys.ResponseNotificationMailSubject,
            "アンケートに新しい回答があります",
            "New survey responses",
            "Có câu trả lời khảo sát mới");

        Add(
            ServerMessageKeys.ResponseNotificationMailBody,
            "アンケート「{0}」に新しい回答が {1} 件届きました。\n"
            + "集計期間（UTC）: {2} ～ {3}\n\n"
            + "回答内容は Pleasanter で確認してください。",
            "The survey \"{0}\" received {1} new response(s).\n"
            + "Period (UTC): {2} to {3}\n\n"
            + "View the response content in Pleasanter.",
            "Khảo sát “{0}” đã nhận được {1} câu trả lời mới.\n"
            + "Thời gian tổng hợp (UTC): {2} đến {3}\n\n"
            + "Vui lòng kiểm tra nội dung câu trả lời trong Pleasanter.");

        Add(
            ServerMessageKeys.RemovedSurvey,
            "削除されたアンケート",
            "Deleted survey",
            "Khảo sát đã xóa");

        // ---- アンケート -----------------------------------------------------
        Add(
            ServerMessageKeys.SurveyTitleRequired,
            "題名を入力してください。",
            "Enter a title.",
            "Vui lòng nhập tiêu đề.");

        Add(
            ServerMessageKeys.PleasanterSiteIdRequired,
            "Pleasanter のサイト ID を指定してください。",
            "Specify the Pleasanter site ID.",
            "Vui lòng chỉ định ID trang Pleasanter.");

        Add(
            ServerMessageKeys.DefinitionAndMappingRequired,
            "定義と割り当ての両方が必要です。",
            "Both the definition and the mapping are required.",
            "Cần có cả định nghĩa và ánh xạ.");

        Add(
            ServerMessageKeys.SurveyUpdatedByOther,
            "他の人がこのアンケートを更新しました。再読み込みしてください。",
            "Someone else updated this survey. Reload it.",
            "Người khác đã cập nhật khảo sát này. Vui lòng tải lại.");

        Add(
            ServerMessageKeys.QuestionImportSourceInvalid,
            "取り込み元のアンケートを開けません。削除・アーカイブ・変更されていないか確認してください。",
            "The source survey cannot be opened. Check whether it was deleted, archived, or changed.",
            "Không thể mở khảo sát nguồn. Vui lòng kiểm tra xem khảo sát đã bị xóa, lưu trữ hoặc thay đổi chưa.");

        Add(
            ServerMessageKeys.QuestionImportSelectionRequired,
            "取り込む設問を 1 つ以上選んでください。",
            "Select at least one question to import.",
            "Vui lòng chọn ít nhất một câu hỏi để nhập.");

        Add(
            ServerMessageKeys.PublishBlockedByMapping,
            "公開できません。割り当ての不備を修正してください。",
            "Cannot publish. Fix the problems in the mapping first.",
            "Không thể công bố. Vui lòng sửa các vấn đề trong ánh xạ.");

        Add(
            ServerMessageKeys.PublishBlockedByFlow,
            "公開できません。分岐の不備を修正してください。",
            "Cannot publish. Fix the problems in the branching first.",
            "Không thể công bố. Vui lòng sửa các vấn đề trong phân nhánh.");

        Add(
            ServerMessageKeys.PublishBlockedBySettings,
            "公開できません。設問の設定に、回答できない指定があります。",
            "Cannot publish. Some questions have settings that no answer can satisfy.",
            "Không thể công bố. Một số cài đặt câu hỏi không cho phép câu trả lời nào.");

        Add(ServerMessageKeys.AutoReplyTestSubjectPrefix, "【試し送信】", "[Test] ", "【Gửi thử】");
        Add(
            ServerMessageKeys.AutoReplyTestLoginIdNotEmail,
            "ログイン ID がメールアドレスではないため、試し送信できません。",
            "Your sign-in ID is not an email address, so a test message cannot be sent.",
            "Không thể gửi thử vì ID đăng nhập không phải là địa chỉ email.");
        Add(
            ServerMessageKeys.AutoReplyTestMailDisabled,
            "メールの送信がサーバ側で有効になっていないため、試し送信できません。",
            "Mail sending is not enabled on the server, so a test message cannot be sent.",
            "Không thể gửi thử vì tính năng gửi email chưa được bật trên máy chủ.");
        Add(
            ServerMessageKeys.AutoReplyTestQueueFailed,
            "試し送信を送信待ちへ登録できませんでした。",
            "The test message could not be queued.",
            "Không thể đưa thư gửi thử vào hàng đợi.");

        // ---- 招待メール（Issue #189）----------------------------------------
        // **平文で送る。** 書式は持たない（OutgoingMail が text/plain）
        Add(
            ServerMessageKeys.InvitationMailSubject,
            "アンケート管理画面への招待",
            "You have been invited to the questionnaire admin",
            "Bạn được mời đến trang quản lý khảo sát");

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
            + "If you were not expecting this, discard this message without opening the URL.",
            "Bạn đã được mời đến trang quản lý khảo sát.\n\n"
            + "Hãy mở URL sau và đặt mật khẩu.\n"
            + "{0}\n\n"
            + "Hết hạn: {1} (UTC)\n\n"
            + "Nếu bạn không mong đợi email này, hãy xóa thư mà không mở URL.");

        Add(
            ServerMessageKeys.PublishBlockedByAutoReply,
            "公開できません。自動返信メールの設定では、メールを送れません。",
            "Cannot publish. The auto-reply settings cannot send an email.",
            "Không thể công bố. Cài đặt trả lời tự động không thể gửi email.");

        Add(
            ServerMessageKeys.NoAnswerableQuestion,
            "回答できる設問がありません。",
            "There is no question that can be answered.",
            "Không có câu hỏi nào có thể trả lời.");

        Add(
            ServerMessageKeys.NotPublishedYet,
            "まだ公開されていません。",
            "This survey has not been published yet.",
            "Khảo sát này chưa được công bố.");

        Add(
            ServerMessageKeys.VersionAlreadyPublished,
            "この版はすでに公開されています。再読み込みしてから、もう一度お試しください。",
            "This version has already been published. Reload and try again.",
            "Phiên bản này đã được công bố. Vui lòng tải lại rồi thử lại.");

        Add(
            ServerMessageKeys.InvalidSurveyStatus,
            "現在の状態ではこの操作を行えません。再読み込みしてください。",
            "This action is not available in the current state. Reload the page.",
            "Không thể thực hiện thao tác này ở trạng thái hiện tại. Vui lòng tải lại trang.");

        Add(
            ServerMessageKeys.SurveyArchived,
            "このアンケートはアーカイブ済みです。復元してから変更してください。",
            "This survey is archived. Restore it before making changes.",
            "Khảo sát này đã được lưu trữ. Vui lòng khôi phục trước khi thay đổi.");

        Add(
            ServerMessageKeys.InvalidArchiveState,
            "アーカイブの状態がすでに変わっています。再読み込みしてください。",
            "The archive state has already changed. Reload the page.",
            "Trạng thái lưu trữ đã thay đổi. Vui lòng tải lại trang.");

        Add(
            ServerMessageKeys.SurveyDeleteRequiresArchive,
            "完全に削除できるのはアーカイブ済みのアンケートだけです。先にアーカイブしてください。",
            "Only archived surveys can be permanently deleted. Archive this survey first.",
            "Chỉ có thể xóa vĩnh viễn các khảo sát đã lưu trữ. Vui lòng lưu trữ khảo sát này trước.");

        Add(
            ServerMessageKeys.SurveyDeleteTitleMismatch,
            "入力した題名がアンケートの題名と一致しません。",
            "The title you entered does not match the survey title.",
            "Tiêu đề đã nhập không khớp với tiêu đề khảo sát.");

        Add(
            ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery,
            "送信待ちまたは送信中の回答・メールが残っているため、完全に削除できません。",
            "This survey cannot be permanently deleted while responses or mail are pending or being sent.",
            "Không thể xóa vĩnh viễn khảo sát này khi vẫn còn câu trả lời hoặc email đang chờ gửi hoặc đang được gửi.");

        Add(
            ServerMessageKeys.SiteIdLockedAfterPublish,
            "本公開したアンケートの Pleasanter サイト ID は変更できません。",
            "The Pleasanter site ID cannot be changed after production publication.",
            "Không thể thay đổi ID trang Pleasanter sau khi khảo sát được công bố chính thức.");

        Add(
            ServerMessageKeys.SiteIdBlockedByPendingResponses,
            "送信待ちの回答が残っているため、Pleasanter サイト ID を変更できません。",
            "The Pleasanter site ID cannot be changed while responses are waiting to be sent.",
            "Không thể thay đổi ID trang Pleasanter khi còn câu trả lời đang chờ gửi.");

        Add(
            ServerMessageKeys.DuplicateSiteIdMustDiffer,
            "複製先には、元とは別の Pleasanter のサイト ID を指定してください。",
            "Specify a Pleasanter site ID other than the one the original survey writes to.",
            "Vui lòng chỉ định ID trang Pleasanter khác với khảo sát gốc.");

        Add(
            ServerMessageKeys.SurveyIsTemplate,
            "これはテンプレートです。テンプレートは公開できません。"
                + "テンプレートからアンケートを作成してください。",
            "This is a template. Templates cannot be published. Create a survey from it first.",
            "Đây là mẫu. Không thể công bố mẫu. Vui lòng tạo khảo sát từ mẫu.");

        // ---- テンプレート ---------------------------------------------------
        Add(
            ServerMessageKeys.TemplateSourceRequired,
            "テンプレートの元にするアンケートを指定してください。",
            "Specify the survey to make a template from.",
            "Vui lòng chỉ định khảo sát để tạo mẫu.");

        // ---- テーマ ---------------------------------------------------------
        Add(
            ServerMessageKeys.ThemeColorInvalid,
            "色は #rrggbb の形式で指定してください。",
            "Specify colors in the #rrggbb form.",
            "Vui lòng chỉ định màu theo định dạng #rrggbb.");

        Add(
            ServerMessageKeys.EmbedHostNotAllowed,
            "埋め込み先が、許可された配信元に入っていません。管理者へ配信元の追加を依頼してください。",
            "The embedded URL is not in the allowed sources. Ask an administrator to add it.",
            "Nguồn nhúng không nằm trong các nguồn được phép. Vui lòng yêu cầu quản trị viên thêm nguồn.");

        Add(
            ServerMessageKeys.HeaderImageRejected,
            "この画像は使用できません。PNG・JPEG・GIF・WebP の 2 MB 以内の画像を選んでください。",
            "That image cannot be used. Choose a PNG, JPEG, GIF or WebP image of up to 2 MB.",
            "Không thể sử dụng hình ảnh này. Vui lòng chọn hình ảnh PNG, JPEG, GIF hoặc WebP có dung lượng tối đa 2 MB.");

        Add(
            ServerMessageKeys.ContentAssetRejected,
            "この配布物は使用できません。許可された形式と容量を確認してください。",
            "That asset cannot be used. Check the allowed file types and size limit.",
            "Không thể sử dụng tài liệu phân phối này. Vui lòng kiểm tra định dạng và dung lượng được phép.");

        Add(
            ServerMessageKeys.ContentAssetLimitReached,
            "このアンケートへ保存できる配布物の上限に達しています。",
            "This survey has reached its asset limit.",
            "Đã đạt giới hạn tài liệu phân phối có thể lưu cho khảo sát này.");

        Add(
            ServerMessageKeys.AssetScannerUnavailable,
            "ウイルス検査を利用できないため、配布物を保存できません。管理者へ連絡してください。",
            "The asset cannot be saved because virus scanning is unavailable. Contact an administrator.",
            "Không thể lưu tài liệu phân phối vì không thể sử dụng tính năng quét vi-rút. Vui lòng liên hệ quản trị viên.");

        // ---- 回答数の上限 ---------------------------------------------------
        Add(
            ServerMessageKeys.ResponseLimitMustBePositive,
            "回答数の上限は 1 以上にしてください。上限を設けない場合は空欄にしてください。",
            "The response limit must be at least 1. Leave it empty for no limit.",
            "Giới hạn số câu trả lời phải từ 1 trở lên. Để trống nếu không đặt giới hạn.");

        Add(
            ServerMessageKeys.ResponseLimitReached,
            "回答数が上限に達しています（{0} / {1} 件）。"
            + "再開するには、先に上限を引き上げてください。",
            "The response limit has been reached ({0} of {1}). "
            + "Raise the limit before resuming.",
            "Số câu trả lời đã đạt giới hạn ({0} / {1}). Hãy tăng giới hạn trước khi tiếp tục.");

        // ---- 送信状況 -------------------------------------------------------
        Add(
            ServerMessageKeys.ResponseTokenRequired,
            "戻す回答を指定してください。",
            "Specify which response to put back.",
            "Vui lòng chỉ định câu trả lời cần đưa trở lại.");

        Add(
            ServerMessageKeys.DeadLetterNotFound,
            "その回答は見つかりません。すでに送信待ちに戻されたか、送信が完了した可能性があります。",
            "That response was not found. It may have already been put back, or it may have been sent.",
            "Không tìm thấy câu trả lời đó. Có thể câu trả lời đã được đưa lại vào hàng đợi hoặc đã gửi xong.");

        return catalog.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
