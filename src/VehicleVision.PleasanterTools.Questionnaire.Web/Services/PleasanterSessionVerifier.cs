using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Pleasanter へ問い合わせた結果の種類。</summary>
public enum PleasanterSessionStatus
{
    /// <summary>Pleasanter にログインしている（2 要素まで済んでいる）。</summary>
    Authenticated,

    /// <summary>Pleasanter にログインしていない。</summary>
    Unauthenticated,

    /// <summary>
    /// 問い合わせが成り立たなかった（接続できない・時間切れ・想定外の応答）。
    /// **成功とは決して扱わない。**
    /// </summary>
    UpstreamError,
}

/// <summary>Pleasanter が返した本人。</summary>
public sealed record PleasanterIdentity(int TenantId, int UserId, string LoginId, string? Name);

/// <summary>Pleasanter へ問い合わせた結果。</summary>
/// <param name="Reason">
/// ログと操作の記録に残す短い理由（英小文字とハイフン）。**画面へは出さない。**
/// </param>
public sealed record PleasanterSessionResult(
    PleasanterSessionStatus Status,
    PleasanterIdentity? Identity = null,
    string Reason = "")
{
    public static PleasanterSessionResult Unauthenticated(string reason) =>
        new(PleasanterSessionStatus.Unauthenticated, Reason: reason);

    public static PleasanterSessionResult Error(string reason) =>
        new(PleasanterSessionStatus.UpstreamError, Reason: reason);
}

/// <summary>ブラウザが持つ Pleasanter の cookie で、Pleasanter に誰がログインしているかを聞く（Issue #464）。</summary>
public interface IPleasanterSessionVerifier
{
    /// <summary>要求の cookie を転送して、Pleasanter に本人を聞く。</summary>
    /// <param name="cookieHeaders">ブラウザから届いた <c>Cookie</c> ヘッダの生値。</param>
    Task<PleasanterSessionResult> VerifyAsync(
        StringValues cookieHeaders,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Pleasanter の拡張 SQL API へ cookie を転送して本人を聞く。</summary>
/// <remarks>
/// <para>
/// **API キーは送らない。** Pleasanter は API キーが無ければ cookie の認証で利用者を決める
/// （<c>_reference/Implem.Pleasanter/Implem.Pleasanter/Libraries/Requests/Context.cs</c>）。
/// 拡張 SQL は Pleasanter が自動で束縛する利用者 ID で本人の行だけを返す。
/// 本文の <c>Params</c> で利用者 ID を偽っても無視される（2026-09-24 に実機で確認）。
/// </para>
/// <para>
/// ⚠️ **成功と判断するのは、JSON の業務ステータスが 200 で、TenantId・UserId・LoginId が
/// きっちり読めたときだけ。** 転送（3xx）・HTML・時間切れ・JSON でないもの・5xx は
/// すべて <see cref="PleasanterSessionStatus.UpstreamError"/>。401 だけが「ログインしていない」。
/// </para>
/// <para>
/// ⚠️ **cookie の値はログへ出さない。** 名前の数と応答の状態だけを残す。
/// </para>
/// <para>
/// **HttpClient は cookie を覚えない・転送を追わない設定で作る**（<c>Program.cs</c> の
/// <see cref="HttpClientName"/>）。cookie は要求ごとに <c>Cookie</c> ヘッダで付ける。
/// 覚える設定だと、ある管理者の cookie が別の管理者の問い合わせへ混ざる。
/// </para>
/// </remarks>
public sealed class PleasanterSessionVerifier(
    IHttpClientFactory clientFactory,
    ILogger<PleasanterSessionVerifier> logger) : IPleasanterSessionVerifier
{
    /// <summary>問い合わせに使う HttpClient の名前。</summary>
    public const string HttpClientName = "PleasanterSso";

    /// <summary>読む応答の上限。**本人 1 行なので小さい。**</summary>
    private const int MaxResponseBytes = 64 * 1024;

    /// <summary>拡張 SQL API へ渡す版。**Pleasanter の API の版で、本アプリの設定とは別。**</summary>
    private const decimal ApiVersion = 1.1m;

    public async Task<PleasanterSessionResult> VerifyAsync(
        StringValues cookieHeaders,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || options.InternalBaseUrl is null)
        {
            return PleasanterSessionResult.Error("disabled");
        }

        var (cookieHeader, forwardedCount) = BuildCookieHeader(cookieHeaders, options);
        if (forwardedCount == 0)
        {
            // **送るものが無ければ聞くまでもない。** Pleasanter へ無駄な要求を出さない
            return PleasanterSessionResult.Unauthenticated("no-cookie");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.ExtendedSqlUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { ApiVersion, Name = options.SqlName }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await clientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Pleasanter への本人確認が時間内に終わりませんでした（{Timeout} 秒）。",
                options.Timeout.TotalSeconds);
            return PleasanterSessionResult.Error("timeout");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Pleasanter へ本人確認を問い合わせられませんでした。");
            return PleasanterSessionResult.Error("unreachable");
        }

        using (response)
        {
            try
            {
                return await ReadAsync(response, stopwatch, forwardedCount, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Pleasanter の応答を時間内に読み切れませんでした。");
                return PleasanterSessionResult.Error("timeout");
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Pleasanter の応答を読めませんでした。");
                return PleasanterSessionResult.Error("unreachable");
            }
        }
    }

    /// <summary>転送してよい cookie だけを並べ直す。</summary>
    /// <remarks>
    /// **生のヘッダから切り出す。** <c>Request.Cookies</c> は値を URL デコードして返すため、
    /// 組み立て直すと元と違う値になり得る。**受け取った形のまま渡す。**
    /// </remarks>
    public static (string Header, int Count) BuildCookieHeader(
        StringValues cookieHeaders,
        PleasanterSsoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder();
        var count = 0;

        foreach (var header in cookieHeaders)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            foreach (var part in header.Split(';'))
            {
                var pair = part.Trim();
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var name = pair[..separator].Trim();
                if (!options.ShouldForwardCookie(name))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

                builder.Append(name).Append('=').Append(pair[(separator + 1)..].Trim());
                count++;
            }
        }

        return (builder.ToString(), count);
    }

