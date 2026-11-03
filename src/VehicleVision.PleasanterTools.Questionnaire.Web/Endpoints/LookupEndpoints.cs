using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>検索の要求本文（仕様書 4.1 節）。未知のプロパティは拒否する。</summary>
public sealed record LookupSearchBodyRequest(
    int? Version,
    Dictionary<string, string?>? Keys,
    string? LookupSession);

/// <summary>検索用トークンの応答。</summary>
public sealed record LookupSessionResponse(string LookupSession, int ExpiresInSeconds);

/// <summary>エラーの応答本文。<c>code</c> と安全な <c>fieldIds</c> だけ。</summary>
public sealed record LookupErrorResponse(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? FieldIds = null);

/// <summary>ルックアップの公開 API（Issue #832・仕様書 4 章）。**認証は無い。**</summary>
/// <remarks>
/// <para>
/// **キー値・取得値・API キー・トークンをログ・例外・応答の本文へ出さない。**
/// ログへ出すのは状態・分類・所要時間・相関 ID・定義の ID だけ。値のハッシュも出さない（仕様書 6.2 節）。
/// 例外は型名だけを記録する。
/// </para>
/// <para>
/// 順序: 枠（IP・フォーム）→ 本文の上限と構文 → トークン → 回答者の枠 → 公開版の解決・受付状態 → 版の一致
/// → 同時実行数 → 検索。**外部検索の前に、枠と構文の不備をすべて断る。**
/// 認証の Cookie を使わないので CSRF の対象にならない（トークンは本文で渡す）。
/// </para>
/// </remarks>
public static class LookupEndpoints
{
    private const string InvalidKeys = "LookupInvalidKeys";
    private const string SessionInvalid = "LookupSessionInvalid";
    private const string Unavailable = "LookupUnavailable";
    private const string VersionChanged = "LookupVersionChanged";
    private const string Closed = "LookupClosed";
    private const string TooManyResults = "LookupTooManyResults";
    private const string RateLimited = "LookupRateLimited";
    private const string SourceInvalid = "LookupSourceInvalid";
    private const string TemporarilyUnavailable = "LookupTemporarilyUnavailable";
    private const string Timeout = "LookupTimeout";
    private const string TooLarge = "LookupRequestTooLarge";

    /// <summary>経路の識別子として受け付ける長さの上限。枠の数え先を無制限に増やさせない。</summary>
    private const int MaxRouteIdLength = 128;

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static IEndpointRouteBuilder MapLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var forms = app.MapGroup("/api/forms")
            .WithTags("回答画面（ルックアップ）");

        // **検索用トークンを出す。** DB を見ない。存在しない公開 ID でも同じように出し、使っても 404 になる
        forms.MapPost("/{publicId}/lookups/session", async (
            string publicId,
            HttpContext context,
            LookupSessionTokens sessions,
            ILookupRateGate rate,
            LookupPublicOptions options,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (publicId.Length > MaxRouteIdLength)
            {
                return Error(StatusCodes.Status404NotFound, Unavailable);
            }

            var limited = await CheckSharedLimitsAsync(
                context, rate, options, publicId, "session-", cancellationToken).ConfigureAwait(false);
            if (limited is not null)
            {
                return limited;
            }

            return Results.Json(new LookupSessionResponse(
                sessions.Issue(publicId), (int)sessions.Lifetime.TotalSeconds));
        });

