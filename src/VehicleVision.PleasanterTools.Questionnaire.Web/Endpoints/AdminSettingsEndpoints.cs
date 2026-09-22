using System.Collections.Frozen;
using System.Net.Mail;
using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>特権管理者がアプリケーション設定を読み書きする入口。</summary>
public static class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSettingsEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/settings")
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SettingsManage));
        group.AddEndpointFilter<AuditLogFilter>();

        group.MapGet("", async (
            IAppSettingsProvider provider,
            IMonitoringStore monitoring,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            var published = await monitoring.CountPublishedSurveysAsync(cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(Body(snapshot, published));
        });

        group.MapPut("", async (
            AppSettingsRequest request,
            HttpContext context,
            IAppSettingsProvider provider,
            IMonitoringStore monitoring,
            CancellationToken cancellationToken) =>
        {
            if (request.Values is null)
            {
                return Results.BadRequest(new { message = "設定値を指定してください。" });
            }

            try
            {
                var before = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
                var definitions = before.Definitions.ToDictionary(
                    definition => definition.Key,
                    StringComparer.Ordinal);
                ValidateAltchaRange(request.Values, before, definitions);
                var changedKeys = request.Values
                    .Where(value => definitions.TryGetValue(value.Key, out var definition)
                        && !before.FixedKeys.Contains(value.Key)
                        && !(definition.IsSecret && string.IsNullOrWhiteSpace(value.Value))
                        && !string.Equals(
                            before[value.Key],
                            definition.Normalize(value.Value),
                            StringComparison.Ordinal))
                    .Select(value => value.Key)
                    .ToArray();
                if (changedKeys.Length > 0)
                {
                    AuditNotes.SetTarget(context, "AppSetting", string.Join(",", changedKeys));
                    AuditNotes.Add(context, "changedKeys", string.Join(",", changedKeys));
                    AuditNotes.Add(
                        context,
                        "changedValues",
                        string.Join(
                            ";",
                            changedKeys
                                .Where(key => !definitions[key].IsSecret)
                                .Select(key =>
                                $"{key}:{before[key]}->{definitions[key].Normalize(request.Values[key])}")));
                }

                var adminUserId = Guid.Parse(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                var snapshot = await provider.SaveAsync(
                    request.Values,
                    adminUserId,
                    cancellationToken).ConfigureAwait(false);
                var published = await monitoring.CountPublishedSurveysAsync(cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(Body(snapshot, published));
            }
            catch (AppSettingValidationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        group.MapPost("/test", async (
            AppSettingsRequest request,
            IAppSettingsProvider provider,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (request.Values is null)
            {
                return Results.BadRequest(new { message = "設定値を指定してください。" });
            }

            try
            {
                var current = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
                var candidate = Candidate(current, request.Values);
                var options = PleasanterOptionsProvider.FromSnapshot(candidate, configuration);
                if (options.BaseUrl.Length == 0 || options.ApiKey.Length == 0)
                {
                    return Results.BadRequest(new
                    {
                        message = "Pleasanter の URL と API キーを指定してください。",
                    });
                }

                var client = new PleasanterApiClient(
                    httpClientFactory.CreateClient("pleasanter"),
                    options);
                var connected = await client.CheckConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                return connected
                    ? Results.Ok(new ConnectionTestResponse(true))
                    : Results.BadRequest(new
                    {
                        message = "Pleasanter へ接続できませんでした。URL、API キー、接続先の状態をご確認ください。",
                    });
            }
            catch (AppSettingValidationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        group.MapPost("/test-send", async (
            AppSettingsRequest request,
            ClaimsPrincipal principal,
            HttpContext context,
            MailSettingsTestMailer mailer,
            CancellationToken cancellationToken) =>
        {
            if (request.Values is null)
            {
                return Results.BadRequest(new { message = "設定値を指定してください。" });
            }

            var recipient = principal.Identity?.Name;
            if (!MailAddress.TryCreate(recipient, out _))
            {
                return Results.BadRequest(new
                {
                    message = "ログイン ID がメールアドレスではないため、試験送信できません。",
                });
            }

            try
            {
                AuditNotes.SetTarget(context, "AppSetting", "mail-test");
                await mailer.SendAsync(request.Values, recipient, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(new { sent = true });
            }
            catch (AppSettingValidationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (MailDeliveryException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        })
            .RequireRateLimiting(AdminAutoReplyEndpoints.TestSendRateLimitPolicy);

        return builder;
    }

    private static void ValidateAltchaRange(
        IReadOnlyDictionary<string, string?> requestedValues,
        AppSettingsSnapshot before,
        IReadOnlyDictionary<string, AppSettingDefinition> definitions)
    {
        static int ValueOf(
            string key,
            IReadOnlyDictionary<string, string?> requested,
            AppSettingsSnapshot current,
            IReadOnlyDictionary<string, AppSettingDefinition> currentDefinitions) =>
            int.Parse(
                requested.TryGetValue(key, out var requestedValue)
                    ? currentDefinitions[key].Normalize(requestedValue)
                    : current[key],
                System.Globalization.CultureInfo.InvariantCulture);

        var minimum = ValueOf(
            BotMitigationOptionsProvider.AltchaMinimumNumberKey,
            requestedValues,
            before,
            definitions);
        var maximum = ValueOf(
            BotMitigationOptionsProvider.AltchaMaximumNumberKey,
            requestedValues,
            before,
            definitions);
        if (minimum > maximum)
        {
            throw new AppSettingValidationException(
                "proof-of-work の探索下限は探索上限以下で指定してください。");
        }
    }

    /// <summary>設定を管理画面へ返す形へ変換する。秘密値の本文は絶対に含めない。</summary>
    public static AppSettingsResponse Body(
        AppSettingsSnapshot snapshot,
        int publishedSurveyCount = 0) => new(
        snapshot.Definitions.Select(definition => new AppSettingFieldResponse(
            definition.Key,
            definition.Type.ToString().ToLowerInvariant(),
            definition.IsSecret ? null : snapshot[definition.Key],
            definition.IsSecret && snapshot[definition.Key].Length > 0,
            definition.IsSecret,
            snapshot.FixedKeys.Contains(definition.Key),
            definition.LabelJa,
            definition.LabelEn,
            definition.DescriptionJa,
            definition.DescriptionEn,
            definition.Minimum,
            definition.Maximum,
            definition.MaximumLength,
            definition.ShowPreview)).ToArray(),
        publishedSurveyCount,
        PleasanterConfigurationReport.MissingKeys(snapshot).Count == 0);

    private static AppSettingsSnapshot Candidate(
        AppSettingsSnapshot current,
        IReadOnlyDictionary<string, string?> requested)
    {
        var definitions = current.Definitions.ToDictionary(
            definition => definition.Key,
            StringComparer.Ordinal);
        var values = current.Values.ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var (key, requestedValue) in requested)
        {
            if (!definitions.TryGetValue(key, out var definition))
            {
                throw new AppSettingValidationException($"管理対象ではない設定です: {key}");
            }

            if (current.FixedKeys.Contains(key))
            {
                if (!string.Equals(
                    current[key],
                    definition.Normalize(requestedValue),
                    StringComparison.Ordinal))
                {
                    throw new AppSettingValidationException(
                        $"{definition.LabelJa}は外部設定で固定されているため、画面から変更できません。");
                }

                continue;
            }

            if (definition.IsSecret && string.IsNullOrWhiteSpace(requestedValue))
            {
                continue;
            }

            values[key] = definition.Normalize(requestedValue);
        }

        return new AppSettingsSnapshot(
            current.Definitions,
            values.ToFrozenDictionary(StringComparer.Ordinal),
            current.FixedKeys);
    }

    public sealed record AppSettingsRequest(IReadOnlyDictionary<string, string?>? Values);

    public sealed record AppSettingsResponse(
        IReadOnlyList<AppSettingFieldResponse> Fields,
        int PublishedSurveyCount,
        bool IsPleasanterConfigured);

    public sealed record ConnectionTestResponse(bool Connected);

    public sealed record AppSettingFieldResponse(
        string Key,
        string Type,
        string? Value,
        bool HasValue,
        bool IsSecret,
        bool IsFixed,
        string LabelJa,
        string LabelEn,
        string DescriptionJa,
        string DescriptionEn,
        int? Minimum,
        int? Maximum,
        int? MaximumLength,
        bool ShowPreview);
}
