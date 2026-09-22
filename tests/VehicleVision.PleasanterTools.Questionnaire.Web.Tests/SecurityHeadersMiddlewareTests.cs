using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>埋め込み設定が実際の CSP 応答ヘッダへ反映されることを確かめる。</summary>
public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task 設定変更後の要求から埋め込み先をCSPへ反映する()
    {
        var provider = new FakeProvider(Snapshot(string.Empty, string.Empty));
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            provider,
            new ContentSecurityPolicyBuilder([], []));

        var before = new DefaultHttpContext();
        await middleware.InvokeAsync(before);
        Assert.Contains("frame-src 'none'", Csp(before));
        Assert.Contains("frame-ancestors 'none'", Csp(before));

        provider.Snapshot = Snapshot("portal.example.com", "media.example.com");
        var after = new DefaultHttpContext();
        await middleware.InvokeAsync(after);

        Assert.Contains("frame-src https://media.example.com", Csp(after));
        Assert.Contains("img-src 'self' data: https://media.example.com", Csp(after));
        Assert.Contains("frame-ancestors https://portal.example.com", Csp(after));
        Assert.DoesNotContain("frame-src https:;", Csp(after));
    }

    [Fact]
    public async Task Scalarだけ要求ごとのnonceをCSPへ設定する()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/scalar";
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            new FakeProvider(Snapshot(string.Empty, string.Empty)),
            new ContentSecurityPolicyBuilder([], []));

        await middleware.InvokeAsync(context);

        var nonce = Assert.IsType<string>(
            context.Items[SecurityHeadersMiddleware.ScalarCspNonceKey]);
        Assert.Contains($"script-src 'self' 'nonce-{nonce}'", Csp(context));
    }

    private static string Csp(HttpContext context) =>
        context.Response.Headers.ContentSecurityPolicy.ToString();

    private static AppSettingsSnapshot Snapshot(string parents, string hosts) =>
        new(
            [],
            new Dictionary<string, string>
            {
                [EmbedParentOptions.AllowedParentsKey] = parents,
                [EmbedOptions.AllowedHostsKey] = hosts,
            }.ToFrozenDictionary(StringComparer.Ordinal),
            FrozenSet<string>.Empty);

    private sealed class FakeProvider(AppSettingsSnapshot snapshot) : IAppSettingsProvider
    {
        public AppSettingsSnapshot Snapshot { get; set; } = snapshot;

        public Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<AppSettingsSnapshot> SaveAsync(
            IReadOnlyDictionary<string, string?> values,
            Guid updatedByAdminUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