        forms.MapPost("/{publicId}/lookups/{lookupId}/search", async (
            string publicId,
            string lookupId,
            HttpContext context,
            ResponseIntake intake,
            ILookupSearchService search,
            ILookupFailureNotifier failureNotifier,
            LookupSessionTokens sessions,
            ILookupRateGate rate,
            LookupConcurrencyGate concurrency,
            LookupPublicOptions options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Questionnaire.Lookup");
            context.Response.Headers.CacheControl = "no-store";
            if (publicId.Length > MaxRouteIdLength || lookupId.Length > MaxRouteIdLength)
            {
                return Error(StatusCodes.Status404NotFound, Unavailable);
            }

            // 1. IP・フォームの枠。本文を読む前に、参照元も DB も呼ばずに断る
            var limited = await CheckSharedLimitsAsync(
                context, rate, options, publicId, string.Empty, cancellationToken).ConfigureAwait(false);
            if (limited is not null)
            {
                return limited;
            }

            // 2. 本文の上限と構文。上限超過は候補数の超過（422）と区別する
            var (body, bodyProblem) = await ReadBodyAsync(context, options, cancellationToken)
                .ConfigureAwait(false);
            if (bodyProblem is not null)
            {
                return bodyProblem;
            }

            // 3. トークンと回答者の枠。偽造したトークンでは枠の数え先を作らせない
            var session = sessions.Verify(body!.LookupSession, publicId);
            if (!session.IsValid)
            {
                return Error(StatusCodes.Status403Forbidden, SessionInvalid);
            }

            var respondent = await rate.AcquireAsync(
                "respondent",
                session.SessionId!,
                options.RespondentLimit,
                options.RespondentWindow,
                cancellationToken).ConfigureAwait(false);
            if (ToRateProblem(context, respondent) is { } respondentProblem)
            {
                return respondentProblem;
            }

            // 4. 公開版の定義を解決する。**下書きは引かせない**（公開版のスナップショットだけ）
            var (form, rejection) = await intake
                .GetPublishedAsync(publicId, cancellationToken, IsFramed(context))
                .ConfigureAwait(false);
            if (form is null)
            {
                return rejection switch
                {
                    IntakeRejection.Maintenance => RetryLater(context, 300),
                    IntakeRejection.NotStarted or IntakeRejection.Closed or IntakeRejection.Suspended =>
                        Error(StatusCodes.Status409Conflict, Closed),
                    _ => Error(StatusCodes.Status404NotFound, Unavailable),
                };
            }

            // 表示中の版と違えば、定義が変わっているので自動で再適用させない
            if (body.Version != form.SurveyVersion)
            {
                return Error(StatusCodes.Status409Conflict, VersionChanged);
            }

            var lookup = form.Definition.GetLookups()
                .FirstOrDefault(item => string.Equals(item.LookupId, lookupId, StringComparison.Ordinal));
            if (lookup is null)
            {
                return Error(StatusCodes.Status404NotFound, Unavailable);
            }

            // 5. 同時実行数。満了なら参照元を呼ばない
            using var lease = concurrency.TryEnter(form.SurveyId, options.ConcurrentSearchesPerForm);
            if (lease is null)
            {
                return RetryLater(context, 1);
            }

            var started = Stopwatch.GetTimestamp();
            LookupSearchResult result;
            try
            {
                result = await search
                    .SearchAsync(form.SurveyId, lookup, body.Keys!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // **例外の中身（メッセージ・スタック）を出さない。** 型名だけ
                logger.LogError(
                    "ルックアップの検索で想定外の例外（種類={ExceptionType} 検索={LookupId} 相関={TraceId}）",
                    exception.GetType().Name,
                    lookup.LookupId,
                    context.TraceIdentifier);
                await failureNotifier.NotifyAsync(
                    form.SurveyId, lookup.LookupId, LookupSearchFailure.None, context.TraceIdentifier)
                    .ConfigureAwait(false);
                return Error(StatusCodes.Status502BadGateway, SourceInvalid);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            LogOutcome(logger, context, form.SurveyId, lookup.LookupId, result, elapsed);
            if (result.Status == LookupSearchStatus.SourceInvalid)
            {
                // **502 のときだけ管理者へ知らせる。** 失敗しても応答は変えない（例外を投げない実装）
                await failureNotifier.NotifyAsync(
                    form.SurveyId, lookup.LookupId, result.Failure, context.TraceIdentifier)
                    .ConfigureAwait(false);
            }

            return ToResult(context, form, lookup, result);
        });

        return app;
    }

    private static async Task<IResult?> CheckSharedLimitsAsync(
        HttpContext context,
        ILookupRateGate rate,
        LookupPublicOptions options,
        string publicId,
        string scopePrefix,
        CancellationToken cancellationToken)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ip = await rate.AcquireAsync(
            scopePrefix + "ip", address, options.IpLimit, options.IpWindow, cancellationToken)
            .ConfigureAwait(false);
        if (ToRateProblem(context, ip) is { } ipProblem)
        {
            return ipProblem;
        }

        var form = await rate.AcquireAsync(
            scopePrefix + "form", publicId, options.FormLimit, options.FormWindow, cancellationToken)
            .ConfigureAwait(false);
        return ToRateProblem(context, form);
    }

    private static IResult? ToRateProblem(HttpContext context, LookupRateDecision decision)
    {
        switch (decision.Outcome)
        {
            case LookupRateOutcome.Allowed:
                return null;
            case LookupRateOutcome.Limited:
                context.Response.Headers.RetryAfter = Seconds(decision.RetryAfter);
                return Error(StatusCodes.Status429TooManyRequests, RateLimited);
            default:
                context.Response.Headers.RetryAfter = Seconds(decision.RetryAfter);
                return Error(StatusCodes.Status503ServiceUnavailable, TemporarilyUnavailable);
        }
    }

