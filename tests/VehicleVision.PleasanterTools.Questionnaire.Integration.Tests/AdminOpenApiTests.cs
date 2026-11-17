using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using VehicleVision.PleasanterTools.Questionnaire.Web;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理 API の OpenAPI 文書の公開範囲と、実装との食い違いを確かめる（Issue #1021）。</summary>
public sealed class AdminOpenApiTests
{
    private static readonly Dictionary<string, string> Enabled = new(StringComparer.Ordinal)
    {
        [OpenApiExposureOptions.EnabledSetting] = "true",
    };

    [Fact]
    public async Task 未認証には文書も画面も返さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = host.CreateClient();

        foreach (var path in new[] { "/openapi/v1.json", "/scalar/", "/scalar/v1" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("/api/admin", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 既定では有効化しない限り文書を返さない()
    {
        await using var host = await AdminContractHost.StartAsync();
        var admin = await host.SeedAdminAsync(withTotp: false);
        using var client = host.CreateClient();
        using var login = await host.LoginAsync(client, admin);
        login.EnsureSuccessStatusCode();

        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ログイン済みの管理者には管理APIだけの文書と画面を返す()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = await SignedInAsync(host);

        using var document = await ReadDocumentAsync(client);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.StartsWith("/api/admin", path, StringComparison.Ordinal));

        var scheme = document.RootElement.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty(AdminOpenApi.SessionSchemeName);
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("cookie", scheme.GetProperty("in").GetString());
        Assert.Equal(AdminOpenApi.SessionCookieName, scheme.GetProperty("name").GetString());

        using var page = await client.GetAsync("/scalar/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        // 外部 CDN から読み込まない（既定フォントを止めている）
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cdn.jsdelivr.net", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 文書の操作と実装の経路が一致する()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = await SignedInAsync(host);
        using var document = await ReadDocumentAsync(client);

        var documented = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var secured = operation.Value.TryGetProperty("security", out var security)
                    && security.GetArrayLength() > 0;
                documented.Add($"{operation.Name.ToUpperInvariant()} {path.Name.TrimEnd('/')}", secured);
            }
        }

        var implemented = new Dictionary<string, bool>(StringComparer.Ordinal);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } raw
                && raw.TrimStart('/').StartsWith(AdminOpenApi.PathPrefix, StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is not { ExcludeFromDescription: true });
        foreach (var endpoint in endpoints)
        {
            var path = NormalizedPath(endpoint.RoutePattern);
            var secured = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
            {
                implemented.Add($"{method.ToUpperInvariant()} {path}", secured);
            }
        }

        Assert.NotEmpty(implemented);
        Assert.Empty(implemented.Keys.Except(documented.Keys));
        Assert.Empty(documented.Keys.Except(implemented.Keys));
        foreach (var (operation, secured) in implemented)
        {
            Assert.True(secured == documented[operation], $"認可の有無が文書と食い違う: {operation}");
        }
    }

    private static async Task<HttpClient> SignedInAsync(AdminContractHost host)
    {
        var admin = await host.SeedAdminAsync(withTotp: false);
        var client = host.CreateClient();
        using var login = await host.LoginAsync(client, admin);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // 制約（`{id:guid}`）・省略記号を落とし、文書の書き方（`{id}`）へ揃える
    private static string NormalizedPath(RoutePattern pattern) =>
        "/" + string.Join('/', pattern.PathSegments.Select(segment => string.Concat(
            segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternParameterPart parameter => "{" + parameter.Name + "}",
                _ => string.Empty,
            })))).TrimEnd('/');
}
