namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>接続先 Pleasanter の設定。</summary>
/// <remarks>
/// 実値は <c>App_Data/Parameters/Pleasanter.json</c> か環境変数から読む。
/// **API キーを設定ファイルへ直書きしないこと。**
/// </remarks>
public sealed class PleasanterOptions
{
    /// <summary>ベース URL。末尾のスラッシュは付けない。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API キー。**サーバ側だけが持つ。ブラウザへ渡さない。**</summary>
    public required string ApiKey { get; init; }

    /// <summary>API のバージョン。リクエストボディの <c>ApiVersion</c> に載せる。</summary>
    public decimal ApiVersion { get; init; } = 1.1m;

    /// <summary>1 回の呼び出しの上限。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>API キーに紐づく Pleasanter 利用者のタイムゾーン ID。</summary>
    /// <remarks>
    /// **Pleasanter は日時をこのタイムゾーンとの間で変換する**
    /// （<c>_documents/実機検証結果.md</c> 4 章。実測で確定）。
    /// **運用開始後に変更してはならない。** 変えると保存済みレコードの解釈が変わる。
    /// </remarks>
    public required string ApiKeyUserTimeZoneId { get; init; }
}
