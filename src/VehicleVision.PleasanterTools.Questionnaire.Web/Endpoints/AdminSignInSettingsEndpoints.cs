using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>管理画面のログイン方式の設定（ID/パスワードを隠すか）の入口（Issue #1056。親 Overview#60）。</summary>
/// <remarks>
/// <para>
/// <b>権限は SAML 設定と同じ（<see cref="AdminPermissions.SamlSettings"/>、Administrator のみ）。</b>
/// ログイン方式を変える操作は、SAML 設定を変える人と同じ人だけに許す。新しい権限は作らない。
/// </para>
/// <para>
/// <b>外部設定（<c>QUESTIONNAIRE_ADMIN_PASSWORD_SIGNIN</c>）で固定されているときは保存を断る</b>（409）。
/// 「隠す」へ変えるときは、SSO が有効で救済トークンが設定済みであることを確かめる（400）。
/// 締め出された後の戻り方は従来どおり救済トークン（<c>?rescue=</c>）のまま。
/// </para>
/// </remarks>
public static class AdminSignInSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSignInSettingsEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var group = builder.MapGroup("/api/admin/sign-in-settings")
            .WithTags("管理 API");
        AdminAuthSchemes.AddNoStore(group);

        // **変更を操作の記録に残す**（Issue #19）。読み取りは残らない
        group.AddEndpointFilter<AuditLogFilter>();

        var policy = AdminPermissions.PolicyOf(AdminPermissions.SamlSettings);

        group.MapGet("", async (
            IAdminSignInSettingsProvider provider,
            ISamlOptionsProvider samlProvider,
            IPleasanterSsoOptionsProvider pleasanterSsoProvider,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            var ssoEnabled = await IsSsoEnabledAsync(samlProvider, pleasanterSsoProvider, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(Body(snapshot, ssoEnabled));
        }).RequireAuthorization(policy);

        group.MapPut("", async (
            SignInSettingsRequest request,
            HttpContext context,
            IAdminSignInSettingsProvider provider,
            ISamlOptionsProvider samlProvider,
            IPleasanterSsoOptionsProvider pleasanterSsoProvider,
            CancellationToken cancellationToken) =>
        {
            AuditNotes.SetTarget(context, "AdminSignInSettings", targetId: null);

            var before = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            var ssoEnabled = await IsSsoEnabledAsync(samlProvider, pleasanterSsoProvider, cancellationToken)
                .ConfigureAwait(false);

            var outcome = await provider
                .SaveAsync(request.PasswordSignInEnabled, ssoEnabled, cancellationToken)
                .ConfigureAwait(false);
            if (outcome != AdminSignInSaveOutcome.Saved)
            {
                AuditNotes.Add(context, "result", "rejected");
                AuditNotes.Add(context, "reason", outcome.ToString());
                return outcome == AdminSignInSaveOutcome.Fixed
                    ? Results.Conflict(new { code = "fixed" })
                    : Results.BadRequest(new
                    {
                        code = outcome == AdminSignInSaveOutcome.SsoDisabled
                            ? "sso-disabled"
                            : "rescue-token-missing",
                    });
            }

            // **何を変えたかだけを残す。** 値そのものは「隠した／出した」の 2 通りで秘密ではないが、
            // 他の設定画面と同じく項目名で残す
            if (before.PasswordSignInEnabled != request.PasswordSignInEnabled)
            {
                AuditNotes.Add(context, "changedFields", "passwordSignIn");
            }

            var after = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(Body(after, ssoEnabled));
        }).RequireAuthorization(policy);

        return builder;
    }

    private static async Task<bool> IsSsoEnabledAsync(
        ISamlOptionsProvider samlProvider,
        IPleasanterSsoOptionsProvider pleasanterSsoProvider,
        CancellationToken cancellationToken)
    {
        var saml = (await samlProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
        var pleasanterSso = (await pleasanterSsoProvider.GetAsync(cancellationToken).ConfigureAwait(false))
            .Options;
        return saml.Enabled || pleasanterSso.Enabled;
    }

    private static object Body(AdminSignInSettingsSnapshot snapshot, bool ssoEnabled) => new
    {
        passwordSignInEnabled = snapshot.PasswordSignInEnabled,
        // **画面が「設定で固定されています」を出して編集を止めるのに使う**（SAML の fixedFields と同じ考え方）
        fixedByConfiguration = snapshot.Fixed,
        // **画面が「隠す」を選べるかの判断に使う。** 保存時にもサーバで検査する
        ssoEnabled,
        rescueTokenConfigured = snapshot.RescueTokenConfigured,
    };

    public sealed record SignInSettingsRequest(bool PasswordSignInEnabled);
}
