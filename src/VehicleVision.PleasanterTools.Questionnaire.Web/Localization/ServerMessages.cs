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

        // **2 言語ぶんの書き方は残す。** 既存の 46 件を書き換えない
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
