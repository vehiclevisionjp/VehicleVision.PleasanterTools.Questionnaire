using System.Collections.Immutable;
using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>設定スナップショットから CSP を組み立て、セキュリティヘッダを付ける。</summary>
public sealed class SecurityHeadersMiddleware(
    RequestDelegate next,
    IAppSettingsProvider appSettings,
    ContentSecurityPolicyBuilder contentSecurityPolicy)
{
    public const string ScalarCspNonceKey = "ScalarCspNonce";

    private readonly object cspGate = new();
    private CachedCsp? cachedCsp;

    public async Task InvokeAsync(HttpContext context)
    {
        var snapshot = await appSettings.GetAsync(context.RequestAborted).ConfigureAwait(false);
        var csp = CspOf(snapshot);
        string? nonce = null;
        if (context.Request.Path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase))
        {
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            context.Items[ScalarCspNonceKey] = nonce;
            csp = ContentSecurityPolicyBuilder.WithNonce(csp, nonce);
        }

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
        headers["Content-Security-Policy"] = csp;
        await next(context);
    }

    private string CspOf(AppSettingsSnapshot snapshot)
    {
        if (Volatile.Read(ref cachedCsp) is { } current
            && ReferenceEquals(current.Snapshot, snapshot))
        {
            return current.Value;
        }

        lock (cspGate)
        {
            if (Volatile.Read(ref cachedCsp) is { } cached
                && ReferenceEquals(cached.Snapshot, snapshot))
            {
                return cached.Value;
            }

            var created = new CachedCsp(snapshot, contentSecurityPolicy.BuildBase(snapshot));
            Volatile.Write(ref cachedCsp, created);
            return created.Value;
        }
    }

    private sealed record CachedCsp(AppSettingsSnapshot Snapshot, string Value);
}

/// <summary>起動時の固定値と要求時の埋め込み設定から CSP を組み立てる。</summary>
public class ContentSecurityPolicyBuilder(
    ImmutableArray<string> analyticsSources,
    ImmutableArray<string> captchaSources)
{
    private readonly ImmutableArray<string> externalScriptSources =
        analyticsSources.AddRange(captchaSources);

    /// <summary>設定スナップショットへ対応する CSP の土台を組み立てる。</summary>
    public virtual string BuildBase(AppSettingsSnapshot snapshot)
    {
        var embedSources = EmbedOptions.FromSnapshot(snapshot).CspSources;
        var embedParentSources = EmbedParentOptions.FromSnapshot(snapshot).CspSources;

        return string.Join("; ",
        [
            "default-src 'self'",
            // 埋め込み先は列挙されたホストだけを足し、https: 全体へは広げない。
            "img-src 'self' data:"
                + Join(embedSources)
                + Join(analyticsSources)
                + Join(captchaSources),
            "frame-src "
                + (embedSources.IsEmpty && captchaSources.IsEmpty
                    ? "'none'"
                    : string.Join(' ', embedSources.AddRange(captchaSources))),
            "frame-ancestors "
                + (embedParentSources.IsEmpty ? "'none'" : string.Join(' ', embedParentSources)),
            "base-uri 'self'",
            "object-src 'none'",
            "script-src 'self'" + Join(externalScriptSources),
            "connect-src 'self'" + Join(externalScriptSources),
        ]);
    }

    /// <summary>Scalar 用の要求ごとの nonce を CSP の土台へ差し込む。</summary>
    public static string WithNonce(string csp, string nonce) =>
        csp.Replace(
            "script-src 'self'",
            $"script-src 'self' 'nonce-{nonce}'",
            StringComparison.Ordinal);

    private static string Join(ImmutableArray<string> sources) =>
        sources.IsEmpty ? string.Empty : " " + string.Join(' ', sources);
}
