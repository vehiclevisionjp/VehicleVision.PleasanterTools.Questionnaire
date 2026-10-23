using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>将来の役割で権限を分離しても、他人の TOTP 解除を閲覧権限だけで実行させない。</summary>
public sealed class AdminUserPermissionSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 他人の二要素解除は閲覧権限だけでは拒み専用権限を持つ人だけ通す(bool canReset)
    {
        await using var host = await AdminContractHost.StartAsync(services =>
        {
            // 将来の users.read だけの役割を模す。reset の本番ポリシーは置き換えない。
            services.AddAuthorization(options => options.AddPolicy(
                AdminPermissions.PolicyOf(AdminPermissions.UsersRead), policy =>
                    policy.AddAuthenticationSchemes(AdminAuthSchemes.Session)
                        .RequireAuthenticatedUser()
                        .RequireAssertion(context => context.User.IsInRole("Editor")
                            || AdminPermissions.Has(context.User.FindFirstValue(ClaimTypes.Role),
                                AdminPermissions.UsersRead))));
        });
        var target = await host.SeedAdminAsync(true);
        var actor = await host.SeedAdminAsync(false);
        var store = host.Services.GetRequiredService<IAdminUserStore>();
        var actorUser = (await store.FindForLoginAsync(actor.LoginId))!.Value.User;
        Assert.Equal(AdminChangeResult.Updated, await store.SetRoleAsync(actorUser.Id,
            canReset ? "UserAdministrator" : "Editor"));
        var targetUser = (await store.FindForLoginAsync(target.LoginId))!.Value.User;
        var secret = await store.GetTotpSecretAsync(targetUser.Id);
        using var client = host.CreateClient();
        using var login = await host.LoginAsync(client, actor);
        login.EnsureSuccessStatusCode();
        using var read = await client.GetAsync("/api/admin/users");
        read.EnsureSuccessStatusCode();
        using var reset = await client.PostAsJsonAsync($"/api/admin/users/{targetUser.Id}/totp/reset", new { });
        Assert.Equal(canReset ? HttpStatusCode.OK : HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal(!canReset, (await store.FindByIdAsync(targetUser.Id))!.TotpEnabled);
        Assert.Equal(canReset ? null : secret, await store.GetTotpSecretAsync(targetUser.Id));
        if (!canReset)
        {
            using var otherDenied = await client.GetAsync("/api/admin/audit-logs");
            Assert.Equal(otherDenied.StatusCode, reset.StatusCode);
            Assert.Equal(await otherDenied.Content.ReadAsStringAsync(), await reset.Content.ReadAsStringAsync());
        }
    }
}
