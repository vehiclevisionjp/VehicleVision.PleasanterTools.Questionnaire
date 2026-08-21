namespace VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;

/// <summary>管理者への知らせの種類（Issue #80）。</summary>
/// <remarks>
/// <para>
/// **設計書のあちこちに「管理者へ通知する」と書いてあるが、実装はログ出力だけだった。**
/// 止まったことに気付けなければ、止める仕組みは半分しか働かない。
/// </para>
/// <para>
/// ⚠️ **値を振り直さないこと。** DB へ整数で入る（<c>AdminNotifications.Kind</c>）。
/// 並べ替えると、既に溜まっている知らせの意味が変わる。
/// </para>
/// <para>
/// **文言はここに持たない。** 画面が種類から文言を作る
/// （サーバから訳した文字列を返さない、という既存の決まりに合わせる）。
/// </para>
/// </remarks>
public enum AdminNotificationKind
{
    /// <summary>再送しても通らず、回答をデッドレターへ分離した。</summary>
    DeadLettered = 1,

    /// <summary>滞留が上限に達して、そのアンケートの受付を止めた（Issue #72）。</summary>
    BacklogBlockedSurvey = 2,

    /// <summary>滞留が上限に達して、全アンケートの受付を止めた（Issue #72）。</summary>
    BacklogBlockedTotal = 3,

    /// <summary>Pleasanter の認証に失敗した。**API キーの失効か設定ミス。**</summary>
    PleasanterUnauthorized = 4,

    /// <summary>回答数の上限に達して、アンケートを自動で停止した（Issue #53）。</summary>
    ResponseLimitReached = 5,
}
