namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>配布資産の引換券の期限を決める方法（Issue #318）。</summary>
public enum AssetTicketExpiration
{
    /// <summary>アンケートの受付終了まで。終了が無ければ既定日数。</summary>
    AcceptTo,

    /// <summary>回答した時点から指定日数。</summary>
    DaysAfterResponse,

    /// <summary>回答完了時の画面でだけ配る。引換券は保存しない。</summary>
    CompletedOnly,
}

/// <summary>回答後に配る資産の設定（Issue #318）。</summary>
public sealed record AssetDeliverySettings
{
    /// <summary>期限の決め方。</summary>
    public AssetTicketExpiration Expiration { get; init; } = AssetTicketExpiration.AcceptTo;

    /// <summary>回答からの日数。受付終了までを選び、終了が無い場合にも使う。</summary>
    public int Days { get; init; } = DefaultDays;

    public const int DefaultDays = 30;

    public const int MaxDays = 365;

    /// <summary>設定から期限を求める。</summary>
    public DateTime ExpiresAt(DateTime answeredAtUtc, DateTime? acceptToUtc)
    {
        if (Expiration == AssetTicketExpiration.AcceptTo && acceptToUtc is { } acceptTo)
        {
            return acceptTo;
        }

        var days = Expiration == AssetTicketExpiration.AcceptTo
            ? DefaultDays
            : Days is >= 1 and <= MaxDays ? Days : DefaultDays;

        // ⚠️ **「回答から N 日」は AcceptTo で切り詰めない。**
        // 締切間際に答えた人が資料を受け取れなくなるため、再編集リンクとは逆にする。
        return answeredAtUtc.AddDays(days);
    }
}
