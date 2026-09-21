namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mail;

/// <summary>送るメールの種類（Issue #189）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **値を振り直さないこと。** DB へ整数で入る（<c>MailOutbox.Kind</c>）。
/// 並べ替えると、既に溜まっている送信待ちの意味が変わる
/// （<see cref="Notifications.AdminNotificationKind"/> と同じ決まり）。
/// </para>
/// <para>
/// **種類を分けるのは、失敗したときに何を止めればよいか判るようにするため。**
/// 回答者への自動返信が届かないのと、管理者の招待が届かないのとでは、打つ手が違う。
/// </para>
/// </remarks>
public enum MailKind
{
    /// <summary>回答者への自動返信。**アンケートごとの設定で送る。**</summary>
    AutoReply = 1,

    /// <summary>管理者の招待。**本人しか開けない URL を含む。**</summary>
    AdminInvitation = 2,

    /// <summary>管理者自身へ送る自動返信の試し送信（Issue #319）。</summary>
    AutoReplyTest = 3,

    /// <summary>管理者へ送る、新しい回答のまとめ通知（Issue #357）。</summary>
    ResponseNotification = 4,
}
