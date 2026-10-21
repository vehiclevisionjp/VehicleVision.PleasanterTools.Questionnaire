using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 本人確認を Core.Web へ置き換えたときの、本アプリ側のつなぎ（Issue #706）。
/// </summary>
/// <remarks>
/// <para>
/// **判定そのもの（応答の読み方・代わりの経路・所属・時間切れ）は Core.Web の試験が受け持つ**
/// （本アプリの <c>PleasanterSessionVerifierTests</c> / <c>PleasanterSsoMembershipTests</c> を移した）。
/// ここでは、本アプリが渡す値と接続設定の渡し方が、置き換える前と同じであることを確かめる。
/// </para>
/// </remarks>
public class PleasanterSessionVerificationTests
{
    private const string ApiKey = "0123456789abcdef-secret-api-key";

    private const string BrowserCookies = ".AspNetCore.Cookies=AUTH; Pleasanter_SessionGuid=6f0c; q.admin=OWN";

    private const string OwnUserJson =
        """{"StatusCode":200,"Response":{"Offset":0,"PageSize":200,"TotalCount":1,"Data":[{"TenantId":1,"UserId":2,"LoginId":"sso-user1","Name":"SSO User 1","Disabled":false}]}}""";

    private const string ForbiddenJson = """{"Id":0,"StatusCode":403,"Message":"Forbidden"}""";

    private const string SessionSetJson =
        """{"StatusCode":200,"Response":{"UserId":2,"Key":"VehicleVision.Questionnaire.SsoProbe"}}""";

    private static readonly Uri OwnUserUrl = new("http://pleasanter.internal:8080/api/users/get");
    private static readonly Uri SessionSetUrl = new("http://pleasanter.internal:8080/api/sessions/set");
    private static readonly Uri UserByIdUrl = new("http://pleasanter.api:8080/root/api/users/2/get");

    private static PleasanterSsoOptions Options() => new()
    {
        Enabled = true,
        InternalBaseUrl = new Uri("http://pleasanter.internal:8080/"),
        LoginUrl = "/users/login",
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static PleasanterOptions Connection(string baseUrl, string apiKey) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        ApiKeyUserTimeZoneId = "Asia/Tokyo",
    };

    [Fact]
    public void 本アプリの値は置き換える前の検証器と同じ()
    {
        var settings = PleasanterSessionVerification.Settings;

        // ⚠️ **置き換える前の本アプリの検証器（PleasanterSessionVerifier）の値。** 変えないこと
        Assert.Equal("VehicleVision.Questionnaire.SsoProbe", settings.SessionProbeKey);
        Assert.Equal("QUESTIONNAIRE_PLEASANTER_BASEURL", settings.BaseUrlSettingName);
        Assert.Equal("QUESTIONNAIRE_PLEASANTER_APIKEY", settings.ApiKeySettingName);
        Assert.Equal(
            "VehicleVision.PleasanterTools.Questionnaire.Web.Services.PleasanterSessionVerifier",
            settings.LoggerCategory);
    }

    [Fact]
    public async Task 接続設定は回答の送信と同じURLとAPIキーを返す()
    {
        var source = new PleasanterApiConnectionSource(
            new StaticPleasanterOptionsProvider(Connection("http://pleasanter.api:8080/root", ApiKey)));

        var settings = await source.GetAsync();

        Assert.Equal("http://pleasanter.api:8080/root", settings.BaseUrl);
        Assert.Equal(ApiKey, settings.ApiKey);
    }

    [Fact]
    public async Task 接続設定を読めない例外は包まずに伝える()
    {
        var exception = new FormatException("broken");
        var source = new PleasanterApiConnectionSource(new ThrowingPleasanterOptionsProvider(exception));

        var thrown = await Assert.ThrowsAsync<FormatException>(() => source.GetAsync());

        Assert.Same(exception, thrown);
    }

    [Fact]
    public async Task 登録した検証器は本アプリの鍵とAPIキーで代わりの経路を通る()
    {
        var handler = new RoutingHandler(
            (OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.OK, SessionSetJson),
            (UserByIdUrl, HttpStatusCode.OK, OwnUserJson));
        var logs = new CapturingLoggerProvider();
        using var services = Build(
            new StaticPleasanterOptionsProvider(Connection("http://pleasanter.api:8080/root", ApiKey)),
            handler,
            logs);

        var verifier = services.GetRequiredService<IPleasanterSessionVerifier>();
        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
        Assert.Equal(new PleasanterIdentity(1, 2, "sso-user1", "SSO User 1"), result.Identity);
        Assert.Equal([OwnUserUrl, SessionSetUrl, UserByIdUrl], handler.Requests);

        using (var sessionBody = JsonDocument.Parse(handler.Bodies[1]))
        {
            Assert.Equal(
                "VehicleVision.Questionnaire.SsoProbe",
                sessionBody.RootElement.GetProperty("SessionKey").GetString());
        }

        using (var userBody = JsonDocument.Parse(handler.Bodies[2]))
        {
            Assert.Equal(ApiKey, userBody.RootElement.GetProperty("ApiKey").GetString());
        }

        // **ログのカテゴリは置き換える前の型の完全名**（Core の型名では出さない）。API キーはどのログにも出さない
        Assert.Contains(logs.Entries, entry => entry.Category == PleasanterSessionVerification.LoggerCategory);
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.Category.StartsWith("VehicleVision.PleasanterTools.Core.Web", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(ApiKey, entry.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 登録した検証器は接続設定を読めなければAPIキーが無いものとして設定名を案内する()
    {
        var handler = new RoutingHandler((OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson));
        var logs = new CapturingLoggerProvider();
        using var services = Build(
            new ThrowingPleasanterOptionsProvider(new InvalidOperationException("broken")),
            handler,
            logs);

        var verifier = services.GetRequiredService<IPleasanterSessionVerifier>();
        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("http-403", result.Reason);
        Assert.Equal([OwnUserUrl], handler.Requests);
        Assert.Contains(logs.Entries, entry =>
            entry.Category == PleasanterSessionVerification.LoggerCategory
            && entry.Level == LogLevel.Warning
            && entry.Message.Contains(AppSettingsProvider.PleasanterApiKeyKey, StringComparison.Ordinal));
    }

    /// <summary>本アプリと同じ登録をし、HTTP の送り先だけを差し替える。</summary>
    private static ServiceProvider Build(
        IPleasanterOptionsProvider pleasanterOptionsProvider,
        HttpMessageHandler handler,
        ILoggerProvider loggerProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(pleasanterOptionsProvider);
        services.AddQuestionnairePleasanterSessionVerifier();

        // **後から足した設定が勝つ。** 実際の通信の代わりに決まった応答を返す
        services
            .AddHttpClient(PleasanterSessionVerifier.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class ThrowingPleasanterOptionsProvider(Exception exception) : IPleasanterOptionsProvider
    {
        public Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<PleasanterOptions>(exception);
    }

    /// <summary>URL ごとに決まった応答を返し、宛先と本文を覚えるハンドラ。</summary>
    private sealed class RoutingHandler(params (Uri Url, HttpStatusCode Status, string Json)[] routes)
        : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            foreach (var (url, status, json) in routes)
            {
                if (request.RequestUri == url)
                {
                    return new HttpResponseMessage(status)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }
            }

            throw new InvalidOperationException($"想定していない宛先: {request.RequestUri}");
        }
    }

    /// <summary>カテゴリ付きでログを覚える。</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lock (owner.Entries)
                {
                    owner.Entries.Add((category, logLevel, $"{formatter(state, exception)} {exception}"));
                }
            }
        }
    }
}
