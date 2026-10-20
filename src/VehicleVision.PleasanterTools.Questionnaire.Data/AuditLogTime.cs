namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>
/// 管理操作の記録（Core.Data の <c>Core_AuditLogs</c>）の時刻の目盛りを、これまでと同じにそろえる。
/// </summary>
/// <remarks>
/// <para>
/// <b>本アプリは、管理操作の時刻を「サーバのローカル時刻の目盛り」で持っていた</b>
/// （書くときは <c>DateTime.Now</c>、掃除の基準も <c>GetLocalNow()</c>、画面へは時間帯の印を付けずに返す）。
/// 旧表から写した行も、この目盛りのまま入っている。
/// </para>
/// <para>
/// Core.Data の記録は <see cref="DateTimeOffset"/> で受け渡す。<b>目盛りを変えると、
/// 写した過去の行と新しい行で目盛りが食い違い、画面の時刻がずれる</b>ので、
/// 本アプリでは <b>これまでと同じ目盛り（ローカル時刻の数字）を、そのまま時刻の数字として渡す</b>。
/// 目盛りを UTC へ直すかどうかは、画面の表現と旧データの移行を含めた別の判断にする。
/// </para>
/// <para>
/// <b>暗黙の変換（<see cref="DateTime"/> から <see cref="DateTimeOffset"/>）を使わないこと。</b>
/// 種別の無い値をサーバの時間帯と見なして UTC へ直すので、ローカル時刻の数字がずれる。
/// </para>
/// </remarks>
public static class AuditLogTime
{
    /// <summary>これまでの目盛り（ローカル時刻の数字）を、Core.Data の記録へ渡す形にする。数字は動かさない。</summary>
    public static DateTimeOffset ToStored(DateTime wallClock) =>
        new(DateTime.SpecifyKind(wallClock, DateTimeKind.Utc));

    /// <summary>絞り込みなど、未指定のままの値を <see cref="ToStored(DateTime)"/> で渡す形にする。</summary>
    public static DateTimeOffset? ToStored(DateTime? wallClock) =>
        wallClock is { } present ? ToStored(present) : null;

    /// <summary>Core.Data の記録から読んだ時刻を、これまでの形（時間帯の印の無い日時）へ戻す。数字は動かさない。</summary>
    public static DateTime ToWallClock(DateTimeOffset stored) =>
        DateTime.SpecifyKind(stored.UtcDateTime, DateTimeKind.Unspecified);
}