    private static string Seconds(TimeSpan value) =>
        Math.Max(1, (int)Math.Ceiling(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static IResult RetryLater(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status503ServiceUnavailable, TemporarilyUnavailable);
    }

    /// <summary>本文を上限まで読んで構文を確かめる。</summary>
    private static async Task<(LookupSearchBodyRequest? Body, IResult? Problem)> ReadBodyAsync(
        HttpContext context,
        LookupPublicOptions options,
        CancellationToken cancellationToken)
    {
        var max = options.RequestBodyMaxBytes;
        if (context.Request.ContentLength > max)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, TooLarge));
        }

        if (!context.Request.HasJsonContentType())
        {
            return (null, Error(StatusCodes.Status400BadRequest, InvalidKeys));
        }

        // **宣言の長さを信じず、上限 +1 バイトまでしか読まない**
        var buffer = new byte[max + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await context.Request.Body
                .ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > max)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, TooLarge));
        }

        LookupSearchBodyRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<LookupSearchBodyRequest>(buffer.AsSpan(0, length), BodyOptions);
        }
        catch (JsonException)
        {
            // **構文の誤りの詳細（位置・値）は返さない・記録しない**
            return (null, Error(StatusCodes.Status400BadRequest, InvalidKeys));
        }

        if (body is null || body.Version is null || body.Keys is null
            || body.Keys.Count == 0 || body.Keys.Count > LookupLimits.MaxKeys)
        {
            return (null, Error(StatusCodes.Status400BadRequest, InvalidKeys));
        }

        return (body, null);
    }

    private static IResult ToResult(
        HttpContext context,
        PublishedForm form,
        LookupDefinition lookup,
        LookupSearchResult result)
    {
        switch (result.Status)
        {
            case LookupSearchStatus.Success:
                return Results.Json(
                    LookupResponseBuilder.Build(form.SurveyVersion, form.Definition, lookup, result));
            case LookupSearchStatus.InvalidKeys:
                // **定義にあるキー項目の ID だけ返す。** 回答者が送った未知の ID は反映しない
                var known = lookup.Keys.Select(key => key.QuestionId).ToHashSet(StringComparer.Ordinal);
                return Error(
                    StatusCodes.Status400BadRequest,
                    InvalidKeys,
                    [.. result.InvalidKeyQuestionIds.Where(known.Contains)]);
            case LookupSearchStatus.TooManyResults:
                return Error(StatusCodes.Status422UnprocessableEntity, TooManyResults);
            case LookupSearchStatus.TemporarilyUnavailable:
                return RetryLater(context, 5);
            case LookupSearchStatus.Timeout:
                return Error(StatusCodes.Status504GatewayTimeout, Timeout);
            default:
                return Error(StatusCodes.Status502BadGateway, SourceInvalid);
        }
    }

    private static void LogOutcome(
        ILogger logger,
        HttpContext context,
        Guid surveyId,
        string lookupId,
        LookupSearchResult result,
        TimeSpan elapsed)
    {
        // **記録するのは状態・分類・所要時間・相関 ID・定義の ID だけ。**
        // キー値・取得値・候補の数以外の中身・トークン・API キーは出さない
        var level = result.Status is LookupSearchStatus.SourceInvalid
            or LookupSearchStatus.TemporarilyUnavailable or LookupSearchStatus.Timeout
            ? LogLevel.Warning
            : LogLevel.Information;
        logger.Log(
            level,
            "ルックアップの検索: 状態={Status} 分類={Failure} 所要={ElapsedMs}ms フォーム={SurveyId} 検索={LookupId} 相関={TraceId}",
            result.Status,
            result.Failure,
            (long)elapsed.TotalMilliseconds,
            surveyId,
            lookupId,
            context.TraceIdentifier);
    }

    private static IResult Error(int status, string code, IReadOnlyList<string>? fieldIds = null) =>
        Results.Json(
            new LookupErrorResponse(code, fieldIds is { Count: > 0 } ? fieldIds : null),
            statusCode: status);

    private static bool IsFramed(HttpContext context) =>
        context.Request.Headers[FormEndpoints.FramedHeader]
            .Any(value => string.Equals(value, "1", StringComparison.Ordinal));
}
