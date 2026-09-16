using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>機械監視へ匿名な稼働状況を返す入口。</summary>
public static class MonitoringEndpoints
{
    /// <summary>監視の入口を追加する。</summary>
    public static IEndpointRouteBuilder MapMonitoringEndpoints(
        this IEndpointRouteBuilder builder,
        MonitoringToken token)
    {
        builder.MapGet("/api/monitoring/status", async (
            HttpContext context,
            MonitoringService monitoring,
            CancellationToken cancellationToken) =>
        {
            if (!token.IsAuthorized(context.Request))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Unauthorized();
            }

            var status = await monitoring.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return status.DatabaseConnected
                ? Results.Ok(status)
                : Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
            .WithTags("監視 API");

        return builder;
    }
}

/// <summary>監視 API の Bearer token を定数時間で検査する。</summary>
public sealed class MonitoringToken
{
    /// <summary>監視 API を有効にする設定。</summary>
    public const string Setting = "QUESTIONNAIRE_MONITORING_TOKEN";

    private readonly byte[] expectedHash;

    public MonitoringToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>要求が正しい Bearer token を持つか。</summary>
    public bool IsAuthorized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suppliedToken = string.Empty;
        if (System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(
                request.Headers.Authorization,
                out var authorization)
            && string.Equals(
                authorization.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase)
            && authorization.Parameter is { Length: > 0 } parameter)
        {
            suppliedToken = parameter;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}

/// <summary>既存の送信状況と公開件数を監視用の形へまとめる。</summary>
public sealed class MonitoringService(
    DatabaseProvider provider,
    string connectionString,
    IResponseOutbox responseOutbox,
    IMonitoringStore monitoringStore,
    IMailOutbox mailOutbox,
    MailOptions mailOptions,
    TimeProvider timeProvider)
{
    /// <summary>匿名な稼働状況を読む。</summary>
    public async Task<MonitoringStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var failure = await DatabaseMigrator.WaitForDatabaseAsync(
            provider,
            connectionString,
            TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            return MonitoringStatusResponse.DatabaseUnavailable;
        }

        var responseStatusTask = responseOutbox.GetStatusAsync(cancellationToken);
        var publishedSurveyCountTask =
            monitoringStore.CountPublishedSurveysAsync(cancellationToken);
        var mailStatusTask = mailOptions.Enabled
            ? mailOutbox.GetStatusAsync(cancellationToken)
            : null;

        await Task.WhenAll(
            mailStatusTask is null
                ? [responseStatusTask, publishedSurveyCountTask]
                : [responseStatusTask, publishedSurveyCountTask, mailStatusTask])
            .ConfigureAwait(false);

        var responseStatus = await responseStatusTask.ConfigureAwait(false);
        var mailStatus = mailStatusTask is null
            ? null
            : await mailStatusTask.ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        return new MonitoringStatusResponse(
            DatabaseConnected: true,
            PendingCount: responseStatus.PendingCount,
            OldestPendingAgeSeconds: AgeSeconds(now, responseStatus.OldestPendingAt),
            DeadLetterCount: responseStatus.DeadLetterCount,
            OldestDeadLetterAgeSeconds: AgeSeconds(now, responseStatus.OldestDeadLetterAt),
            PublishedSurveyCount: await publishedSurveyCountTask.ConfigureAwait(false),
            MailPendingCount: mailStatus?.PendingCount);
    }

    /// <summary>DB の UTC 時刻を、閾値と比較しやすい経過秒へ変える。</summary>
    public static long? AgeSeconds(DateTimeOffset now, DateTime? occurredAt)
    {
        var utc = DbTime.AsUtc(occurredAt);
        return utc is null
            ? null
            : Math.Max(0, (long)(now - new DateTimeOffset(utc.Value)).TotalSeconds);
    }
}

/// <summary>監視へ返す匿名な集計値。</summary>
/// <remarks>
/// 回答、回答者、アンケートを識別できる値の入る場所を型として持たない。
/// </remarks>
public sealed record MonitoringStatusResponse(
    bool DatabaseConnected,
    int? PendingCount,
    long? OldestPendingAgeSeconds,
    int? DeadLetterCount,
    long? OldestDeadLetterAgeSeconds,
    int? PublishedSurveyCount,
    int? MailPendingCount)
{
    public static readonly MonitoringStatusResponse DatabaseUnavailable =
        new(false, null, null, null, null, null, null);
}
