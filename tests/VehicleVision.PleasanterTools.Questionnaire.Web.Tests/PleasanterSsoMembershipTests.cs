using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>所属情報の取得経路と、不明・対象外の利用者を通さない境界を確かめる（Issue #505）。</summary>
public class PleasanterSsoMembershipTests
{
    private const string Cookies = ".AspNetCore.Cookies=secret-cookie; q.admin=own";
    private static string User(int? dept = 10, int tenant = 1, string login = "member") =>
        JsonSerializer.Serialize(new { StatusCode = 200, Response = new { TotalCount = 1,
            Data = new[] { new { TenantId = tenant, UserId = 2, LoginId = login, DeptId = dept } } } });

    private static string Group(int id, string[]? members = null, string[]? children = null, bool disabled = false, int tenant = 1) =>
        JsonSerializer.Serialize(new { StatusCode = 200, Response = new { TotalCount = 1,
            Data = new[] { new { TenantId = tenant, GroupId = id, Disabled = disabled,
                GroupMembers = members ?? [], GroupChildren = children ?? [] } } } });

    private static PleasanterSsoOptions Options(int[]? depts = null, int[]? groups = null, int timeoutMs = 5000) => new()
    {
        Enabled = true,
        InternalBaseUrl = new Uri("http://cookie-server/"),
        AllowedDeptIds = (depts ?? []).ToImmutableArray(),
        AllowedGroupIds = (groups ?? []).ToImmutableArray(),
        Timeout = TimeSpan.FromMilliseconds(timeoutMs),
    };

    private static (PleasanterSessionVerifier Verifier, FakeHttpMessageHandler Handler) Create(
        Dictionary<int, string>? groups = null, string? own = null, string? apiUser = null,
        bool apiKey = true, FakeTimeProvider? time = null, int delayMs = 0,
        HttpStatusCode groupStatus = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler((request, token) =>
        {
            time?.Advance(TimeSpan.FromMilliseconds(delayMs));
            token.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var isGroup = path.Contains("/groups/", StringComparison.Ordinal);
            var json = isGroup ? groups![int.Parse(path.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture)]
                : path.EndsWith("/users/get", StringComparison.Ordinal) ? own ?? User() : apiUser ?? own ?? User();
            return Task.FromResult(new HttpResponseMessage(isGroup ? groupStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        });
        return (new PleasanterSessionVerifier(new SingleHttpClientFactory(handler),
            apiKey ? PleasanterSsoTestConnections.WithApiKey : PleasanterSsoTestConnections.WithoutApiKey,
            NullLogger<PleasanterSessionVerifier>.Instance, time), handler);
    }

    [Theory]
    [InlineData(10, PleasanterSessionStatus.Authenticated)]
    [InlineData(11, PleasanterSessionStatus.NotAllowed)]
    public async Task 組織はIDで判定しキーが無くても本人の組織が一致すれば通す(int allowed, PleasanterSessionStatus expected)
    {
        var (verifier, handler) = Create(apiKey: false);
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(depts: [allowed]));
        Assert.Equal(expected, result.Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("User,2,False")]
    [InlineData("User,2,True")]
    [InlineData("Dept,10,False")]
    public async Task 組織が不一致でもグループのユーザーか組織メンバーなら通す(string member)
    {
        var (verifier, handler) = Create(new() { [7] = Group(7, [member]) });
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(depts: [99], groups: [7]));
        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("ApiKey", handler.Bodies[0], StringComparison.Ordinal);
        foreach (var request in handler.Requests.Skip(1))
        {
            Assert.Equal("pleasanter.api", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("Cookie"));
        }
        Assert.All(handler.Bodies.Skip(1), body => Assert.Contains(PleasanterSsoTestConnections.ApiKey, body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 組織が一致すればグループの取得は不要()
    {
        var (verifier, handler) = Create(apiKey: false);
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(depts: [10], groups: [7]));
        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false, PleasanterSessionStatus.Authenticated)]
    [InlineData(true, PleasanterSessionStatus.NotAllowed)]
    public async Task 子グループをたどるが無効な親グループは許可しない(bool disabled, PleasanterSessionStatus expected)
    {
        var (verifier, _) = Create(new() { [7] = Group(7, children: ["Group,8,"], disabled: disabled), [8] = Group(8, ["User,2,False"]) });
        Assert.Equal(expected, (await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7]))).Status);
    }

    [Fact]
    public async Task 循環していても同じグループは一度だけ読み所属が無ければ拒否する()
    {
        var (verifier, handler) = Create(new() { [7] = Group(7, children: ["Group,8,"]), [8] = Group(8, children: ["Group,7,"]) });
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7]));
        Assert.Equal(PleasanterSessionStatus.NotAllowed, result.Status);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task グループが上限を超える構成は拒否する()
    {
        var groups = Enumerable.Range(1, 65).ToDictionary(id => id, id => Group(id, children: [$"Group,{id + 1},"]));
        var (verifier, handler) = Create(groups);
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [1]));
        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("membership-group-limit", result.Reason);
        Assert.Equal(66, handler.Requests.Count);
    }

    [Fact]
    public async Task 所属が読めなければグループも含めて許可しない()
    {
        var (verifier, _) = Create(own: User(dept: null));
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(depts: [10], groups: [7]));
        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
    }

    [Fact]
    public async Task グループ検査に必要なキーが無ければ許可しない()
    {
        var (verifier, _) = Create(apiKey: false);
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7]));
        Assert.Equal("membership-no-api-key", result.Reason);
        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
    }

    [Theory]
    [InlineData(2, "member", 10)]
    [InlineData(1, "other", 10)]
    [InlineData(1, "member", 20)]
    public async Task キーで引いた本人が一致しなければ別の所属を使わない(int tenant, string login, int dept)
    {
        var (verifier, _) = Create(apiUser: User(dept, tenant, login));
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7]));
        Assert.Equal("membership-user-mismatch", result.Reason);
        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
    }

    public static TheoryData<string> InvalidGroups => new()
    {
        Group(7, ["User,2,False"], tenant: 2),
        Group(8, ["User,2,False"]),
        Group(7, ["User,2,False", "garbage"]),
        Group(7, ["User,2,False"], ["Group,8"]),
        Group(7).Replace("\"Disabled\":false,", "", StringComparison.Ordinal),
        Group(7).Replace("\"GroupMembers\":[]", "\"GroupMembers\":null", StringComparison.Ordinal),
        "{\"StatusCode\":200,\"Response\":{\"TotalCount\":0,\"Data\":[]}}",
    };

    [Theory]
    [MemberData(nameof(InvalidGroups))]
    public async Task 不正なグループ応答の一部に本人が含まれていても許可しない(string json)
    {
        var (verifier, _) = Create(new() { [7] = json });
        Assert.Equal(PleasanterSessionStatus.UpstreamError,
            (await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7]))).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task グループが取得できなければ許可しない(HttpStatusCode status)
    {
        var (verifier, _) = Create(new() { [7] = Group(7, ["User,2,False"]) }, groupStatus: status);
        Assert.Equal(PleasanterSessionStatus.UpstreamError,
            (await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7]))).Status);
    }

    [Fact]
    public async Task 本人とグループの問い合わせ合計が時間切れなら許可しない()
    {
        var (verifier, _) = Create(new() { [7] = Group(7, ["User,2,False"]) }, time: new FakeTimeProvider(), delayMs: 110);
        var result = await verifier.VerifyAsync(new StringValues(Cookies), Options(groups: [7], timeoutMs: 300));
        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("timeout", result.Reason);
    }
}
