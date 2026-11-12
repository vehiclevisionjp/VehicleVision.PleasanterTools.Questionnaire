namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>Pleasanter の接続設定を呼び出し時に解決する。</summary>
public interface IPleasanterOptionsProvider
{
    Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default);
}

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

    /// <summary>API のバージョンの既定。**書き間違いはここへ落とす**（Issue #158）。</summary>
    public const decimal DefaultApiVersion = 1.1m;

    /// <summary>1 回の呼び出しの上限の既定。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>API のバージョン。リクエストボディの <c>ApiVersion</c> に載せる。</summary>
    public decimal ApiVersion { get; init; } = DefaultApiVersion;

    /// <summary>環境変数の名前。DotNetClient の <c>PleasanterOptions.FromEnvironment()</c> と同じ名前にそろえている。</summary>
    public const string AllowInsecureHttpEnvironmentVariable = "VVPT_PLEASANTER_ALLOW_INSECURE_HTTP";

    /// <summary>
    /// ループバック以外の <c>http</c> も許す。<b>検証用の環境（コンテナ間の通信など）だけで有効にすること。</b>
    /// 有効にすると、API キーを含むリクエストの本文が<b>平文</b>で流れる。既定は無効。
    /// 資格情報・クエリ・フラグメント・http／https 以外のスキームは、有効にしても拒否される（DotNetClient 0.3.0）。
    /// </summary>
    /// <remarks>
    /// <b>管理画面・設定ファイルからは設定できない</b>（本番で誤って有効にされないよう、設定の項目にしない）。
    /// 既定値は、環境変数 <c>VVPT_PLEASANTER_ALLOW_INSECURE_HTTP</c> が <c>true</c> のときだけ有効。
    /// 本アプリは接続設定を自前で組み立てて DotNetClient へ渡すので、DotNetClient 側の環境変数の読み取り
    /// （<c>FromEnvironment()</c>）は届かない。そのため、ここで読む。
    /// </remarks>
    public bool AllowInsecureHttp { get; init; } = ParseAllowInsecureHttp(Environment.GetEnvironmentVariable(AllowInsecureHttpEnvironmentVariable));

    /// <summary>環境変数の値を読む。<c>true</c>（大文字小文字は問わない）のときだけ有効。</summary>
    public static bool ParseAllowInsecureHttp(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>1 回の呼び出しの上限。</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>API キーに紐づく Pleasanter 利用者のタイムゾーン ID。</summary>
    /// <remarks>
    /// **Pleasanter は日時をこのタイムゾーンとの間で変換する**
    /// （<c>_documents/実機検証結果.md</c> 4 章。実測で確定）。
    /// **運用開始後に変更してはならない。** 変えると保存済みレコードの解釈が変わる。
    /// </remarks>
    public required string ApiKeyUserTimeZoneId { get; set; }
}

/// <summary>固定した Pleasanter 接続設定を返す。</summary>
public sealed class StaticPleasanterOptionsProvider(PleasanterOptions options)
    : IPleasanterOptionsProvider
{
    public Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(options);
}
