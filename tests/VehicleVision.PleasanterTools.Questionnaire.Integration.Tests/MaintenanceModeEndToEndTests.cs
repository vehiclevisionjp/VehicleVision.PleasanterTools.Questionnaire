using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>メンテナンス中の公開経路と管理経路を、動いているアプリへ当てて確かめる。</summary>
public class MaintenanceModeEndToEndTests
{
    private const string Password = "long-enough-password";
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    [Fact]
    public async Task メンテ中は回答を503にして管理者のログインは通す()
    {
        if (!Enabled)
        {
            return;
        }

        var factory = E2EDatabase.AppFactory();
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await connection.ExecuteAsync(E2EDatabase.Sql("DELETE FROM [AdminRecoveryCodes]"));
        await connection.ExecuteAsync(E2EDatabase.Sql("DELETE FROM [AdminUsers]"));

        var store = new MaintenanceModeStore(factory);
        await store.SetAsync(
            true,
            "ただいまメンテナンス中です",
            "The service is currently under maintenance.",
            Guid.NewGuid(),
            DateTime.UtcNow);

        try
        {
            using var http = CreateClient();

            using (var form = await http.GetAsync("/api/forms/not-a-survey"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, form.StatusCode);
                Assert.Equal(TimeSpan.FromSeconds(300), form.Headers.RetryAfter?.Delta);
                var body = JsonNode.Parse(await form.Content.ReadAsStringAsync());
                Assert.Equal("maintenance", body!["reason"]!.GetValue<string>());
                Assert.Equal("ただいまメンテナンス中です", body["message"]!.GetValue<string>());
            }

            using (var formPage = await http.GetAsync("/f/not-a-survey"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, formPage.StatusCode);
                Assert.Equal(TimeSpan.FromSeconds(300), formPage.Headers.RetryAfter?.Delta);
                Assert.Contains(
                    "ただいまメンテナンス中です",
                    WebUtility.HtmlDecode(await formPage.Content.ReadAsStringAsync()));
            }

            using (var health = await http.GetAsync("/healthz"))
            {
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            }

            using (var ready = await http.GetAsync("/ready"))
            {
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            }

            using var setup = await http.PostAsJsonAsync(
                "/api/admin/setup",
                new { loginId = "maintenance-admin", password = Password });
            setup.EnsureSuccessStatusCode();
            var next = await AdminTwoFactorE2E.NextOfAsync(setup);
            await AdminTwoFactorE2E.EnrollAnywayAsync(http, next, Password);

            using var session = await http.GetAsync("/api/admin/session");
            session.EnsureSuccessStatusCode();
            var sessionBody = JsonNode.Parse(await session.Content.ReadAsStringAsync());
            Assert.True(sessionBody!["authenticated"]!.GetValue<bool>());
        }
        finally
        {
            await store.SetAsync(
                false,
                MaintenanceModeOptions.DefaultMessageJa,
                MaintenanceModeOptions.DefaultMessageEn,
                Guid.NewGuid(),
                DateTime.UtcNow);
        }
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = new Uri(BaseUrl),
        DefaultRequestHeaders = { { "Accept-Language", "ja" } },
    };
}
