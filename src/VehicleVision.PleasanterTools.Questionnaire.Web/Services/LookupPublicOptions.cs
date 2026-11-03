namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>ルックアップの公開 API の上限（Issue #832・仕様書 4.1・6.2 節・11 章 U4・U5）。</summary>
/// <remarks>
/// <para>
/// **既定値だけを持つ。環境変数・設定ファイルからは読まない**（実測で調整が要るとなってから別途判断する）。
/// 試験は小さい値を入れて境界を確かめる。
/// </para>
/// <para>
/// 枠はどれも固定窓。回答者は署名つきトークンの識別子、IP は接続元、フォームは公開 ID で数える。
/// </para>
/// </remarks>
public sealed record LookupPublicOptions
{
    /// <summary>要求本文の上限（バイト）。</summary>
    public int RequestBodyMaxBytes { get; init; } = 4096;

    /// <summary>フォームごとの同時検索数。</summary>
    public int ConcurrentSearchesPerForm { get; init; } = 4;

    /// <summary>回答者の枠の回数。</summary>
    public int RespondentLimit { get; init; } = 30;

    /// <summary>回答者の枠の窓。</summary>
    public TimeSpan RespondentWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>IP の枠の回数。</summary>
    public int IpLimit { get; init; } = 60;

    /// <summary>IP の枠の窓。</summary>
    public TimeSpan IpWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>フォームの枠の回数。</summary>
    public int FormLimit { get; init; } = 120;

    /// <summary>フォームの枠の窓。</summary>
    public TimeSpan FormWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 参照元の不備（502）を管理者へ知らせる重複抑止の窓（Issue #833）。
    /// アンケート・検索・種類ごとに、この間は 2 回目以降を立てない。
    /// </summary>
    public TimeSpan AdminNotificationWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>検索用トークンの有効期間。</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(30);
}
