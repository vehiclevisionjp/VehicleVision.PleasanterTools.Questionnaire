using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理操作が記録されること。**そして、記録してはいけないものが入らないこと。**</summary>
public class AuditLogFilterTests
{
    private sealed class FakeStore : IAuditLogStore
    {
        public List<AuditEntry> Written { get; } = [];

        public bool ThrowOnWrite { get; set; }

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("書けない");
            }

            Written.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLogView>> ListAsync(
            AuditLogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogView>>([]);

        public Task<int> DeleteOlderThanAsync(
            DateTime threshold,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Written.RemoveAll(entry => entry.OccurredAt < threshold));
    }

    private static DefaultHttpContext Request(
        string method,
        string routePattern,
        Guid? adminUserId = null,
        (string Key, string Value)[]? routeValues = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = routePattern;

        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(routePattern),
            order: 0,
            new EndpointMetadataCollection(),
            displayName: null));

        foreach (var (key, value) in routeValues ?? [])
        {
            context.Request.RouteValues[key] = value;
        }

        if (adminUserId is { } id)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Test"));
        }

        return context;
    }

    private static async Task<FakeStore> RunAsync(
        HttpContext context,
        object? result,
        Action<FakeStore>? configure = null)
    {
        var store = new FakeStore();
        configure?.Invoke(store);

        var filter = new AuditLogFilter(store, NullLogger<AuditLogFilter>.Instance);

        await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => ValueTask.FromResult(result));

        return store;
    }

    [Fact]
    public async Task 読み取りは残さない()
    {
        // **一覧を開くたびに行が増えると、変えた操作が埋もれる**
        var store = await RunAsync(Request("GET", "/api/admin/users"), Results.Ok());

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task 変える操作は残す()
    {
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();

        var store = await RunAsync(
            Request(
                "POST",
                "/api/admin/users/{adminUserId}/disable",
                actor,
                [("adminUserId", target.ToString())]),
            Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.Equal(actor, entry.AdminUserId);
        Assert.Equal("POST /api/admin/users/{adminUserId}/disable", entry.Action);
        Assert.Equal(StatusCodes.Status200OK, entry.StatusCode);
        Assert.Equal("AdminUser", entry.TargetType);
        Assert.Equal(target.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task 経路に出せない対象は入口が明示する()
    {
        // **デッドレターの再送は `ResponseToken` を経路に出せない**
        // （監査ログへ入れない決まり。`_documents/データモデル設計.md` 2.6）。
        // 代わりに、どのアンケートの回答を戻したかを入口が預ける
        var context = Request("POST", "/api/admin/outbox/dead-letters/requeue", Guid.NewGuid());
        var surveyId = Guid.NewGuid();
        AuditNotes.SetTarget(context, "Survey", surveyId.ToString());

        var store = await RunAsync(context, Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.Equal("Survey", entry.TargetType);
        Assert.Equal(surveyId.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task 明示した対象は経路の値より優先する()
    {
        // **経路の値から見分けた対象で上書きされないこと**
        var context = Request(
            "POST",
            "/api/admin/surveys/{surveyId}/something",
            Guid.NewGuid(),
            [("surveyId", "経路の値")]);

        AuditNotes.SetTarget(context, "Survey", "明示した値");

        var store = await RunAsync(context, Results.Ok());

        Assert.Equal("明示した値", Assert.Single(store.Written).TargetId);
    }

    [Fact]
    public async Task 経路の型の制約は落とす()
    {
        // **制約は照合の都合で、読む人には要らない。** 残すと 1 行が横に長くなる
        var store = await RunAsync(
            Request(
                "POST",
                "/api/admin/surveys/{surveyId:guid}/publish",
                Guid.NewGuid()),
            Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.Equal("POST /api/admin/surveys/{surveyId}/publish", entry.Action);
    }

    [Fact]
    public async Task IPv4はIPv6の形で残さない()
    {
        // **同じ相手が 2 通りの書き方で残ると、送信元で絞れなくなる**
        var context = Request("POST", "/api/admin/login");
        context.Connection.RemoteIpAddress =
            System.Net.IPAddress.Parse("203.0.113.10").MapToIPv6();

        var store = await RunAsync(context, Results.Ok());

        Assert.Equal("203.0.113.10", Assert.Single(store.Written).IpAddress);
    }

    [Fact]
    public async Task 断られた操作も残す()
    {
        // **成功だけ残すと、試みられたことが分からない**
        var store = await RunAsync(
            Request("POST", "/api/admin/users/{adminUserId}/role", Guid.NewGuid()),
            Results.Json(new { message = "だめ" }, statusCode: StatusCodes.Status409Conflict));

        var entry = Assert.Single(store.Written);
        Assert.Equal(StatusCodes.Status409Conflict, entry.StatusCode);
    }

    [Fact]
    public async Task 認証を通っていない試みも残す()
    {
        // **合言葉が違えば誰なのか分からない。** それでも試みは残す
        var store = await RunAsync(
            Request("POST", "/api/admin/login"),
            Results.Json(new { }, statusCode: StatusCodes.Status401Unauthorized));

        var entry = Assert.Single(store.Written);
        Assert.Null(entry.AdminUserId);
        Assert.Equal(StatusCodes.Status401Unauthorized, entry.StatusCode);
    }

    [Fact]
    public async Task アンケートは対象として見分ける()
    {
        var surveyId = Guid.NewGuid();

        var store = await RunAsync(
            Request(
                "POST",
                "/api/admin/surveys/{surveyId}/publish",
                Guid.NewGuid(),
                [("surveyId", surveyId.ToString())]),
            Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.Equal("Survey", entry.TargetType);
        Assert.Equal(surveyId.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task 預けた補足が残る()
    {
        var context = Request("POST", "/api/admin/login");
        AuditNotes.Add(context, "loginId", "admin");

        var store = await RunAsync(context, Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.NotNull(entry.DetailJson);

        var detail = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailJson);
        Assert.Equal("admin", detail!["loginId"]);
    }

    [Fact]
    public async Task 日本語は逃がさずに残す()
    {
        // **既定の設定は非 ASCII を \uXXXX へ逃がす。**
        // DB の中で読めず、検索もできなくなる
        var context = Request("POST", "/api/admin/login");
        AuditNotes.Add(context, "loginId", "山田太郎");

        var store = await RunAsync(context, Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.Contains("山田太郎", entry.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HTMLに効く文字は逃がす()
    {
        // **画面へ出すときの逃がし忘れが、そのまま script の挿し込みにならないようにする**
        var context = Request("POST", "/api/admin/login");
        AuditNotes.Add(context, "loginId", "<script>alert(1)</script>");

        var store = await RunAsync(context, Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.DoesNotContain("<script>", entry.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 預けた補足の制御文字は落とす()
    {
        // **改行を通すと、記録の行を偽装できる**
        var context = Request("POST", "/api/admin/login");
        AuditNotes.Add(context, "loginId", "admin\n2026-08-19 誰かが管理者になった");

        var store = await RunAsync(context, Results.Ok());

        var entry = Assert.Single(store.Written);
        Assert.DoesNotContain("\n", entry.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 記録できなくても操作は通す()
    {
        // **操作はもう済んでいる。** ここで例外にすると「通ったのに失敗と伝える」
        var context = Request("POST", "/api/admin/users", Guid.NewGuid());
        var expected = Results.Ok();

        var store = new FakeStore { ThrowOnWrite = true };
        var filter = new AuditLogFilter(store, NullLogger<AuditLogFilter>.Instance);

        var actual = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => ValueTask.FromResult<object?>(expected));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task 要求本文には触らない()
    {
        // **触らない限り、合言葉は入りようがない。**
        // 本文を読んでいれば、読み取り位置が動く
        var context = Request("POST", "/api/admin/login");
        context.Request.Body = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("""{"loginId":"admin","password":"秘密"}"""));

        var store = await RunAsync(context, Results.Ok());

        Assert.Equal(0, context.Request.Body.Position);

        var entry = Assert.Single(store.Written);
        Assert.DoesNotContain("秘密", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
    }
}
