using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 管理 API の個人トークンの<b>レート制限</b>と<b>操作の記録</b>を、実アプリの host（SQLite）へ HTTP で当てて確かめる
/// （Issue #1027。親 #1012）。
/// </summary>
/// <remarks>
/// 見るのは「失敗の枠を超えた IP の Bearer だけが 429（cookie は巻き込まない）」「トークン単位の枠（書き込みは別・
/// 低い）」「認証の失敗・403・枠の超過・書き込みが、トークンの識別つきで残り、値は残らない」。
/// 接続元 IP は TestServer では決められないので、枠の数え先は同じ 1 つになる。
/// </remarks>
public sealed class AdminApiTokenGuardEndToEndTests
{
    private const string Password = "contract-long-password-1";
    private const string Surveys = "/api/admin/surveys";

    private sealed record Account(Guid Id, string LoginId);

    private static IReadOnlyDictionary<string, string> Settings(
        int? failures = null,
        int? reads = null,
        int? writes = null)
    {
        var settings = new Dictionary<string, string> { [AdminApiTokenOptions.ModeSetting] = "enabled" };
        if (failures is { } f)
        {
            settings[AdminApiTokenOptions.FailuresPer5MinSetting] = f.ToString();
        }

        if (reads is { } r)
        {
            settings[AdminApiTokenOptions.ReadsPerMinSetting] = r.ToString();
        }

        if (writes is { } w)
        {
            settings[AdminApiTokenOptions.WritesPerMinSetting] = w.ToString();
        }

        return settings;
    }

    private static string Missing(string suffix = "") => $"{Surveys}/{Guid.NewGuid()}{suffix}";

