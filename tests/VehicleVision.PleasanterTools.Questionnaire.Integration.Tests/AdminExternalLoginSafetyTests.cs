using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using IPleasanterSessionVerifier = VehicleVision.PleasanterTools.Core.Web.IPleasanterSessionVerifier;
using IPleasanterSsoVerificationOptions = VehicleVision.PleasanterTools.Core.Web.IPleasanterSsoVerificationOptions;
using PleasanterSessionResult = VehicleVision.PleasanterTools.Core.Web.PleasanterSessionResult;
using PleasanterSessionStatus = VehicleVision.PleasanterTools.Core.Web.PleasanterSessionStatus;
using PleasanterIdentity = VehicleVision.PleasanterTools.Core.Web.PleasanterIdentity;
using PleasanterSsoUnknownUserPolicy = VehicleVision.PleasanterTools.Core.Web.PleasanterSsoUnknownUserPolicy;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>実機に接続せず、HTTP パイプラインと SQLite で U8 の拒否を検査する。</summary>
public sealed class AdminExternalLoginSafetyTests
{
    [Theory]
    [InlineData(TwoFactorPolicy.Optional, false)]
    [InlineData(TwoFactorPolicy.Optional, true)]
    [InlineData(TwoFactorPolicy.Required, false)]
    [InlineData(TwoFactorPolicy.Required, true)]
    [InlineData(TwoFactorPolicy.Disabled, false)]
    [InlineData(TwoFactorPolicy.Disabled, true)]
    public async Task Pleasanterの締め出しは二要素の方針によらず拒み満了後は通す(
        TwoFactorPolicy policy, bool withTotp)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var verifier = new LocalVerifier();
        await using var host = await StartAsync(verifier, time, policy);
        var admin = await host.SeedAdminAsync(withTotp);
        verifier.LoginId = admin.LoginId;
        var store = host.Services.GetRequiredService<IAdminUserStore>();
        var user = (await store.FindForLoginAsync(admin.LoginId))!.Value.User;
        await store.RecordFailureAsync(user.Id, 1, TimeSpan.FromMinutes(15));
        var before = await store.FindByIdAsync(user.Id);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var refused = await client.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
            Assert.Equal(HttpStatusCode.Locked, refused.StatusCode);
            AssertNoAuthenticationCookie(refused);
            using var password = await host.LoginAsync(client, admin);
            Assert.Equal(await password.Content.ReadAsStringAsync(), await refused.Content.ReadAsStringAsync());
            Assert.Equal(before, await store.FindByIdAsync(user.Id));
            using var probe = await client.GetAsync("/api/admin/users");
            Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        }
        var audits = await host.Services.GetRequiredService<IAuditLogStore>().ListAsync(
            new AuditLogQuery { Action = "POST /api/admin/pleasanter-sso/check" });
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.False(audit.Succeeded);
            Assert.Equal(423, audit.StatusCode);
            Assert.Contains("locked-out", audit.DetailJson);
            Assert.DoesNotContain(admin.Password, audit.DetailJson);
        });
        time.Advance(TimeSpan.FromMinutes(15));
        using var accepted = await client.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
        accepted.EnsureSuccessStatusCode();
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(withTotp ? "totp" : policy == TwoFactorPolicy.Required ? "enroll" : "done",
            body.GetProperty("next").GetString());
    }

    [Fact]
    public async Task Pleasanterの未登録と無効はパスワード失敗と同じ本文にする()
    {
        var verifier = new LocalVerifier();
        await using var host = await StartAsync(verifier);
        var enabled = await host.SeedAdminAsync(false);
        var disabled = await host.SeedAdminAsync(false, disabled: true);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var password = await client.PostAsJsonAsync("/api/admin/login",
            new { loginId = enabled.LoginId, password = "wrong-password" });
        foreach (var loginId in new[] { "unknown@example.test", disabled.LoginId })
        {
            verifier.LoginId = loginId;
            using var response = await client.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(await password.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
            AssertNoAuthenticationCookie(response);
        }
    }

    [Theory]
    [InlineData(AdminRole.Editor)]
    [InlineData(AdminRole.Administrator)]
    public async Task PleasanterのJITは通常の役割だけ作り禁止された役割は監査に残す(AdminRole role)
    {
        var verifier = new LocalVerifier();
        await using var host = await StartAsync(verifier, registerRole: role, register: true);
        _ = await host.SeedAdminAsync(false);
        using var client = host.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
        var store = host.Services.GetRequiredService<IAdminUserStore>();
        if (role == AdminRole.Editor)
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal("Editor", (await store.FindForLoginAsync(verifier.LoginId))!.Value.User.Role);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            AssertNoAuthenticationCookie(response);
            Assert.Null(await store.FindForLoginAsync(verifier.LoginId));
            var audit = Assert.Single(await host.Services.GetRequiredService<IAuditLogStore>().ListAsync(
                new AuditLogQuery { Action = "POST /api/admin/pleasanter-sso/check" }));
            Assert.False(audit.Succeeded);
            Assert.Contains("register-role-not-allowed", audit.DetailJson);
        }
    }

    [Fact]
    public async Task 管理者がゼロなら外部ログインから最初の管理者を作らない()
    {
        var verifier = new LocalVerifier();
        await using var host = await StartAsync(verifier);
        using var client = host.CreateClient();
        using var sso = await client.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
        Assert.Equal(HttpStatusCode.Conflict, sso.StatusCode);
        Assert.Equal(0, verifier.Calls);
        using var saml = await client.PostAsync("/api/admin/saml/acs",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.Redirect, saml.StatusCode);
        Assert.EndsWith("?samlError=invalid", saml.Headers.Location!.ToString());
        AssertNoAuthenticationCookie(sso);
        AssertNoAuthenticationCookie(saml);
        Assert.Equal(0, await host.Services.GetRequiredService<IAdminUserStore>().CountAsync());
        var audits = await host.Services.GetRequiredService<IAuditLogStore>().ListAsync(new AuditLogQuery());
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.False(audit.Succeeded);
            Assert.Equal(409, audit.StatusCode);
            Assert.Contains("setup-required", audit.DetailJson);
        });
    }

    [Theory]
    [InlineData("saml")]
    [InlineData("pleasanter-sso")]
    public async Task 設定更新APIは無効時もAdministratorを保存しない(string method)
    {
        await using var host = await AdminContractHost.StartAsync();
        var admin = await host.SeedAdminAsync(false);
        using var client = host.CreateClient();
        using var login = await host.LoginAsync(client, admin);
        login.EnsureSuccessStatusCode();
        using var before = await client.GetAsync($"/api/admin/{method}/settings");
        before.EnsureSuccessStatusCode();
        using var response = await client.PutAsJsonAsync($"/api/admin/{method}/settings",
            new { enabled = false, registerRole = "Administrator" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("REGISTERROLE", await response.Content.ReadAsStringAsync());
        using var after = await client.GetAsync($"/api/admin/{method}/settings");
        Assert.Equal(await before.Content.ReadAsStringAsync(), await after.Content.ReadAsStringAsync());
    }

    private static Task<AdminContractHost> StartAsync(
        LocalVerifier verifier, TimeProvider? time = null, TwoFactorPolicy policy = TwoFactorPolicy.Optional,
        AdminRole registerRole = AdminRole.Editor, bool register = false) =>
        AdminContractHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IPleasanterSessionVerifier>(verifier));
            services.Replace(ServiceDescriptor.Singleton<IPleasanterSsoOptionsProvider>(
                new LocalSsoOptions(registerRole, register)));
            services.Replace(ServiceDescriptor.Singleton<ISamlOptionsProvider>(new LocalSamlOptions()));
            services.Replace(ServiceDescriptor.Singleton(new AdminAuthOptions { TwoFactor = policy }));
            if (time is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(time));
            }
        });

    private static void AssertNoAuthenticationCookie(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            Assert.DoesNotContain(cookies, value => value.StartsWith("q.admin=", StringComparison.Ordinal)
                || value.StartsWith("q.admin.pending=", StringComparison.Ordinal));
        }
    }

    private sealed class LocalVerifier : IPleasanterSessionVerifier
    {
        public string LoginId { get; set; } = "jit@example.test";
        public int Calls { get; private set; }
        public Task<PleasanterSessionResult> VerifyAsync(
            StringValues cookies, IPleasanterSsoVerificationOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PleasanterSessionResult(
                PleasanterSessionStatus.Authenticated, new PleasanterIdentity(1, 7, LoginId, null)));
        }
    }

    private sealed class LocalSsoOptions(AdminRole role, bool register) : IPleasanterSsoOptionsProvider
    {
        public Task<PleasanterSsoOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PleasanterSsoOptionsSnapshot(new PleasanterSsoOptions
            {
                Enabled = true,
                InternalBaseUrl = new Uri("https://pleasanter.invalid"),
                LoginUrl = "/users/login",
                UnknownUser = register ? PleasanterSsoUnknownUserPolicy.Register : PleasanterSsoUnknownUserPolicy.Reject,
                RegisterRole = role,
                AllowedDeptIds = [10],
            }, new PleasanterSsoSettingValues(), new HashSet<string>()));

        public Task<PleasanterSsoOptionsSnapshot> PreviewAsync(PleasanterSsoSettingValues values,
            bool forceEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PleasanterSsoOptionsSnapshot> SaveAsync(PleasanterSsoSettingValues values,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LocalSamlOptions : ISamlOptionsProvider
    {
        public Task<SamlOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SamlOptionsSnapshot(new SamlOptions { Enabled = true },
                new SamlSettingValues(), new HashSet<string>()));

        public Task<SamlOptionsSnapshot> SaveAsync(SamlSettingValues values,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
