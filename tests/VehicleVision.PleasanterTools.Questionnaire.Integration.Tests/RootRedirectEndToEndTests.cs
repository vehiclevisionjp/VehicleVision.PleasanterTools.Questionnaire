using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// トップページ（<c>/</c>）の案内（<c>QUESTIONNAIRE_ROOT_REDIRECT_URL</c>）を、実アプリ（SQLite）へ HTTP で当てて確かめる（Issue #967）。
/// </summary>
public sealed class RootRedirectEndToEndTests
{
    private const string IndexMarker = "root-redirect-test-index";

    [Fact]
    public async Task 未設定なら従来どおり入口のHTMLを返す()
    {
        await using var app = await App.StartAsync(redirectUrl: null, pathBase: null);
        using var http = app.Client();

        using var response = await http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(IndexMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task サイト内のパスへ302で案内する()
    {
        await using var app = await App.StartAsync("/f/welcome", pathBase: null);
        using var http = app.Client();

        using var response = await http.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/f/welcome", response.Headers.Location?.OriginalString);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        // セキュリティヘッダは案内にも付く
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    [Theory]
    [InlineData("https://example.com/landing?a=1")]
    [InlineData("http://example.com/")]
    public async Task 絶対URLへ302で案内する(string target)
    {
        await using var app = await App.StartAsync(target, pathBase: null);
        using var http = app.Client();

        using var response = await http.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(target, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task サブパス配置ではサイト内のパスへサブパスを足す()
    {
        await using var app = await App.StartAsync("/f/welcome", pathBase: "/questionnaire");
        using var http = app.Client();

        using var response = await http.GetAsync("/questionnaire/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/questionnaire/f/welcome", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task サブパス配置でも絶対URLはそのまま案内する()
    {
        await using var app = await App.StartAsync("https://example.com/", pathBase: "/questionnaire");
        using var http = app.Client();

        using var response = await http.GetAsync("/questionnaire/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task HEADも案内する()
    {
        await using var app = await App.StartAsync("https://example.com/", pathBase: null);
        using var http = app.Client();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task 要求の値では宛先が変わらない()
    {
        await using var app = await App.StartAsync("https://example.com/fixed", pathBase: null);
        using var http = app.Client();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/?next=https://evil.example/&redirect=//evil.example&url=javascript:alert(1)");
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("X-Forwarded-Prefix", "/evil");
        request.Headers.Add("X-Original-URL", "https://evil.example/");
        request.Headers.Referrer = new Uri("https://evil.example/");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/fixed", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task 案内の対象は根だけで他の入口は変えない()
    {
        await using var app = await App.StartAsync("https://example.com/", pathBase: null);
        using var http = app.Client();

        // 入口の HTML（別名）と回答画面の入口は今までどおり
        using var index = await http.GetAsync("/index.html");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains(IndexMarker, await index.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var form = await http.GetAsync("/f/x");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains(IndexMarker, await form.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(form.Headers.Location);

        using var health = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Null(health.Headers.Location);

        // 管理画面（admin.html は置いていないので従来どおり 404）。案内へは変わらない
        using var admin = await http.GetAsync("/admin");
        Assert.NotEqual(HttpStatusCode.Found, admin.StatusCode);
        Assert.Null(admin.Headers.Location);

        // API
        using var api = await http.GetAsync("/api/forms/not-found");
        Assert.NotEqual(HttpStatusCode.Found, api.StatusCode);
        Assert.Null(api.Headers.Location);
    }

    [Fact]
    public async Task 書き込み系の要求は案内しない()
    {
        await using var app = await App.StartAsync("https://example.com/", pathBase: null);
        using var http = app.Client();

        using var response = await http.PostAsync("/", content: null);

        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task 危険な設定は起動を失敗させる()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await App.StartAsync("//evil.example", pathBase: null));

        Assert.Contains(RootRedirectOptions.Setting, exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class App : IAsyncDisposable
    {
        private readonly string directory;
        private readonly Factory factory;

        private App(string directory, Factory factory)
        {
            this.directory = directory;
            this.factory = factory;
        }

        public static async Task<App> StartAsync(string? redirectUrl, string? pathBase)
        {
            var directory = Path.Combine(
                FindRepositoryRoot(), "temp", "20261115_root-redirect", Guid.NewGuid().ToString("N"));
            var factory = new Factory(directory, redirectUrl, pathBase);
            var app = new App(directory, factory);
            try
            {
                QuestionnaireDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, factory.ConnectionString);
                using var client = app.Client();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var readyPath = pathBase is null ? "/ready" : $"{pathBase}/ready";
                    using var ready = await client.GetAsync(readyPath, timeout.Token);
                    if (ready.StatusCode == HttpStatusCode.OK)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                }

                return app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "VehicleVision.PleasanterTools.Questionnaire.slnx")))
                {
                    return current.FullName;
                }
            }

            throw new DirectoryNotFoundException("Questionnaire のソリューションが見つかりません。");
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string directory;

        public Factory(string directory, string? redirectUrl, string? pathBase)
        {
            this.directory = directory;
            var webRoot = Path.Combine(directory, "wwwroot");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(
                Path.Combine(webRoot, "index.html"),
                $"""<html><body>{IndexMarker}<script src="__QUESTIONNAIRE_BASE_PATH__/assets/i.js"></script>"""
                + """<meta name="questionnaire-base-path" content="__QUESTIONNAIRE_BASE_PATH__" /></body></html>""");
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "root-redirect.db"),
                Pooling = false,
            }.ToString();
            var parameters = Path.Combine(directory, "App_Data", "Parameters");
            Directory.CreateDirectory(parameters);
            var settings = new Dictionary<string, string?>
            {
                ["QUESTIONNAIRE_DB_PROVIDER"] = "Sqlite",
                ["QUESTIONNAIRE_DB_CONNECTIONSTRING"] = ConnectionString,
                ["QUESTIONNAIRE_DB_AUTO_MIGRATE"] = "false",
                ["QUESTIONNAIRE_DB_SKIP_MIGRATION_CHECK"] = "false",
                ["QUESTIONNAIRE_SECRET_KEY"] = SecretProtector.GenerateKey(),
                ["QUESTIONNAIRE_ADMIN_SESSION_STORE"] = "Database",
                ["QUESTIONNAIRE_SAML_ENABLED"] = "false",
                ["QUESTIONNAIRE_PLEASANTERSSO_ENABLED"] = "false",
                ["QUESTIONNAIRE_ASSET_PATH"] = Path.Combine(directory, "assets"),
                ["QUESTIONNAIRE_PLEASANTER_BASEURL"] = "https://localhost.invalid",
                ["QUESTIONNAIRE_PLEASANTER_APIKEY"] = "root-redirect-test-not-a-real-key",
            };
            if (redirectUrl is not null)
            {
                settings[RootRedirectOptions.Setting] = redirectUrl;
            }

            if (pathBase is not null)
            {
                settings[PathBaseOptions.Setting] = pathBase;
            }

            // 環境変数より強い専用の設定ファイルで、開発者の DB・資格情報を使わせない
            File.WriteAllText(
                Path.Combine(parameters, "General.local.json"), JsonSerializer.Serialize(settings));
        }

        public string ConnectionString { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(directory);
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                Type[] deliveryWorkers =
                [
                    typeof(ResponseSenderHostedService),
                    typeof(AssetHistorySenderHostedService),
                    typeof(MailSenderHostedService),
                    typeof(ResponseNotificationMailerHostedService),
                ];
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType is { } type && deliveryWorkers.Contains(type)).ToArray())
                {
                    services.Remove(service);
                }
            });
        }
    }
}
