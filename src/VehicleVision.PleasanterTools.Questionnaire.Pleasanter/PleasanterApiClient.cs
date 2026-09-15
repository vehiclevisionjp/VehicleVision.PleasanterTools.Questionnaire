using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>Pleasanter の応答をどう扱うか。</summary>
/// <remarks>
/// **分類を誤ると、直らないものを延々と再送し続けるか、直るものを捨てる**
/// （<c>_documents/アプリケーション設計.md</c> 4 章）。
/// </remarks>
public enum PleasanterErrorKind
{
    /// <summary>成功。</summary>
    None,

    /// <summary>一時的。**再送する。**</summary>
    Transient,

    /// <summary>恒久的。**再送しても通らない。デッドレターへ。**</summary>
    Permanent,

    /// <summary>認証に失敗。**一時的として再送しつつ、管理者へ通知する。**</summary>
    /// <remarks>キーの失効・設定ミスは人が直す必要がある。</remarks>
    Unauthorized,

    /// <summary>応答が返らなかった。**できたかどうか分からない。照合してから判断する。**</summary>
    Unknown,
}

/// <summary>Pleasanter 呼び出しの結果。</summary>
/// <param name="ErrorKind">扱い方。</param>
/// <param name="StatusCode">HTTP のステータス。応答が無ければ <c>null</c>。</param>
/// <param name="Id">作成・更新されたレコードの ID。</param>
/// <param name="Message">Pleasanter が返した文言。</param>
/// <param name="Body">応答の中身。</param>
public sealed record PleasanterResponse(
    PleasanterErrorKind ErrorKind,
    int? StatusCode = null,
    long? Id = null,
    string? Message = null,
    JsonNode? Body = null)
{
    public bool IsSuccess => ErrorKind is PleasanterErrorKind.None;
}

/// <summary>Pleasanter の標準 API を叩く。</summary>
/// <remarks>
/// <para>
/// **API キーはリクエストボディに載せる。ヘッダ方式は無い**
/// （<c>_documents/アーキテクチャ方針.md</c> 3 章）。
/// </para>
/// <para>
/// **このクラスは再試行しない。** 失敗の扱いは送信ワーカーが決める。
/// </para>
/// </remarks>
public sealed class PleasanterApiClient(HttpClient httpClient, PleasanterOptions options)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

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
        var payload = new Dictionary<string, object?>(body, StringComparer.Ordinal)
        {
            ["ApiKey"] = options.ApiKey,
            ["ApiVersion"] = options.ApiVersion,
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}/{path}")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 送れたのか届いたのか分からない。**照合してから判断する**
            return new PleasanterResponse(PleasanterErrorKind.Unknown);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                // Pleasanter は JSON を返す。返らないなら経路の問題とみなす
            }

            var id = node?["Id"]?.GetValue<long?>();
            var message = node?["Message"]?.GetValue<string?>();
            var status = (int)response.StatusCode;

            return new PleasanterResponse(Classify(response.StatusCode), status, id, message, node);
        }
    }

    private static PleasanterErrorKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => PleasanterErrorKind.None,

        // キーの失効・設定ミス。人が直すまで通らないが、直れば同じ回答が送れる
        HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,

        // 送ったデータか権限の問題。**再送しても通らない**
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,

        // 混雑・一時的な不調
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            => PleasanterErrorKind.Transient,

        _ => (int)statusCode >= 500
            ? PleasanterErrorKind.Transient
            : PleasanterErrorKind.Permanent,
    };
}
