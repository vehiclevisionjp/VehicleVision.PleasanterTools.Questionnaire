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

    /// <summary>再送しても通らず、メールをデッドレターへ分離した（Issue #189）。</summary>
    /// <remarks>
    /// **回答の分離（<see cref="DeadLettered"/>）と分ける。**
    /// 回答は届いていないが、メールは届かなかっただけで**回答は Pleasanter に入っている。**
    /// 同じ知らせにすると、慌てて回答を探しに行くことになる。
    /// </remarks>
    MailDeadLettered = 6,

    /// <summary>新しい回答を受け付けた（Issue #357）。</summary>
    /// <remarks>⚠️ **回答の中身は持たない。** 件数・時刻・アンケートだけを知らせる。</remarks>
    ResponseReceived = 7,

    /// <summary>ClamAV の定期確認で検査できない状態を検知した。</summary>
    ClamAvUnavailable = 8,

    /// <summary>同じインスタンスで異常を検知した後、ClamAV の検査が復旧した。</summary>
    ClamAvRecovered = 9,

    /// <summary>ルックアップの専用資格情報が使えない（無い・停止・失効・復号不可）。仕様書 4.3 節の 502。</summary>
    /// <remarks>**公開し直さずに効く。** 管理画面で資格情報を確認する。回答者には内部情報なしの取得失敗を返している。</remarks>
    LookupCredentialUnavailable = 10,

    /// <summary>ルックアップの参照元で権限がない・サイトが見つからない（401・403・404）。仕様書 4.3 節の 502。</summary>
    LookupSourceAccessDenied = 11,

    /// <summary>ルックアップの参照元が要求を拒否した・定義や接続先の不備（その他の 4xx・リダイレクト・HTTPS でない・定義不正）。</summary>
    LookupSourceRejected = 12,

    /// <summary>ルックアップの参照元の応答が使えない（JSON でない・上限超過・件数の不整合・想定外の例外）。</summary>
    LookupSourceBadResponse = 13,

    /// <summary>Pleasanter への接続先（BaseUrl）の設定が使えない（https でない・資格情報／クエリ／フラグメント付きなど）。</summary>
    /// <remarks>
    /// **認証の失敗（<see cref="PleasanterUnauthorized"/>）とは別。** サーバー側の設定の誤りで、人が直す必要がある。
    /// 直るまで、未送信の回答は再送の対象のまま残る。通知にも URL・資格情報は載せない。
    /// </remarks>
    PleasanterConfigurationInvalid = 14,
}
