using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>トークンの要求の、監査へ残す出来事（<c>Core_AuditLogs.Action</c> の値）。</summary>
/// <remarks>
/// <b>名前を変えると、過去の記録と食い違う</b>ので変えないこと。人の操作（<c>METHOD 経路</c>）とは別の名前にして、
/// 後から「トークンの出来事だけ」を絞れるようにする。
/// </remarks>
public static class AdminApiTokenAuditActions
{
    /// <summary>認証の失敗（不存在・期限切れ・失効・持ち主の停止・許可リスト外・機能の無効・ヘッダの形の違い）。</summary>
    public const string AuthFailed = "ApiToken.AuthFailed";

    /// <summary>スコープ不足など、認証は通ったが認可で断った（403）。</summary>
    public const string Forbidden = "ApiToken.Forbidden";

    /// <summary>トークン単位の枠を超えた（429）。<b>窓ごとに 1 回だけ</b>残す。</summary>
    public const string RateLimited = "ApiToken.RateLimited";
}

/// <summary>トークンの要求の出来事を、監査（<c>Core_AuditLogs</c>）へ残す（Issue #1027。親 #1012）。</summary>
/// <remarks>
/// <para>
/// <b>トークンの値は残さない。</b>残すのは、識別子・接頭辞（<c>qat_</c> ＋ 16 進 8 文字）・送信元・経路の型・理由だけ。
/// 見つからなかったトークンの接頭辞は、<b>形が合うときだけ</b>残す（<see cref="PublicPrefixPattern"/>。
/// 送り手が好きな文字列を書ける場所なので、形で絞る）。<c>Authorization</c> ヘッダ・要求の本文・問い合わせ文字列は触らない。
/// </para>
/// <para>
/// <b>記録が失敗しても要求は通す／止めない</b>（握り潰さずログへ出す）。
/// <b>認証の失敗は、失敗の枠（IP ごと）が書き込みの数を縛る</b>（枠を超えた要求は、DB を引く前に <c>429</c>）。
/// </para>
/// </remarks>
public sealed partial class AdminApiTokenAuditor(
    IAuditLogStore store,
    ILogger<AdminApiTokenAuditor> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    [GeneratedRegex(@"^qat_[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicPrefixPattern();

    /// <summary>認証の失敗を残す。<paramref name="reason"/> は <see cref="AdminApiTokenRejection"/> の名前、またはヘッダの形の違い。</summary>
    public Task WriteAuthFailureAsync(HttpContext http, string reason)
    {
        ArgumentNullException.ThrowIfNull(http);

        var validation = AdminApiTokenAuthentication.ValidationOf(http);
        var detail = new Dictionary<string, string>
        {
            ["reason"] = LogSafe.Text(reason),
            ["credential"] = AuditLogFilter.ActorKindApiToken,
            ["operation"] = LogSafe.Text(AuditLogFilter.ActionOf(http)),
        };

        // 見つかったトークンの接頭辞。無ければ、送られた値の形が合うときだけ（値そのものは残さない）
        var prefix = validation?.Token?.Prefix ?? PresentedPrefixOf(http.Request);
        if (prefix is not null)
        {
            detail[AuditLogFilter.TokenPrefixKey] = prefix;
        }

        var token = validation?.Token;
        return WriteAsync(
            http,
            AdminApiTokenAuditActions.AuthFailed,
            StatusCodes.Status401Unauthorized,
            actorKey: null,
            actorLoginId: null,
            targetId: token?.TokenId.ToString(),
            detail);
    }

    /// <summary>認証は通ったが、認可で断った（403）ことを残す。</summary>
    public Task WriteForbiddenAsync(HttpContext http) =>
        WriteAuthenticatedAsync(http, AdminApiTokenAuditActions.Forbidden, StatusCodes.Status403Forbidden, null);

    /// <summary>トークン単位の枠を超えた（429）ことを残す。</summary>
    /// <param name="bucket"><c>read</c> または <c>write</c>。</param>
    public Task WriteRateLimitedAsync(HttpContext http, string bucket) =>
        WriteAuthenticatedAsync(http, AdminApiTokenAuditActions.RateLimited, StatusCodes.Status429TooManyRequests, bucket);

    private Task WriteAuthenticatedAsync(HttpContext http, string action, int statusCode, string? bucket)
    {
        ArgumentNullException.ThrowIfNull(http);

        var user = http.User;
        var detail = new Dictionary<string, string>
        {
            [AuditLogFilter.ActorKindKey] = AuditLogFilter.ActorKindApiToken,
            [AuditLogFilter.TokenIdKey] = LogSafe.Text(user.FindFirstValue(AdminApiTokenAuthentication.TokenIdClaim)),
            [AuditLogFilter.TokenPrefixKey] = LogSafe.Text(user.FindFirstValue(AdminApiTokenAuthentication.TokenPrefixClaim)),
            ["operation"] = LogSafe.Text(AuditLogFilter.ActionOf(http)),
        };
        if (bucket is not null)
        {
            detail["bucket"] = bucket;
        }

        return WriteAsync(
            http,
            action,
            statusCode,
            actorKey: Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerId)
                ? ownerId.ToString("D")
                : null,
            actorLoginId: user.FindFirstValue(ClaimTypes.Name),
            targetId: user.FindFirstValue(AdminApiTokenAuthentication.TokenIdClaim),
            detail);
    }

    private async Task WriteAsync(
        HttpContext http,
        string action,
        int statusCode,
        string? actorKey,
        string? actorLoginId,
        string? targetId,
        Dictionary<string, string> detail)
    {
        try
        {
            await store.WriteAsync(
                new AuditEntry(
                    OccurredAt: AuditLogTime.ToStored(_time.GetLocalNow().DateTime),
                    Action: action,
                    Succeeded: false,
                    StatusCode: statusCode,
                    ActorKey: actorKey,
                    ActorLoginId: actorLoginId,
                    TargetType: targetId is null ? null : "AdminApiToken",
                    TargetId: targetId,
                    DetailJson: JsonSerializer.Serialize(detail, AuditLogFilter.DetailOptions),
                    IpAddress: AuditLogFilter.IpAddressOf(http)),
                http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **握り潰さない。** 記録できていないこと自体が異常
            logger.LogError(
                exception,
                "管理 API のトークンの出来事を監査ログへ残せなかった（{Action}）",
                LogSafe.Text(action));
        }
    }

    /// <summary>
    /// 送られたトークンの接頭辞。<b>形が合うときだけ</b>返す（<c>qat_</c> ＋ 16 進 8 文字。値の残りは読まない・返さない）。
    /// </summary>
    internal static string? PresentedPrefixOf(HttpRequest request)
    {
        var headers = request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } value)
        {
            return null;
        }

        var token = value["Bearer".Length..].Trim();
        if (token.Length < AdminApiTokenSecret.PrefixLength)
        {
            return null;
        }

        var prefix = token[..AdminApiTokenSecret.PrefixLength];
        return PublicPrefixPattern().IsMatch(prefix) ? prefix : null;
    }
}

