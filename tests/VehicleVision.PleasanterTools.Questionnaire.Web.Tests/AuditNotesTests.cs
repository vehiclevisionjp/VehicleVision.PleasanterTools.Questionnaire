using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>監査ログへ入口から預ける補足。</summary>
public class AuditNotesTests
{
    private sealed class FakeStore : IAuditLogStore
    {
        public List<AuditEntry> Written { get; } = [];

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
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
            Task.FromResult(0);
    }

    private static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/admin/login";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/admin/login"),
            order: 0,
            new EndpointMetadataCollection(),
            displayName: null));
        return context;
    }

    private static async Task<AuditEntry> WriteAsync(HttpContext context)
    {
        var store = new FakeStore();
        var filter = new AuditLogFilter(store, NullLogger<AuditLogFilter>.Instance);

        await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        return Assert.Single(store.Written);
    }

    private static Dictionary<string, string> DetailOf(AuditEntry entry)
    {
        Assert.NotNull(entry.DetailJson);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailJson!)!;
    }

    [Fact]
    public async Task 補足の改行は監査ログへ入れない()
    {
        var context = Request();
        AuditNotes.Add(context, "loginId", "admin\r\n2026-08-21 偽の記録");

        var detail = DetailOf(await WriteAsync(context));

        Assert.DoesNotContain("\r", detail["loginId"], StringComparison.Ordinal);
        Assert.DoesNotContain("\n", detail["loginId"], StringComparison.Ordinal);
        Assert.Contains("制御文字を除去", detail["loginId"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 補足が長すぎると切り詰める()
    {
        var context = Request();
        AuditNotes.Add(context, "loginId", new string('あ', 500));

        var detail = DetailOf(await WriteAsync(context));

        Assert.True(detail["loginId"].Length < 200, $"切れていない: {detail["loginId"].Length} 文字");
        Assert.Contains("切り詰め", detail["loginId"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 明示した対象の制御文字も落とす()
    {
        var context = Request();
        AuditNotes.SetTarget(context, "Survey\nInjected", "id\r\nnext");

        var entry = await WriteAsync(context);

        Assert.Equal("SurveyInjected(制御文字を除去)", entry.TargetType);
        Assert.Equal("idnext(制御文字を除去)", entry.TargetId);
    }

    [Fact]
    public void 補足の鍵が空なら受け付けない()
    {
        Assert.Throws<ArgumentException>(() => AuditNotes.Add(Request(), " ", "admin"));
    }

    [Fact]
    public void 対象の種類が空なら受け付けない()
    {
        Assert.Throws<ArgumentException>(() => AuditNotes.SetTarget(Request(), "", "id"));
    }
}
