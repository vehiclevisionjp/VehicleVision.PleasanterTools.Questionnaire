using System.Net;
using VehicleVision.PleasanterTools.Questionnaire.Web;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>変更した管理画面パスを、動いているアプリへ HTTP で当てて確かめる。</summary>
public class AdminPathEndToEndTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static string AdminPath =>
        Environment.GetEnvironmentVariable(AdminPathOptions.Setting) ?? AdminPathOptions.DefaultPath;

    [Fact]
    public async Task 変更した入口とその配下で管理画面を返す()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };

        using var root = await http.GetAsync(AdminPath);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains(
            $"""<meta name="questionnaire-admin-path" content="{AdminPath}" """,
            await root.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var nested = await http.GetAsync($"{AdminPath}/surveys/example");
        Assert.Equal(HttpStatusCode.OK, nested.StatusCode);
    }
}
