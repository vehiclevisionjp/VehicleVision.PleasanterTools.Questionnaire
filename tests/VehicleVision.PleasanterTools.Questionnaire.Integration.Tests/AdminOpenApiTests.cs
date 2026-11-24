using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using VehicleVision.PleasanterTools.Questionnaire.Web;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

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

    [Fact]
    public async Task 個人トークンのスキームは_httpのbearerで_cookieのスキームを壊さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = await SignedInAsync(host);
        using var document = await ReadDocumentAsync(client);

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        var token = schemes.GetProperty(AdminOpenApi.TokenSchemeName);
        Assert.Equal("http", token.GetProperty("type").GetString());
        Assert.Equal("bearer", token.GetProperty("scheme").GetString());
        // 値の例は書かない（秘密スキャンの誤検知と、漏えいの見本を避ける）
        Assert.DoesNotMatch(@"qat_[0-9a-f]{8}_", token.GetProperty("description").GetString()!);

        var session = schemes.GetProperty(AdminOpenApi.SessionSchemeName);
        Assert.Equal("apiKey", session.GetProperty("type").GetString());
        Assert.Equal("cookie", session.GetProperty("in").GetString());
    }

    /// <summary>
    /// 許可宣言（<c>AllowAutomation</c>）と文書の食い違いを拾う（Issue #1030）。
    /// 宣言のある口だけにトークンの security が付き、スコープが宣言と一致する。宣言の無い口にはセッションだけ。
    /// </summary>
    [Fact]
    public async Task 文書のトークンの要求は_許可宣言と一致する()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = await SignedInAsync(host);
        using var document = await ReadDocumentAsync(client);

        var documented = new Dictionary<string, (bool Session, List<string>? TokenScopes)>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var session = false;
                List<string>? token = null;
                if (operation.Value.TryGetProperty("security", out var security))
                {
                    foreach (var requirement in security.EnumerateArray())
                    {
                        foreach (var scheme in requirement.EnumerateObject())
                        {
                            var scopes = scheme.Value.EnumerateArray().Select(v => v.GetString()!).ToList();
                            if (scheme.Name == AdminOpenApi.SessionSchemeName)
                            {
                                session = true;
                                Assert.Empty(scopes);
                            }
                            else if (scheme.Name == AdminOpenApi.TokenSchemeName)
                            {
                                Assert.Null(token);
                                token = scopes;
                            }
                            else
                            {
                                Assert.Fail($"未知のスキーム: {scheme.Name}");
                            }
                        }
                    }
                }

                documented.Add($"{operation.Name.ToUpperInvariant()} {path.Name.TrimEnd('/')}", (session, token));
            }
        }

        var declared = 0;
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } raw
                && raw.TrimStart('/').StartsWith(AdminOpenApi.PathPrefix, StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is not { ExcludeFromDescription: true });
        foreach (var endpoint in endpoints)
        {
            var access = endpoint.Metadata.GetMetadata<AutomationAccessMetadata>();
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                var key = $"{method.ToUpperInvariant()} {NormalizedPath(endpoint.RoutePattern)}";
                var actual = documented[key];
                if (access is null)
                {
                    Assert.True(actual.TokenScopes is null, $"宣言が無いのにトークンが書かれている: {key}");
                }
                else
                {
                    declared++;
                    Assert.True(actual.Session, $"セッションの要求が無い: {key}");
                    Assert.True(
                        actual.TokenScopes is { Count: 1 } scopes && scopes[0] == access.Scope,
                        $"スコープが宣言（{access.Scope}）と一致しない: {key}");
                    Assert.True(AdminApiTokenScopes.IsAllowed(access.Scope), $"許可リスト外のスコープ: {key}");
                }
            }
        }

        // 宣言が 1 つも無い（＝検査が空振り）状態を許さない
        Assert.True(declared > 0);
        Assert.Equal(declared, documented.Values.Count(value => value.TokenScopes is not null));
    }

    [Fact]
    public async Task 閲覧画面はTry_itを出さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = await SignedInAsync(host);

        using var page = await client.GetAsync("/scalar/");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Matches(@"hideTestRequestButton&quot;:\s*true|""hideTestRequestButton"":\s*true", html);
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
