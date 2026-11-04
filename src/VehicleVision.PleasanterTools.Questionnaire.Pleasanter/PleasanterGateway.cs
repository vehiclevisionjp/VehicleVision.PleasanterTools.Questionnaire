using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dnc = VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>
/// DotNetClient の <see cref="Dnc.PleasanterApiClient"/> を、本アプリの呼び方（辞書の本文・失敗の分類）で包んで Pleasanter の標準 API を叩く。
/// </summary>
/// <remarks>
/// <para>
/// **API キーはリクエストボディに載せる。ヘッダ方式は無い**
/// （<c>_documents/アーキテクチャ方針.md</c> 3 章）。
/// </para>
/// <para>
/// **このクラスは再試行しない。** 失敗の扱いは送信ワーカーが決める。結果は
/// <see cref="PleasanterResponse"/>（DotNetClient の型）で返し、分類は <see cref="PleasanterErrorKind"/> で行う。
/// 呼び出し側の取り消しはそのまま伝わり、**それ以外の通信の失敗（接続・タイムアウト）は <see cref="PleasanterErrorKind.Unknown"/>**
/// （できたかどうか分からない。照合してから判断する）。
/// </para>
/// <para>
/// 通信の経路（<see cref="HttpClient"/>）は呼び出し側が作る。**リダイレクトを辿らない
/// <see cref="Dnc.PleasanterHttp.CreateHandler"/> を使うこと**（API キーは本文に載り、307・308 は本文を転送先へ再送するため。
/// DI では <c>ConfigurePrimaryHttpMessageHandler(PleasanterHttp.CreateHandler)</c>）。3xx は <see cref="PleasanterErrorKind.Permanent"/> で返る。
/// </para>
/// <para>
/// **<c>BaseUrl</c> は DotNetClient が通信の前に呼び出しごとに検査する**（https のみ・ループバックだけ http・資格情報／クエリ／フラグメント拒否）。
/// 満たさないときは通信せず、<see cref="PleasanterErrorKind.Unauthorized"/> で返す（人が設定を直す必要があり、直れば同じ回答が送れるため。
/// 送信ワーカーは再送しつつ管理者へ通知する。<b>Permanent にすると、設定ミスで未送信の回答が全部デッドレターへ落ちる</b>）。
/// 文言は固定で、API キーも URL も含めない。
/// </para>
/// <para>
/// 型付きのメソッド（<c>client.Items.*</c>）は使わず、<see cref="Dnc.PleasanterApiClient.PostJsonAsync"/> だけを使う。
/// 本文はマッピング定義から動的に組む任意の列の辞書で、型付きのモデルでは表せない。型付きは最初の呼び出しの前に
/// 版の問い合わせ（<c>GetLicenseInfo</c>）も行い、本アプリが数えない API の呼び出しが増える（Pleasanter 1.5.6.0 からはレート制限の対象）。
/// <c>DetectServerVersion = false</c> も指定している。
/// </para>
/// <para>
/// **DotNetClient 0.2.0 に無いものを、ここで補っている。** 該当の変更を含む版へ上げたら、不要になった分を消す。
/// </para>
/// <list type="bullet">
/// <item><description>HTTP が 200 でも、本文の <c>StatusCode</c> が 200 以外なら失敗として分類する
/// （DotNetClient #95・PR #96 は develop にあるが 0.2.0 には無い。<b>DotNetClient #95 を含む版へ上げたら、この補いは消せる</b>）</description></item>
/// <item><description><c>Retry-After</c> は読まない。従来の自前クライアントも読まず、再送の間隔は送信ワーカーの
/// 再送の予定（<c>ResponseSenderOptions.NextAttemptAt</c>）で決めているため</description></item>
/// </list>
/// <para>**実機の Pleasanter では未検証**（フェイクの通信とループバックのみ）。</para>
/// </remarks>
public sealed class PleasanterGateway(
    HttpClient httpClient,
    IPleasanterOptionsProvider optionsProvider)
{
    /// <summary>接続先の URL が使えないときの文言。**API キーも URL も含めない。**</summary>
    public const string InvalidBaseUrlMessage =
        "Pleasanter の URL が使えません。https（ループバックだけ http 可）で、資格情報・クエリ・フラグメントを含めずに指定してください。";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public PleasanterGateway(HttpClient httpClient, PleasanterOptions options)
        : this(httpClient, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    /// <remarks><c>BaseUrl</c> が使えない形のときは、通信せずに <c>false</c>。</remarks>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var options = await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var client = TryCreateClient(options);
        return client is not null
            && await client.CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>レコードを作る。</summary>
    public Task<PleasanterResponse> CreateAsync(
        long siteId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/Create", record, cancellationToken);

    /// <summary>レコードを更新する。</summary>
    public Task<PleasanterResponse> UpdateAsync(
        long referenceId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Update", record, cancellationToken);

    /// <summary>レコードを取り出す。</summary>
    public Task<PleasanterResponse> GetAsync(
        long siteId,
        IReadOnlyDictionary<string, object?>? view = null,
        CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/items/{siteId}/Get",
            view is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["View"] = view },
            cancellationToken);

    /// <summary>レコードを 1 件取り出す。</summary>
    /// <remarks>
    /// **添付を消すために使う。** 消すには <c>Guid</c> が要り、
    /// <c>Guid</c> は取り出さないと分からない（<c>_documents/実機検証結果.md</c> 8 章）。
    /// </remarks>
    public Task<PleasanterResponse> GetRecordAsync(
        long referenceId,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Get", new Dictionary<string, object?>(), cancellationToken);

    /// <summary>サイト設定を取り出す。マッピング先の列を調べるのに使う。</summary>
    public Task<PleasanterResponse> GetSiteAsync(
        long siteId,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/GetSite", new Dictionary<string, object?>(), cancellationToken);

    /// <summary>マッピング先にするサイトを作成する。</summary>
    public Task<PleasanterResponse> CreateSiteAsync(
        long parentSiteId,
        IReadOnlyDictionary<string, object?> site,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{parentSiteId}/CreateSite", site, cancellationToken);

    /// <summary>サイト設定を丸ごと更新する。</summary>
    /// <remarks>
    /// <c>UpdateSite</c> は差分更新ではない。呼び出し側は必ず <c>GetSite</c> の
    /// <c>SiteSettings</c> を読み、必要な列だけを変更して渡す。
    /// </remarks>
    public Task<PleasanterResponse> UpdateSiteAsync(
        long siteId,
        IReadOnlyDictionary<string, object?> site,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/UpdateSite", site, cancellationToken);

    /// <summary>回答トークンで既存レコードを探す。応答不明の <c>Create</c> の照合に使う。</summary>
    /// <remarks>
    /// **回答の正本 JSON にトークンを埋めておき、部分一致で引く**
    /// （<c>_documents/実機検証結果.md</c> 7 章。実機で確認済み）。
    /// **部分一致は索引が効かない。** 照合のときだけ使うこと。
    /// </remarks>
    public Task<PleasanterResponse> FindByResponseTokenAsync(
        long siteId,
        string responseJsonColumn,
        string responseToken,
        CancellationToken cancellationToken = default)
        => GetAsync(
            siteId,
            new Dictionary<string, object?>
            {
                ["ColumnFilterHash"] = new Dictionary<string, object?>
                {
                    [responseJsonColumn] = responseToken,
                },
            },
            cancellationToken);

    /// <summary>本文の <c>StatusCode</c> を、HTTP のステータスと同じ表で分類する。</summary>
    private static PleasanterErrorKind Classify(int statusCode) => statusCode switch
    {
        (int)HttpStatusCode.OK => PleasanterErrorKind.None,
        (int)HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,
        (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,
        (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests
            => PleasanterErrorKind.Transient,
        >= 500 => PleasanterErrorKind.Transient,
        _ => PleasanterErrorKind.Permanent,
    };

    private async Task<PleasanterResponse> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var options = await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var client = TryCreateClient(options);
        if (client is null)
        {
            // 通信しない。**人が設定を直すまで通らない**（キーの失効と同じ扱い）
            return new PleasanterResponse(PleasanterErrorKind.Unauthorized, Message: InvalidBaseUrlMessage);
        }

        var payload = JsonSerializer.SerializeToNode(body, SerializerOptions) as JsonObject ?? [];

        // 呼び出し側の取り消しは伝わる。それ以外の通信の失敗（接続・タイムアウト）は、DotNetClient が応答なし（Unknown）で返す
        var response = await client.PostJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);

        // 応答本文の StatusCode も見る（HTTP が 200 でも本文でエラーを返す場合に備える）。
        // DotNetClient #95 を含む版へ上げたら消せる（DotNetClient 0.2.0 は HTTP のステータスだけで分類する）
        if (response.IsSuccess
            && response.Body is JsonObject node
            && node["StatusCode"] is JsonValue statusNode
            && statusNode.TryGetValue<int>(out var bodyStatus)
            && bodyStatus != (int)HttpStatusCode.OK)
        {
            return response with { ErrorKind = Classify(bodyStatus), StatusCode = bodyStatus };
        }

        return response;
    }

    /// <summary>接続設定から DotNetClient を作る。<c>BaseUrl</c> が使えない形なら <c>null</c>（呼び出しごとに検査する）。</summary>
    /// <remarks>
    /// **接続設定は管理画面で変えられる**ので、クライアントは呼び出しごとに作る（軽い。<see cref="HttpClient"/> は使い回す）。
    /// </remarks>
    private Dnc.PleasanterApiClient? TryCreateClient(PleasanterOptions options)
    {
        var dncOptions = new Dnc.PleasanterOptions
        {
            BaseUrl = options.BaseUrl.TrimEnd('/'),
            ApiKey = options.ApiKey,
            ApiVersion = options.ApiVersion,
            Timeout = options.Timeout,
            DetectServerVersion = false,
        };

        try
        {
            // 例外の文言は理由だけで、URL も API キーも含まない。ここで握りつぶし、画面へ出す文言は固定にする
            dncOptions.Validate();
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new Dnc.PleasanterApiClient(httpClient, dncOptions);
    }
}
