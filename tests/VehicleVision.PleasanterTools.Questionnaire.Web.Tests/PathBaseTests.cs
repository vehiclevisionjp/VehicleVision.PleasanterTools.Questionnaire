using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>サブパス（<c>QUESTIONNAIRE_PATH_BASE</c>）で動かす（Issue #465）。</summary>
/// <remarks>
/// **経路の照合と cookie の Path は、本物の Kestrel へ HTTP で当てて確かめる。**
/// <c>UseRouting</c> の位置を誤ると、単体で呼ぶだけでは気付けない
/// （WebApplication が先頭へ自動で入れた経路照合は、サブパスを剥がす前の Path を見る）。
/// </remarks>
public class PathBaseTests
{
    // ---- 設定の検証 -------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/")]
    public void 未設定と根はサブパス無し(string? value)
    {
        var options = PathBaseOptions.Parse(value);

        Assert.False(options.IsConfigured);
        Assert.Equal(PathString.Empty, options.Value);
    }

    [Theory]
    [InlineData("/questionnaire")]
    [InlineData("/apps/questionnaire")]
    [InlineData("/Q-1_a.b~c")]
    public void 使える値はそのまま受け付ける(string value)
    {
        var options = PathBaseOptions.Parse(value);

        Assert.True(options.IsConfigured);
        Assert.Equal(value, options.Value.Value);
    }

    [Theory]
    [InlineData("questionnaire")]
    [InlineData("/questionnaire/")]
    [InlineData("//questionnaire")]
    [InlineData("/apps//questionnaire")]
    [InlineData("/questionnaire?x=1")]
    [InlineData("/questionnaire#top")]
    [InlineData("/ques tionnaire")]
    [InlineData("/%71uestionnaire")]
    [InlineData("/apps/../questionnaire")]
    [InlineData("/./questionnaire")]
    [InlineData("/..")]
    [InlineData(@"/apps\questionnaire")]
    [InlineData("https://example.com/questionnaire")]
    public void 書き間違いは起動時に止める(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PathBaseOptions.Parse(value));

        // **英語で出す。** Azure の Kudu の Debug console で日本語が化ける（Issue #225）
        Assert.Contains(PathBaseOptions.Setting, exception.Message, StringComparison.Ordinal);
        Assert.All(exception.Message, character => Assert.True(character < 0x80));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/questionnaire")]
    [InlineData("https://example.com/", "https://example.com/questionnaire")]
    [InlineData("https://example.com/questionnaire", "https://example.com/questionnaire")]
    [InlineData("https://example.com/questionnaire/", "https://example.com/questionnaire")]
    [InlineData("https://example.com/Questionnaire", "https://example.com/Questionnaire")]
    [InlineData("https://example.com/portal", "https://example.com/portal/questionnaire")]
    public void メールの起点にはサブパスを1度だけ足す(string baseUrl, string expected)
    {
        var options = PathBaseOptions.Parse("/questionnaire");

        Assert.Equal(expected, options.ComposePublicUrl(baseUrl));
    }

    [Fact]
    public void サブパス無しならメールの起点を変えない()
    {
        Assert.Equal("https://example.com/", PathBaseOptions.Root.ComposePublicUrl("https://example.com/"));
        Assert.Null(PathBaseOptions.Parse("/questionnaire").ComposePublicUrl(null));
    }

    // ---- 入口の HTML ------------------------------------------------------------

    private const string IndexTemplate =
        """<meta name="questionnaire-base-path" content="__QUESTIONNAIRE_BASE_PATH__" />"""
        + """<script type="module" src="__QUESTIONNAIRE_BASE_PATH__/assets/index.js"></script>""";

    private const string AdminTemplate =
        IndexTemplate
        + """<meta name="questionnaire-admin-path" content="__QUESTIONNAIRE_ADMIN_PATH__" />""";

