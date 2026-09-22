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

    public async Task InvokeAsync(HttpContext context)
    {
        var snapshot = await appSettings.GetAsync(context.RequestAborted).ConfigureAwait(false);
        string? nonce = null;
        if (context.Request.Path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase))
        {
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            context.Items[ScalarCspNonceKey] = nonce;
        }

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
        headers["Content-Security-Policy"] = contentSecurityPolicy.Build(snapshot, nonce);
        await next(context);
    }
}

/// <summary>起動時の固定値と要求時の埋め込み設定から CSP を組み立てる。</summary>
public sealed class ContentSecurityPolicyBuilder(
    ImmutableArray<string> analyticsSources,
    ImmutableArray<string> captchaSources)
{
    private readonly ImmutableArray<string> externalScriptSources =
        analyticsSources.AddRange(captchaSources);

    public string Build(AppSettingsSnapshot snapshot, string? nonce = null)
    {
        var embedSources = EmbedOptions.FromSnapshot(snapshot).CspSources;
        var embedParentSources = EmbedParentOptions.FromSnapshot(snapshot).CspSources;
        var scriptSources = "script-src 'self'"
            + (nonce is null ? string.Empty : $" 'nonce-{nonce}'")
            + Join(externalScriptSources);

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
            scriptSources,
            "connect-src 'self'" + Join(externalScriptSources),
        ]);
    }

    private static string Join(ImmutableArray<string> sources) =>
        sources.IsEmpty ? string.Empty : " " + string.Join(' ', sources);
}
