using System.Security.Claims;
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
            CancellationToken cancellationToken) =>
        {
            var snapshot = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(Body(snapshot));
        });

        group.MapPut("", async (
            AppSettingsRequest request,
            HttpContext context,
            IAppSettingsProvider provider,
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
                var changedKeys = request.Values
                    .Where(value => definitions.TryGetValue(value.Key, out var definition)
                        && !before.FixedKeys.Contains(value.Key)
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
                }

                var adminUserId = Guid.Parse(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
                var snapshot = await provider.SaveAsync(
                    request.Values,
                    adminUserId,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(Body(snapshot));
            }
            catch (AppSettingValidationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        return builder;
    }

    /// <summary>設定を管理画面へ返す形へ変換する。秘密値の本文は絶対に含めない。</summary>
    public static AppSettingsResponse Body(AppSettingsSnapshot snapshot) => new(
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
            definition.ShowPreview)).ToArray());

    public sealed record AppSettingsRequest(IReadOnlyDictionary<string, string?>? Values);

    public sealed record AppSettingsResponse(IReadOnlyList<AppSettingFieldResponse> Fields);

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
