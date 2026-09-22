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
        var builder = new CountingContentSecurityPolicyBuilder();
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            provider,
            builder);

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
        Assert.Equal(2, builder.BuildCount);
    }

    [Fact]
    public async Task 同じ設定スナップショットではCSPの土台を使い回しScalarだけnonceを変える()
    {
        var builder = new CountingContentSecurityPolicyBuilder();
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            new FakeProvider(Snapshot(string.Empty, string.Empty)),
            builder);
        var first = ScalarContext();
        var second = ScalarContext();

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        var firstNonce = NonceOf(first);
        var secondNonce = NonceOf(second);
        Assert.NotEqual(firstNonce, secondNonce);
        Assert.Contains($"script-src 'self' 'nonce-{firstNonce}'", Csp(first));
        Assert.Contains($"script-src 'self' 'nonce-{secondNonce}'", Csp(second));
        Assert.Equal(1, builder.BuildCount);
    }

    private static DefaultHttpContext ScalarContext() =>
        new()
        {
            Request = { Path = "/scalar" },
        };

    private static string NonceOf(HttpContext context) =>
        Assert.IsType<string>(context.Items[SecurityHeadersMiddleware.ScalarCspNonceKey]);

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

    private sealed class CountingContentSecurityPolicyBuilder() :
        ContentSecurityPolicyBuilder([], [])
    {
        public int BuildCount { get; private set; }

        public override string BuildBase(AppSettingsSnapshot snapshot)
        {
            BuildCount++;
            return base.BuildBase(snapshot);
        }
    }
}