    [Fact]
    public void 入口のHTMLへサブパスを埋める()
    {
        var html = HtmlShell.Apply(IndexTemplate, HtmlShell.IndexFile, new PathString("/questionnaire"), "/admin");

        Assert.Contains("""content="/questionnaire" """, html, StringComparison.Ordinal);
        Assert.Contains("""src="/questionnaire/assets/index.js""", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__QUESTIONNAIRE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void サブパス無しなら従来どおり根から資産を指す()
    {
        var html = HtmlShell.Apply(AdminTemplate, HtmlShell.AdminFile, PathString.Empty, "/admin");

        Assert.Contains("""name="questionnaire-base-path" content="" """, html, StringComparison.Ordinal);
        Assert.Contains("""src="/assets/index.js""", html, StringComparison.Ordinal);
        Assert.Contains("""name="questionnaire-admin-path" content="/admin" """, html, StringComparison.Ordinal);
    }

    [Fact]
    public void 管理画面の入口はサブパス込みで渡す()
    {
        var html = HtmlShell.Apply(
            AdminTemplate, HtmlShell.AdminFile, new PathString("/questionnaire"), "/back-office");

        Assert.Contains(
            """name="questionnaire-admin-path" content="/questionnaire/back-office" """,
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 埋め込み先の無いHTMLは組み立ての不備として止める()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HtmlShell.Apply("<html></html>", HtmlShell.IndexFile, PathString.Empty, "/admin"));
        Assert.Throws<InvalidOperationException>(() =>
            HtmlShell.Apply(IndexTemplate, HtmlShell.AdminFile, PathString.Empty, "/admin"));
    }

    // ---- 本アプリの cookie の Path -----------------------------------------------

    [Fact]
    public void 配布資産のCookieはサブパスの中に閉じる()
    {
        var options = FormEndpoints.AssetCookieOptions(
            allowEmbedding: false,
            isHttps: true,
            publicId: "pub-1",
            pathBase: new PathString("/questionnaire"));

        Assert.Equal("/questionnaire/api/forms/pub-1/assets", options.Path);
        Assert.Equal(
            "/api/forms/pub-1/assets",
            FormEndpoints.AssetCookieOptions(false, true, "pub-1").Path);
    }

    [Fact]
    public void 救済のCookieはサブパスの中に閉じる()
    {
        var policy = new AdminPasswordSignInPolicy(
            new AdminPasswordSignInOptions(false, new string('x', 32)),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<AdminPasswordSignInPolicy>.Instance);
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/questionnaire";

        Assert.True(policy.TryGrantRescue(context, new string('x', 32)));

        Assert.Contains(
            "path=/questionnaire/api/admin",
            context.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- 動いているアプリへ当てる -------------------------------------------------

    [Fact]
    public async Task サブパス配下でだけAPIが応答する()
    {
        await using var app = await StartAsync("/questionnaire");
        using var http = Client(app);

        using var inside = await http.GetAsync("/questionnaire/api/ping");
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Equal("/questionnaire|/api/ping", await inside.Content.ReadAsStringAsync());

        // **サブパスの外は応答しない。** 同じホストの Pleasanter の経路を奪わない
        using var outside = await http.GetAsync("/api/ping");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        // **区切りの途中で一致しただけのものは別の経路**
        using var similar = await http.GetAsync("/questionnaire-other/api/ping");
        Assert.Equal(HttpStatusCode.NotFound, similar.StatusCode);
    }

    [Fact]
    public async Task 生存確認はサブパスの内外どちらでも応答する()
    {
        await using var app = await StartAsync("/questionnaire");
        using var http = Client(app);

        using var inside = await http.GetAsync("/questionnaire/healthz");
        using var outside = await http.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Equal(HttpStatusCode.OK, outside.StatusCode);
    }

    [Fact]
    public async Task 未設定なら従来どおり根で応答する()
    {
        await using var app = await StartAsync(null);
        using var http = Client(app);

        using var root = await http.GetAsync("/api/ping");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("|/api/ping", await root.Content.ReadAsStringAsync());

        using var prefixed = await http.GetAsync("/questionnaire/api/ping");
        Assert.Equal(HttpStatusCode.NotFound, prefixed.StatusCode);
    }

    [Fact]
    public async Task IISが先にPathBaseを渡しても二重にしない()
    {
        // **ANCM（IIS のサブアプリケーション）の振る舞いを真似る。**
        // サーバがサブパスを PathBase へ移してから、アプリのパイプラインへ渡す
        await using var app = await StartAsync("/questionnaire", simulateAncm: "/questionnaire");
        using var http = Client(app);

        using var response = await http.GetAsync("/questionnaire/api/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/questionnaire|/api/ping", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 管理者のcookieはサブパスの中に閉じる()
    {
        await using var app = await StartAsync("/questionnaire");
        using var http = Client(app);

        using var response = await http.PostAsync("/questionnaire/api/signin", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("q.admin=", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/questionnaire;", cookie + ";", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task サブパス無しなら管理者のcookieは従来どおり根に置く()
    {
        await using var app = await StartAsync(null);
        using var http = Client(app);

        using var response = await http.PostAsync("/api/signin", content: null);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/;", cookie + ";", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 作成したものの場所はサブパス込みで返る()
    {
        await using var app = await StartAsync("/questionnaire");
        using var http = Client(app);

        using var response = await http.PostAsync("/questionnaire/api/created", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/questionnaire/api/admin/surveys/1", response.Headers.Location?.OriginalString);
    }

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(app.Urls.First()),
        };

    /// <summary>本番と同じ順（サブパス → 経路照合 → 認証）で組んだ小さなアプリを起動する。</summary>
    private static async Task<WebApplication> StartAsync(string? pathBase, string? simulateAncm = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services
            .AddAuthentication(AdminAuthSchemes.Session)
            .AddCookie(AdminAuthSchemes.Session, options =>
                AdminAuthSchemes.Configure(options, "q.admin", TimeSpan.FromHours(1)));
        builder.Services.AddAuthorization();

        var app = builder.Build();

        if (simulateAncm is not null)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(simulateAncm, out var remaining))
                {
                    context.Request.PathBase = simulateAncm;
                    context.Request.Path = remaining;
                }

                await next(context);
            });
        }

        app.UseQuestionnairePathBase(PathBaseOptions.Parse(pathBase));
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/api/ping", (HttpContext context) =>
            $"{context.Request.PathBase}|{context.Request.Path}");
        app.MapGet("/healthz", () => Results.Ok());
        app.MapPost("/api/signin", async (HttpContext context) =>
        {
            await context.SignInAsync(
                AdminAuthSchemes.Session,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test")));
            return Results.Ok();
        });
        app.MapPost("/api/created", (HttpContext context) =>
            Results.Created($"{context.Request.PathBase}/api/admin/surveys/1", new { }));

        await app.StartAsync();
        return app;
    }
}
