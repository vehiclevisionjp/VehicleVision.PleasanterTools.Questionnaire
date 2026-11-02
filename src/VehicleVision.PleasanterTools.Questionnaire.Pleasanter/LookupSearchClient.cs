using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>
/// ルックアップ専用の Pleasanter 検索クライアント（Issue #828・仕様書 5.2・6.1 節）。
/// </summary>
/// <remarks>
/// <para>
/// **回答の書き込み用の <see cref="PleasanterApiClient"/> とは別。** 専用の資格情報で読み取りだけを行い、
/// 再試行せず、リダイレクトを追わず、応答の大きさと時間に上限を持つ。
/// </para>
/// <para>
/// 渡された <see cref="HttpClient"/> は、**リダイレクトを追わない設定**にすること
/// （<c>AllowAutoRedirect = false</c>）。追った場合でも、最終的な送信先が要求と同じ
/// 配信元（スキーム・ホスト・ポート）でなければ失敗にする。
/// </para>
/// <para>**このクラスはログを出さない。** 結果にも API キー・検索値・生の応答を含めない。</para>
/// </remarks>
public sealed class LookupSearchClient(HttpClient httpClient, LookupSearchOptions? options = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly LookupSearchOptions _options = options ?? new LookupSearchOptions();

    public async Task<LookupSearchResult> SearchAsync(
        LookupSourceConnection connection,
        LookupSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        var lookup = request.Lookup;
        if (!IsUsableDefinition(lookup))
        {
            return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.InvalidDefinition);
        }

        var endpoint = ResolveEndpoint(connection, lookup);
        if (endpoint.Failure != LookupSearchFailure.None)
        {
            return Fail(LookupSearchStatus.SourceInvalid, endpoint.Failure);
        }

        // 条件の生成とキー値の検証。**不備があれば外部検索しない**
        var filter = BuildFilter(request);
        if (filter.Failure != LookupSearchFailure.None)
        {
            return Fail(LookupSearchStatus.SourceInvalid, filter.Failure);
        }

        if (filter.InvalidQuestionIds.Length > 0)
        {
            return new LookupSearchResult
            {
                Status = LookupSearchStatus.InvalidKeys,
                InvalidKeyQuestionIds = filter.InvalidQuestionIds,
            };
        }

        var body = BuildBody(connection, lookup, filter);
        var transfer = await SendAsync(endpoint.Uri!, body, cancellationToken).ConfigureAwait(false);
        if (transfer.Result is not null)
        {
            return transfer.Result;
        }

        return MapResponse(lookup, transfer.Json!);
    }

    private static bool IsUsableDefinition(LookupDefinition lookup) =>
        lookup.SourceKind == LookupSourceKind.Pleasanter
        && lookup.SourceSiteId is > 0
        && !lookup.Keys.IsDefaultOrEmpty
        && lookup.Keys.Length <= LookupLimits.MaxKeys
        && !lookup.AllowedColumns.IsDefaultOrEmpty
        && lookup.MaxResults is >= 1 and <= LookupLimits.SystemMaxResults
        && lookup.Keys.Select(key => key.SourceColumn).Distinct(StringComparer.Ordinal).Count()
            == lookup.Keys.Length
        && lookup.Keys.All(key => !string.IsNullOrWhiteSpace(key.SourceColumn))
        && lookup.AllowedColumns.All(column => !string.IsNullOrWhiteSpace(column));

    private (Uri? Uri, LookupSearchFailure Failure) ResolveEndpoint(
        LookupSourceConnection connection,
        LookupDefinition lookup)
    {
        if (string.IsNullOrEmpty(connection.ApiKey)
            || !Uri.TryCreate(connection.BaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            return (null, LookupSearchFailure.InvalidDefinition);
        }

        var secure = baseUri.Scheme == Uri.UriSchemeHttps
            || (_options.AllowLoopbackHttp
                && baseUri.Scheme == Uri.UriSchemeHttp
                && baseUri.IsLoopback);
        if (!secure)
        {
            return (null, LookupSearchFailure.InsecureTransport);
        }

        return (new Uri(baseUri, $"api/items/{lookup.SourceSiteId}/get"), LookupSearchFailure.None);
    }

    /// <summary>キーごとの単一値の条件。**型付きで作り、値の連結で JSON を作らない。**</summary>
    private static FilterResult BuildFilter(LookupSearchRequest request)
    {
        var lookup = request.Lookup;
        var invalid = ImmutableArray.CreateBuilder<string>();
        var hash = new JsonObject();
        var searchTypes = new JsonObject();

        // 未知のキー ID は拒否する（回答者が任意の条件を足せない）
        var known = lookup.Keys.Select(key => key.QuestionId).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in request.KeyValues.Keys.Where(id => !known.Contains(id)))
        {
            invalid.Add(unknown);
        }

        foreach (var key in lookup.Keys)
        {
            request.KeyValues.TryGetValue(key.QuestionId, out var raw);

            // 種類が分からない列へは送らない。**黙って全件が返り得る**
            if (!request.KeyColumnKinds.TryGetValue(key.SourceColumn, out var kind))
            {
                return new FilterResult([], LookupSearchFailure.InvalidDefinition, hash, searchTypes);
            }

            var prefix = key.MatchMode == LookupMatchMode.Prefix;
            if (prefix && !key.AllowPrefix)
            {
                return new FilterResult([], LookupSearchFailure.InvalidDefinition, hash, searchTypes);
            }

            var checkedValue = LookupKeyValueValidator.Validate(raw, kind, key.MatchMode);
            if (!checkedValue.IsValid)
            {
                invalid.Add(key.QuestionId);
                continue;
            }

            hash[key.SourceColumn] = checkedValue.Value;
            searchTypes[key.SourceColumn] = prefix ? "ForwardMatch" : "ExactMatch";
        }

        return new FilterResult(invalid.ToImmutable(), LookupSearchFailure.None, hash, searchTypes);
    }

    private JsonObject BuildBody(
        LookupSourceConnection connection,
        LookupDefinition lookup,
        FilterResult filter)
    {
        var columns = new JsonArray();
        foreach (var column in lookup.AllowedColumns.Distinct(StringComparer.Ordinal))
        {
            columns.Add(column);
        }

        return new JsonObject
        {
            ["ApiVersion"] = _options.ApiVersion,
            ["ApiKey"] = connection.ApiKey,
            ["Offset"] = 0,

            // K+1 件を要求し、K 超過を検出する
            ["PageSize"] = lookup.MaxResults + 1,
            ["View"] = new JsonObject
            {
                ["ColumnFilterHash"] = filter.Hash,
                ["ColumnFilterSearchTypes"] = filter.SearchTypes,
                ["ApiDataType"] = "KeyValues",
                ["ApiColumnKeyDisplayType"] = "ColumnName",
                ["ApiColumnValueDisplayType"] = "Value",
                ["GridColumns"] = columns,
            },
        };
    }

    private async Task<Transfer> SendAsync(
        Uri uri,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(body, options: SerializerOptions),
            };
            using var response = await httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var statusFailure = ClassifyStatus(response, uri);
            if (statusFailure is not null)
            {
                return new Transfer(statusFailure, null);
            }

            var raw = await ReadLimitedAsync(response, timeout.Token).ConfigureAwait(false);
            if (raw is null)
            {
                return new Transfer(
                    Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.ResponseTooLarge), null);
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                node = null;
            }

            if (node is not JsonObject json)
            {
                return new Transfer(
                    Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.InvalidResponse), null);
            }

            // Pleasanter は本文にも StatusCode を持つ。200 以外なら本文側の値で分類し直す
            if (json["StatusCode"] is JsonValue code
                && code.TryGetValue<int>(out var bodyStatus)
                && bodyStatus != (int)HttpStatusCode.OK)
            {
                return new Transfer(ClassifyCode(bodyStatus), null);
            }

            return new Transfer(null, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Transfer(Fail(LookupSearchStatus.Timeout, LookupSearchFailure.None), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new Transfer(
                Fail(LookupSearchStatus.TemporarilyUnavailable, LookupSearchFailure.ConnectionFailed), null);
        }
    }

    private static LookupSearchResult? ClassifyStatus(HttpResponseMessage response, Uri requested)
    {
        // 追って別の配信元へ着いたなら失敗。**資格情報を別のホストへ送ったことになる**
        var final = response.RequestMessage?.RequestUri;
        if (final is not null
            && (!string.Equals(final.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(final.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
                || final.Port != requested.Port))
        {
            return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Redirected);
        }

        var status = (int)response.StatusCode;
        return status == (int)HttpStatusCode.OK ? null : ClassifyCode(status);
    }

    private static LookupSearchResult ClassifyCode(int status) => status switch
    {
        >= 300 and < 400 => Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Redirected),
        401 => Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Unauthorized),
        403 => Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Forbidden),

        // 権限なしと不存在は区別できない。**0 件にはしない**
        404 => Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.NotFound),
        408 => Fail(LookupSearchStatus.Timeout, LookupSearchFailure.None),
        429 => Fail(LookupSearchStatus.TemporarilyUnavailable, LookupSearchFailure.RateLimited),
        >= 500 => Fail(LookupSearchStatus.TemporarilyUnavailable, LookupSearchFailure.ServerError),
        _ => Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.ClientError),
    };

    /// <summary>応答本文を上限つきで読む。超えたら <c>null</c>。</summary>
    private async Task<byte[]?> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var limit = _options.MaxResponseBytes;
        if (response.Content.Headers.ContentLength is { } declared && declared > limit)
        {
            return null;
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>0/1/複数件・超過への写像と、許可列への再射影。</summary>
    private static LookupSearchResult MapResponse(LookupDefinition lookup, JsonObject json)
    {
        if (json["Response"] is not JsonObject response
            || response["Data"] is not JsonArray data
            || !TryLong(response["TotalCount"], out var total)
            || total < 0)
        {
            return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.InvalidResponse);
        }

        var k = lookup.MaxResults;
        long? effectivePageSize = null;
        if (response["PageSize"] is not null)
        {
            if (!TryLong(response["PageSize"], out var pageSize) || pageSize <= 0)
            {
                return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Inconsistent);
            }

            effectivePageSize = pageSize;
        }

        if (response["Offset"] is not null
            && (!TryLong(response["Offset"], out var offset) || offset != 0))
        {
            return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Inconsistent);
        }

        // 取得配列は min(総件数, 実効ページサイズ)。**一致しなければ信用しない**
        var expected = effectivePageSize is { } size ? Math.Min(total, size) : total;
        if (data.Count != expected || data.Count > k + 1)
        {
            return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.Inconsistent);
        }

        // 上限超過、または切り詰められて全件を確認できない。**取得配列 1 件だけで一意と決めない**
        if (total > k || data.Count != total)
        {
            return Fail(LookupSearchStatus.TooManyResults, LookupSearchFailure.TooManyResults);
        }

        var allowed = lookup.AllowedColumns.Distinct(StringComparer.Ordinal).ToArray();
        var rows = ImmutableArray.CreateBuilder<LookupCandidateRow>(data.Count);
        foreach (var item in data)
        {
            if (item is not JsonObject record || !TryProject(record, allowed, out var values))
            {
                return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.InvalidResponse);
            }

            rows.Add(new LookupCandidateRow(values));
        }

        return new LookupSearchResult
        {
            Status = LookupSearchStatus.Success,
            Candidates = rows.ToImmutable(),
        };
    }

    /// <summary>許可列だけを取り出す。**上流がそれ以外（Title・ID 等）を付けても返さない。**</summary>
    private static bool TryProject(
        JsonObject record,
        string[] allowed,
        out IReadOnlyDictionary<string, string?> values)
    {
        var projected = new Dictionary<string, string?>(StringComparer.Ordinal);
        values = projected;
        foreach (var column in allowed)
        {
            JsonNode? node = null;
            foreach (var property in record)
            {
                if (string.Equals(property.Key, column, StringComparison.OrdinalIgnoreCase))
                {
                    node = property.Value;
                    break;
                }
            }

            switch (node)
            {
                case null:
                    projected[column] = null;
                    break;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    projected[column] = text;
                    break;
                case JsonValue value when value.GetValueKind()
                    is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    projected[column] = value.ToJsonString();
                    break;
                default:
                    // オブジェクト・配列は想定外
                    return false;
            }
        }

        return true;
    }

    private static bool TryLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue json && json.TryGetValue(out value);
    }

    private static LookupSearchResult Fail(LookupSearchStatus status, LookupSearchFailure failure) =>
        new() { Status = status, Failure = failure };

    private sealed record FilterResult(
        ImmutableArray<string> InvalidQuestionIds,
        LookupSearchFailure Failure,
        JsonObject Hash,
        JsonObject SearchTypes);

    private sealed record Transfer(LookupSearchResult? Result, JsonObject? Json);
}