    private static async Task<Account> SeedAsync(AdminContractHost host, AdminRole role)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var user = await host.Services.GetRequiredService<IAdminUserStore>().CreateAsync(
            loginId, host.Services.GetRequiredService<PasswordHasher>().Hash(Password), AdminRoleMap.ToName(role), "en");
        Assert.NotNull(user);
        return new Account(user.Id, loginId);
    }

    private static async Task<(string Token, Guid TokenId, string Prefix)> IssueAsync(
        AdminContractHost host,
        Account owner,
        params string[] scopes)
    {
        var service = host.Services.GetRequiredService<AdminApiTokenService>();
        var (outcome, issued) = await service.IssueAsync(
            owner.Id,
            new AdminApiTokenIssueRequest("ci", scopes, 30, []),
            new AdminApiTokenReauthentication.Password(Password));
        Assert.Equal(AdminApiTokenIssueOutcome.Issued, outcome);
        return (issued!.Token, issued.Record.TokenId, issued.Record.Prefix);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? token,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<IReadOnlyList<AuditEntry>> AuditAsync(AdminContractHost host, string? action = null) =>
        await host.Services.GetRequiredService<IAuditLogStore>()
            .ListAsync(new AuditLogQuery { Limit = 500, Action = action });

    private static Dictionary<string, string> DetailOf(AuditEntry entry) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailJson!)!;

    // ---- 失敗の枠 -----------------------------------------------------------------------------

    [Fact]
    public async Task 認証の失敗が枠を超えた送信元は_Bearerだけ429になり_cookieは影響を受けない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings(failures: 3));
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var (token, _, _) = await IssueAsync(host, owner, "surveys.read");
        using var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var failed = await SendAsync(client, HttpMethod.Get, Surveys, AdminApiTokenSecret.Create().Plain);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // **正しいトークンでも止まる**（枠は失敗の数。成功で戻さない）。DB を引く前に止める
        using var limited = await SendAsync(client, HttpMethod.Get, Surveys, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));

        // **cookie の要求は巻き込まない**
        using var cookie = host.CreateClient();
        using var login = await cookie.PostAsJsonAsync(
            "/api/admin/login", new { loginId = owner.LoginId, password = Password });
        login.EnsureSuccessStatusCode();
        using var list = await cookie.GetAsync(Surveys);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        // **止められた要求は監査へ書かない**（失敗の記録は枠の数まで。枠が書き込みの増幅にならない）
        Assert.Equal(3, (await AuditAsync(host, AdminApiTokenAuditActions.AuthFailed)).Count);
    }

    [Fact]
    public async Task 成功した要求は_失敗の枠を使わない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings(failures: 2, reads: 1000));
        var (token, _, _) = await IssueAsync(host, await SeedAsync(host, AdminRole.Administrator), "surveys.read");
        using var client = host.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            using var response = await SendAsync(client, HttpMethod.Get, Surveys, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ---- トークン単位の枠 ---------------------------------------------------------------------

    [Fact]
    public async Task トークンの書き込みの枠を超えると_DBへ書く前に429で_読み取りと別のトークンは通る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings(reads: 1000, writes: 2));
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var (token, tokenId, prefix) = await IssueAsync(host, owner, "surveys.read", "surveys.write");
        var (other, _, _) = await IssueAsync(host, owner, "surveys.write");
        using var client = host.CreateClient();

        // 通る間は、存在しないアンケートへの更新なので 404（口まで届いた印）
        for (var i = 0; i < 2; i++)
        {
            using var passed = await SendAsync(client, HttpMethod.Put, Missing(), token, new { });
            Assert.Equal(HttpStatusCode.NotFound, passed.StatusCode);
        }

        var before = (await AuditAsync(host)).Count;
        for (var i = 0; i < 3; i++)
        {
            using var limited = await SendAsync(client, HttpMethod.Put, Missing(), token, new { });
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        }

        using var read = await SendAsync(client, HttpMethod.Get, Surveys, token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var otherWrite = await SendAsync(client, HttpMethod.Put, Missing(), other, new { });
        Assert.Equal(HttpStatusCode.NotFound, otherWrite.StatusCode);

        // 超過は窓ごとに 1 回だけ残す（口の処理は走っていないので、書き込みの記録は増えない。other の 1 件を除く）
        var rateLimited = Assert.Single(await AuditAsync(host, AdminApiTokenAuditActions.RateLimited));
        Assert.Equal(429, rateLimited.StatusCode);
        Assert.False(rateLimited.Succeeded);
        Assert.Equal(tokenId.ToString(), rateLimited.TargetId);
        Assert.Equal(owner.Id.ToString("D"), rateLimited.ActorKey);
        var detail = DetailOf(rateLimited);
        Assert.Equal("write", detail["bucket"]);
        Assert.Equal(prefix, detail["tokenPrefix"]);
        Assert.Equal("api-token", detail["actorKind"]);
        Assert.Equal(before + 1 + 1, (await AuditAsync(host)).Count);
    }

    // ---- 監査: 認証の失敗 ---------------------------------------------------------------------

    [Fact]
    public async Task 認証の失敗は_理由と接頭辞つきで残り_トークンの値は残らない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings());
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var (revoked, revokedId, revokedPrefix) = await IssueAsync(host, owner, "surveys.read");
        var service = host.Services.GetRequiredService<AdminApiTokenService>();
        Assert.NotNull(await service.RevokeAsync(revokedId, owner.Id, owner.Id, "test"));
        var forged = AdminApiTokenSecret.Create();
        using var client = host.CreateClient();

        using var forgedResponse = await SendAsync(client, HttpMethod.Get, Surveys, forged.Plain);
        using var revokedResponse = await SendAsync(client, HttpMethod.Get, Surveys, revoked);
        using var garbage = await SendAsync(client, HttpMethod.Get, Surveys, "qat_zzzzzzzz_secret-looking-value");

        Assert.All(
            new[] { forgedResponse, revokedResponse, garbage },
            response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

        var failures = await AuditAsync(host, AdminApiTokenAuditActions.AuthFailed);
        Assert.Equal(3, failures.Count);
        Assert.All(failures, entry =>
        {
            Assert.False(entry.Succeeded);
            Assert.Equal(401, entry.StatusCode);
            Assert.Null(entry.ActorKey);
            // **値を残さない**（平文の残り・ハッシュ・ヘッダ）
            Assert.DoesNotContain(forged.Plain[forged.Prefix.Length..], entry.DetailJson, StringComparison.Ordinal);
            Assert.DoesNotContain(forged.Hash, entry.DetailJson, StringComparison.Ordinal);
            Assert.DoesNotContain(revoked[revokedPrefix.Length..], entry.DetailJson, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-looking-value", entry.DetailJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", entry.DetailJson, StringComparison.Ordinal);
        });

        // 失効したトークンは見つかるので、識別子と接頭辞つき
        var revokedEntry = Assert.Single(failures, entry => DetailOf(entry)["reason"] == nameof(AdminApiTokenRejection.Revoked));
        Assert.Equal(revokedId.ToString(), revokedEntry.TargetId);
        Assert.Equal(revokedPrefix, DetailOf(revokedEntry)["tokenPrefix"]);

        // 偽のトークンは見つからない（識別子は無い）が、形が合う接頭辞だけ残る
        var notFound = failures.Where(entry => DetailOf(entry)["reason"] == nameof(AdminApiTokenRejection.NotFound)).ToList();
        Assert.Equal(2, notFound.Count);
        Assert.All(notFound, entry => Assert.Null(entry.TargetId));
        Assert.Contains(notFound, entry => DetailOf(entry).GetValueOrDefault("tokenPrefix") == forged.Prefix);
    }

    [Fact]
    public async Task 形の合わない接頭辞は_残さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings());
        using var client = host.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Surveys, "qat_zzzzzzzz_aaaaaaaaaaaaaaaa");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(await AuditAsync(host, AdminApiTokenAuditActions.AuthFailed));
        Assert.DoesNotContain("tokenPrefix", DetailOf(entry).Keys);
        Assert.DoesNotContain("zzzzzzzz", entry.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 機能が無効のときの失敗も_理由つきで残る()
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Surveys, AdminApiTokenSecret.Create().Plain);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(await AuditAsync(host, AdminApiTokenAuditActions.AuthFailed));
        Assert.Equal(nameof(AdminApiTokenRejection.FeatureDisabled), DetailOf(entry)["reason"]);
    }

    // ---- 監査: 403・書き込み ------------------------------------------------------------------

    [Fact]
    public async Task スコープ不足の403は_トークンの識別つきで残る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings());
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var (token, tokenId, prefix) = await IssueAsync(host, owner, "surveys.read");
        using var client = host.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, Missing(), token, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Assert.Single(await AuditAsync(host, AdminApiTokenAuditActions.Forbidden));
        Assert.Equal(403, entry.StatusCode);
        Assert.Equal(owner.Id.ToString("D"), entry.ActorKey);
        Assert.Equal(owner.LoginId, entry.ActorLoginId);
        Assert.Equal(tokenId.ToString(), entry.TargetId);
        Assert.Equal(prefix, DetailOf(entry)["tokenPrefix"]);
        Assert.DoesNotContain(token[prefix.Length..], entry.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task トークンの書き込みは_主体の種別とトークンの識別つきで残り_人の操作はhumanで残る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings());
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var (token, tokenId, prefix) = await IssueAsync(host, owner, "surveys.write");
        using var client = host.CreateClient();

        using var byToken = await SendAsync(client, HttpMethod.Put, Missing(), token, new { });
        Assert.Equal(HttpStatusCode.NotFound, byToken.StatusCode);

        using var cookie = host.CreateClient();
        using var login = await cookie.PostAsJsonAsync(
            "/api/admin/login", new { loginId = owner.LoginId, password = Password });
        login.EnsureSuccessStatusCode();
        using var byHuman = await cookie.PutAsJsonAsync(Missing(), new { });
        Assert.Equal(HttpStatusCode.NotFound, byHuman.StatusCode);

        var writes = (await AuditAsync(host)).Where(entry => entry.Action.StartsWith("PUT ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, writes.Count);
        var tokenEntry = Assert.Single(writes, entry => DetailOf(entry)["actorKind"] == "api-token");
        var humanEntry = Assert.Single(writes, entry => DetailOf(entry)["actorKind"] == "human");
        Assert.Equal(tokenId.ToString(), DetailOf(tokenEntry)["tokenId"]);
        Assert.Equal(prefix, DetailOf(tokenEntry)["tokenPrefix"]);
        Assert.Equal(owner.Id.ToString("D"), tokenEntry.ActorKey);
        Assert.DoesNotContain("tokenId", DetailOf(humanEntry).Keys);
        Assert.DoesNotContain(token[prefix.Length..], tokenEntry.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通常の読み取りは_トークンでも残さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Settings());
        var (token, _, _) = await IssueAsync(host, await SeedAsync(host, AdminRole.Administrator), "surveys.read");
        using var client = host.CreateClient();

        using var list = await SendAsync(client, HttpMethod.Get, Surveys, token);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty(await AuditAsync(host));
    }
}
