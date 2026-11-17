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
/// 満たさないときは通信せず、<see cref="PleasanterConfigurationException"/> を投げる。
/// **認証の失敗でも恒久的な失敗でもなく、サーバー側の設定の誤り（内部エラー）**だからである。
/// 送信ワーカーは、未送信の回答をデッドレターへ落とさず再送の対象のまま残し、管理者へ通知する。
/// 文言は固定で、API キーも URL も含めない。
/// </para>
/// <para>
/// 型付きのメソッド（<c>client.Items.*</c>）は使わず、<see cref="Dnc.PleasanterApiClient.PostJsonAsync"/> だけを使う。
/// 本文はマッピング定義から動的に組む任意の列の辞書で、型付きのモデルでは表せない。型付きは最初の呼び出しの前に
/// 版の問い合わせ（<c>GetLicenseInfo</c>）も行い、本アプリが数えない API の呼び出しが増える（Pleasanter 1.5.6.0 からはレート制限の対象）。
/// <c>DetectServerVersion = false</c> も指定している。
/// </para>
/// <para>
/// **HTTP が 200 でも本文の <c>StatusCode</c> が 200 以外なら、DotNetClient（0.3.0。#95）が失敗として分類する**
/// （<see cref="PleasanterResponse.ApplicationStatusCode"/>）。このクラスは分類しない。
/// <c>Retry-After</c> は読まない（DotNetClient 0.3.0 に無い。従来の自前クライアントも読まず、再送の間隔は送信ワーカーの
/// 再送の予定（<c>ResponseSenderOptions.NextAttemptAt</c>）で決めているため）。429 は <see cref="PleasanterErrorKind.Transient"/>。
/// </para>
/// <para>
/// <b>平文 http は既定で拒否する。</b> <see cref="PleasanterOptions.AllowInsecureHttp"/>（環境変数
/// <c>VVPT_PLEASANTER_ALLOW_INSECURE_HTTP=true</c> が既定値。検証用の環境だけ）で、ループバック以外の http も許す。
/// </para>
/// <para>**実機の Pleasanter では未検証**（フェイクの通信とループバックのみ）。</para>
/// </remarks>
public sealed class PleasanterGateway(
    HttpClient httpClient,
    IPleasanterOptionsProvider optionsProvider)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public PleasanterGateway(HttpClient httpClient, PleasanterOptions options)
        : this(httpClient, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    /// <exception cref="PleasanterConfigurationException"><c>BaseUrl</c> が使えない形のとき（通信しない）。</exception>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var options = await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return await CreateClient(options).CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<PleasanterResponse> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var options = await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var client = CreateClient(options);

        var payload = JsonSerializer.SerializeToNode(body, SerializerOptions) as JsonObject ?? [];

        // 呼び出し側の取り消しは伝わる。それ以外の通信の失敗（接続・タイムアウト）は、DotNetClient が応答なし（Unknown）で返す。
        // HTTP が 200 でも本文の StatusCode が 200 以外なら、DotNetClient（0.3.0。#95）が失敗として分類する
        return await client.PostJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>接続設定から DotNetClient を作る。<c>BaseUrl</c> が使えない形なら <see cref="PleasanterConfigurationException"/>（呼び出しごとに検査する）。</summary>
    /// <remarks>
    /// **接続設定は管理画面で変えられる**ので、クライアントは呼び出しごとに作る（軽い。<see cref="HttpClient"/> は使い回す）。
    /// </remarks>
    private Dnc.PleasanterApiClient CreateClient(PleasanterOptions options)
    {
        var dncOptions = new Dnc.PleasanterOptions
        {
            BaseUrl = options.BaseUrl.TrimEnd('/'),
            ApiKey = options.ApiKey,
            ApiVersion = options.ApiVersion,
            Timeout = options.Timeout,
            DetectServerVersion = false,
            AllowInsecureHttp = options.AllowInsecureHttp,
        };

        try
        {
            // DotNetClient の例外の文言は理由だけで、URL も API キーも含まない。それでも内側の例外にはしない（固定の文言だけを出す）
            dncOptions.Validate();
        }
        catch (ArgumentException)
        {
            throw new PleasanterConfigurationException();
        }

        return new Dnc.PleasanterApiClient(httpClient, dncOptions);
    }
}

/// <summary>
/// Pleasanter への接続先（<c>BaseUrl</c>）の設定が使えない。**サーバー側の設定の誤り（内部エラー）。**
/// </summary>
/// <remarks>
/// 認証の失敗（<see cref="PleasanterErrorKind.Unauthorized"/>）でも、送ったデータの問題（<see cref="PleasanterErrorKind.Permanent"/>）でもない。
/// **文言は固定で、URL・資格情報を含めない。** 呼び出し側は、回答をデッドレターへ落とさず、管理者へ通知する。
/// </remarks>
public sealed class PleasanterConfigurationException()
    : InvalidOperationException("Pleasanter への接続先（BaseUrl）の設定が使えません。")
{
}
