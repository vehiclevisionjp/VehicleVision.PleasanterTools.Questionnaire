using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.Core.Web.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// テスト回答の削除の権限を、実アプリ（SQLite）で確かめる（Issue #1024）。
/// <b>削除そのものの挙動は <c>TestResponseCleanupEndToEndTests</c> が見る。</b>ここは入口の権限だけ。
/// 対象のアンケートは回答 JSON 列が無く、<b>権限を通っても何も消さない</b>（400 で断る）。
/// </summary>
public sealed class AdminTestResponsePermissionEndToEndTests : IAsyncLifetime
{
    private AdminContractHost host = null!;
    private Guid surveyId;

    public async Task InitializeAsync()
    {
        host = await AdminContractHost.StartAsync();
        var repository = host.Services.GetRequiredService<ISurveyRepository>();
        surveyId = Guid.NewGuid();
        await repository.SaveAsync(new SurveyRecord(
            surveyId, "pub" + Guid.NewGuid().ToString("N")[..12], "検証用", PleasanterSiteId: 1,
            ResponseJsonColumn: null, Status: (int)SurveyStatus.Draft, PublishedVersion: null));
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<HttpClient> SignInAsync(string role)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var password = "contract-" + Guid.NewGuid().ToString("N");
        var store = host.Services.GetRequiredService<IAdminUserStore>();
        var hash = host.Services.GetRequiredService<PasswordHasher>().Hash(password);
        Assert.NotNull(await store.CreateAsync(loginId, hash, role, "en"));
        var client = host.CreateClient();
        using var response = await host.LoginAsync(client, new SeededAdmin(loginId, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private string Url(string suffix = "") => $"/api/admin/surveys/{surveyId}/test-responses{suffix}";

    [Theory]
    [InlineData("Administrator")]
    [InlineData("SurveyAdministrator")]
    public async Task 特権管理者とアンケート管理者は件数の取得と削除の入口を通れる(string role)
    {
        using var client = await SignInAsync(role);

        using var count = await client.GetAsync(Url());
        Assert.Equal(HttpStatusCode.OK, count.StatusCode);

        // 回答 JSON 列が無いので、権限を通っても何も消さずに 400 で断る
        using var delete = await client.PostAsJsonAsync(Url("/delete"), new { expectedCount = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Theory]
    [InlineData("Editor")]
    [InlineData("Auditor")]
    [InlineData("UserAdministrator")]
    public async Task それ以外の役割は件数の取得も削除もできない(string role)
    {
        using var client = await SignInAsync(role);

        using var count = await client.GetAsync(Url());
        using var delete = await client.PostAsJsonAsync(Url("/delete"), new { expectedCount = 0 });

        Assert.Equal(HttpStatusCode.Forbidden, count.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task 未認証は401()
    {
        using var client = host.CreateClient();

        using var count = await client.GetAsync(Url());
        using var delete = await client.PostAsJsonAsync(Url("/delete"), new { expectedCount = 0 });

        Assert.Equal(HttpStatusCode.Unauthorized, count.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }
}
