using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Routing.Patterns;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理操作を <c>AuditLogs</c> へ残す。</summary>
/// <remarks>
/// <para>
/// **要求の本文には触らない。** 記録するのは宛先・結果・経路の値だけ。
/// 触らない限り、合言葉も招待の合言葉も 2 要素の共有鍵も入りようがない
/// （<c>_documents/データモデル設計.md</c> 2.6「回答本文を入れない」）。
/// **入れない約束を、書ける場所を絞ることで守る。**
/// </para>
/// <para>
/// **1 つ 1 つの入口に書き足さない。** 20 か所へ手で置くと、
/// 後から足された入口だけが記録されない。**入口の集まりに 1 回掛ける。**
/// </para>
/// <para>
/// **失敗した操作も残す**（Issue #19）。断られた試みが残っていないと、
/// 「起きなかった」と「気付けなかった」を区別できない。
/// </para>
/// <para>
/// **読み取りは残さない。** 一覧を開くたびに行が増えると、
/// 本当に見たいもの（変えた操作）が埋もれる。
/// </para>
/// </remarks>
public sealed partial class AuditLogFilter(
    IAuditLogStore store,
    ILogger<AuditLogFilter> logger) : IEndpointFilter
{
    /// <summary>補足を書き出す設定。</summary>
    /// <remarks>
    /// **日本語をそのまま入れる。** 既定の設定は非 ASCII を <c>\uXXXX</c> へ逃がすので、
    /// ログイン ID に日本語が入っていると DB の中で読めず、検索もできなくなる。
    /// **HTML に効く文字（&lt; &gt; &amp; ' "）は逃がしたまま**にしてある
    /// （<c>UnsafeRelaxedJsonEscaping</c> ではなく <c>UnicodeRanges.All</c> を使う理由）。
    /// 画面へ出すときの逃がし忘れが、そのまま script の挿し込みにならないようにする。
    /// </remarks>
    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
            System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>経路の値から対象を見分ける。**順番に見て、最初に当たったものを使う。**</summary>
    private static readonly (string RouteKey, string TargetType)[] Targets =
    [
        ("adminUserId", "AdminUser"),
        ("surveyId", "Survey"),
    ];

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        // **読み取りは残さない**
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        {
            return await next(context).ConfigureAwait(false);
        }

        var result = await next(context).ConfigureAwait(false);

        // **記録が失敗しても操作は通す。** 操作はもう済んでおり、
        // ここで例外にすると「通ったのに失敗と伝える」ことになる
        try
        {
            await WriteAsync(http, StatusCodeOf(result, http)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // **握り潰さない。** 記録できていないこと自体が異常
            logger.LogError(
                exception,
                "管理操作を監査ログへ残せなかった（{Action}）",
                LogSafe.Text(ActionOf(http)));
        }

        return result;
    }

    /// <summary>この応答の状態。</summary>
    /// <remarks>
    /// **応答オブジェクトから読む。** 最小 API では、まだ書き出されていないので
    /// <c>HttpContext.Response.StatusCode</c> は既定値のままのことがある。
    /// </remarks>
    private static int StatusCodeOf(object? result, HttpContext http) =>
        result switch
        {
            IStatusCodeHttpResult { StatusCode: { } status } => status,
            // 明示しない Results.Ok などは 200
            IResult => StatusCodes.Status200OK,
            _ => http.Response.StatusCode,
        };

    /// <summary>何をしたか。<c>POST /api/admin/users/{adminUserId}/role</c> の形。</summary>
    /// <remarks>
    /// **実際の URL ではなく経路の型を残す。** 識別子は <c>TargetId</c> に入るので、
    /// 型で残しておくと「役割の変更」をまとめて数えられる。
    /// </remarks>
    private static string ActionOf(HttpContext http)
    {
        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return $"{http.Request.Method} {Readable(pattern ?? http.Request.Path.Value ?? "/")}";
    }

    /// <summary>経路から型の制約を落とす。<c>{surveyId:guid}</c> → <c>{surveyId}</c>。</summary>
    /// <remarks>
    /// **制約は経路を照合するための都合で、読む人には要らない。**
    /// 残すと 1 行が横に長くなり、同じ操作かどうかも見分けにくくなる。
    /// </remarks>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\{([A-Za-z_][A-Za-z0-9_]*):[^}]*\}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex RouteConstraintPattern();

    private static string Readable(string pattern) =>
        RouteConstraintPattern().Replace(pattern, "{$1}");

    /// <summary>送信元。**IPv4 を IPv6 の形で残さない。**</summary>
    /// <remarks>
    /// Kestrel は IPv4 の接続を <c>::ffff:172.18.0.7</c> の形で返すことがある。
    /// **同じ相手が 2 通りの書き方で残ると、送信元で絞れなくなる。**
    /// </remarks>
    private static string? IpAddressOf(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private async Task WriteAsync(HttpContext http, int statusCode)
    {
        var (targetType, targetId) = TargetOf(http);

        await store.WriteAsync(
            new AuditEntry(
                DateTime.Now,
                AdminUserIdOf(http),
                ActionOf(http),
                statusCode,
                targetType,
                targetId,
                DetailOf(http),
                IpAddressOf(http)),
            http.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>誰が。**認証を通っていない試み（招待の受け取りなど）では <c>null</c>。**</summary>
    private static Guid? AdminUserIdOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminUserId)
            ? adminUserId
            : null;

    private static (string? TargetType, string? TargetId) TargetOf(HttpContext http)
    {
        // **入口が明示したものを優先する**（AuditNotes.SetTarget）。
        // 対象の識別子を経路へ出せない入口がある
        // （デッドレターの再送。`ResponseToken` は監査ログへ入れない）
        if (AuditNotes.TargetOf(http) is { } declared)
        {
            return declared;
        }

        foreach (var (routeKey, targetType) in Targets)
        {
            if (http.Request.RouteValues.TryGetValue(routeKey, out var value)
                && value?.ToString() is { Length: > 0 } text)
            {
                return (targetType, text);
            }
        }

        return (null, null);
    }

    /// <summary>補足。**経路の値だけ**を入れる。</summary>
    /// <remarks>
    /// **要求本文と問い合わせ文字列は入れない。**
    /// 本文には合言葉が、問い合わせ文字列には招待の合言葉が載り得る。
    /// 経路の値は URL の一部で、識別子しか入らない。
    /// </remarks>
    private static string? DetailOf(HttpContext http)
    {
        var values = http.Request.RouteValues
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.ToString() ?? string.Empty);

        // **入口が明示して預けたものだけ足す**（AuditNotes）。
        // 経路の値と名前がぶつかったら、預けた側を勝たせる
        foreach (var (key, value) in
            AuditNotes.Of(http) ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())
        {
            values[key] = value;
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values, DetailOptions);
    }
}