    private async Task<PleasanterSessionResult> ReadAsync(
        HttpResponseMessage response,
        Stopwatch stopwatch,
        int forwardedCount,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogInformation(
                "Pleasanter は未ログインと答えました（cookie {CookieCount} 個、{ElapsedMs} ms）。",
                forwardedCount,
                stopwatch.ElapsedMilliseconds);
            return PleasanterSessionResult.Unauthenticated("http-401");
        }

        if (status is >= 300 and < 400)
        {
            // **転送は追わない。** ログイン画面への転送などは「成り立たなかった」扱い
            logger.LogWarning(
                "Pleasanter が本人確認に転送（{StatusCode}）で答えました。内部 URL と拡張 SQL の名前が正しいか確認してください（名前が見つからないときも転送が返ります）。",
                status);
            return PleasanterSessionResult.Error("redirect");
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Pleasanter が本人確認に {StatusCode} で答えました。拡張 SQL の名前・Api 指定・IP 制限・TokenCheck を確認してください。",
                status);
            return PleasanterSessionResult.Error($"http-{status}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null
            || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning(
                "Pleasanter の本人確認の応答が JSON ではありませんでした（{MediaType}）。",
                LogSafe.Text(mediaType));
            return PleasanterSessionResult.Error("not-json");
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            logger.LogWarning("Pleasanter の本人確認の応答が大きすぎます。");
            return PleasanterSessionResult.Error("too-large");
        }

        byte[] body;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            body = await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false) ?? [];
            if (body.Length == 0)
            {
                logger.LogWarning("Pleasanter の本人確認の応答が空か、大きすぎます。");
                return PleasanterSessionResult.Error("empty-or-too-large");
            }
        }

        var result = Parse(body);
        switch (result.Status)
        {
            case PleasanterSessionStatus.Authenticated:
                logger.LogInformation(
                    "Pleasanter が本人を返しました（TenantId {TenantId}、UserId {UserId}、{ElapsedMs} ms）。",
                    result.Identity!.TenantId,
                    result.Identity.UserId,
                    stopwatch.ElapsedMilliseconds);
                break;
            case PleasanterSessionStatus.Unauthenticated:
                logger.LogInformation("Pleasanter は未ログインと答えました（{Reason}）。", result.Reason);
                break;
            default:
                logger.LogWarning(
                    "Pleasanter の本人確認の応答を読めませんでした（{Reason}）。拡張 SQL の定義を確認してください。",
                    result.Reason);
                break;
        }

        return result;
    }

    /// <summary>上限まで読む。**超えたら <c>null</c>。**</summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>拡張 SQL API の応答を読む。</summary>
    /// <remarks>
    /// 期待する形（2026-09-24 に Pleasanter 1.5.8.1 で実測）:
    /// <c>{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":1,"LoginId":"...","Name":"..."}]}}}</c>。
    /// **1 行ちょうどでなければ成功にしない。** 0 行や複数行は定義の誤りとして扱う。
    /// </remarks>
    public static PleasanterSessionResult Parse(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return PleasanterSessionResult.Error("malformed-json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("StatusCode", out var statusCode)
                || statusCode.ValueKind != JsonValueKind.Number
                || !statusCode.TryGetInt32(out var businessStatus))
            {
                return PleasanterSessionResult.Error("no-status");
            }

            if (businessStatus == 401)
            {
                return PleasanterSessionResult.Unauthenticated("status-401");
            }

            if (businessStatus != 200)
            {
                return PleasanterSessionResult.Error($"status-{businessStatus}");
            }

            if (!root.TryGetProperty("Response", out var responseElement)
                || responseElement.ValueKind != JsonValueKind.Object
                || !responseElement.TryGetProperty("Data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return PleasanterSessionResult.Error("no-data");
            }

            // **表の名前に依らず、表が 1 つ・行が 1 つであることを求める。**
            // Pleasanter は DataSet の表を名前ごとに返す（既定は "Table"）
            JsonElement? table = null;
            foreach (var property in data.EnumerateObject())
            {
                if (table is not null)
                {
                    return PleasanterSessionResult.Error("multiple-tables");
                }

                table = property.Value;
            }

            if (table is not { ValueKind: JsonValueKind.Array } rows)
            {
                return PleasanterSessionResult.Error("no-table");
            }

            if (rows.GetArrayLength() != 1)
            {
                return PleasanterSessionResult.Error(rows.GetArrayLength() == 0 ? "no-row" : "multiple-rows");
            }

            var row = rows[0];
            if (row.ValueKind != JsonValueKind.Object
                || !TryGetPositiveInt(row, "TenantId", out var tenantId)
                || !TryGetPositiveInt(row, "UserId", out var userId)
                || !row.TryGetProperty("LoginId", out var loginIdElement)
                || loginIdElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(loginIdElement.GetString()))
            {
                return PleasanterSessionResult.Error("invalid-row");
            }

            string? name = row.TryGetProperty("Name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

            return new PleasanterSessionResult(
                PleasanterSessionStatus.Authenticated,
                new PleasanterIdentity(tenantId, userId, loginIdElement.GetString()!.Trim(), name));
        }
    }

    private static bool TryGetPositiveInt(JsonElement row, string name, out int value)
    {
        value = 0;
        return row.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value > 0;
    }
}