/// <summary>
/// <b>認証の前</b>に、認証の失敗の枠（送信元 IP ごと）を見る（Issue #1027）。
/// 使い切っている IP の Bearer の要求は、DB を引く前に <c>429</c> で止める。
/// </summary>
/// <remarks>
/// 見るだけで数えない（数えるのは認証の後。<see cref="AdminApiTokenLimitMiddleware"/>）。
/// <b>Bearer の要求だけが対象</b>で、cookie の要求は巻き込まない。
/// </remarks>
public sealed class AdminApiTokenFailureGateMiddleware(
    RequestDelegate next,
    AdminApiTokenRateGate gate,
    AdminApiTokenOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!AdminApiTokenAuthentication.UsesBearer(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var decision = await gate
            .PeekFailuresAsync(AdminApiTokenGuardHelper.IpKeyOf(context), options.FailuresPer5Min, context.RequestAborted)
            .ConfigureAwait(false);
        if (!decision.Allowed)
        {
            AdminApiTokenGuardHelper.Reject429(context, decision.RetryAfter);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// <b>認証の後・認可の前</b>で、(1) 認証の失敗を数えて監査へ残し、(2) 通ったトークンの枠を数え、
/// (3) 認可が 403 で断ったことを監査へ残す（Issue #1027）。
/// </summary>
/// <remarks>
/// <para>
/// 失敗の応答は区別しない（同じ <c>401</c>）。理由は監査にだけ残す。
/// 枠を超えた要求は、<b>DB へ書く前</b>（口の処理の前）に <c>429</c> と <c>Retry-After</c> で止める。
/// </para>
/// <para>
/// 403 の記録は、認可の応答が <c>403</c> で、トークンの principal のときだけ。
/// 口そのものが返す 403 も同じ印で残る（トークンの識別つき。口の処理が書く操作の記録とは別の行）。
/// </para>
/// </remarks>
public sealed class AdminApiTokenLimitMiddleware(
    RequestDelegate next,
    AdminApiTokenRateGate gate,
    AdminApiTokenAuditor auditor,
    AdminApiTokenOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!AdminApiTokenAuthentication.UsesBearer(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!AdminApiTokenAuthentication.IsApiToken(context.User))
        {
            var validation = AdminApiTokenAuthentication.ValidationOf(context);
            var reason = validation?.Rejection?.ToString() ?? "MalformedHeader";

            await gate
                .RecordFailureAsync(AdminApiTokenGuardHelper.IpKeyOf(context), options.FailuresPer5Min, context.RequestAborted)
                .ConfigureAwait(false);
            await auditor.WriteAuthFailureAsync(context, reason).ConfigureAwait(false);

            await next(context).ConfigureAwait(false);
            return;
        }

        var write = !(HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method));
        var bucket = write ? "write" : "read";
        var tokenIdText = context.User.FindFirstValue(AdminApiTokenAuthentication.TokenIdClaim);
        if (Guid.TryParse(tokenIdText, out var tokenId))
        {
            var decision = await gate
                .AcquireTokenAsync(
                    tokenId,
                    write,
                    write ? options.WritesPerMin : options.ReadsPerMin,
                    context.RequestAborted)
                .ConfigureAwait(false);
            if (!decision.Allowed)
            {
                if (gate.TryMarkReported($"{tokenId:N}:{bucket}", AdminApiTokenRateGate.TokenWindow))
                {
                    await auditor.WriteRateLimitedAsync(context, bucket).ConfigureAwait(false);
                }

                AdminApiTokenGuardHelper.Reject429(context, decision.RetryAfter);
                return;
            }
        }

        await next(context).ConfigureAwait(false);

        if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
        {
            await auditor.WriteForbiddenAsync(context).ConfigureAwait(false);
        }
    }
}

internal static class AdminApiTokenGuardHelper
{
    /// <summary>数え先の IP。<b>IPv4 を IPv6 の形で数えない</b>（同じ相手を 2 つに分けない）。</summary>
    internal static string IpKeyOf(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    internal static void Reject429(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
    }
}
