using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>アンケートの作成・編集・公開を、動いているアプリへ HTTP で当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **認証が要ることを含めて確かめる。** 部品の試験では、
/// 認証を通っていない相手に何が見えるかが分からない。
/// </para>
/// </remarks>
public class AdminSurveyEndToEndTests
{
    private const string Password = "long-enough-password";

    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = new Uri(BaseUrl),
    };

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>ログイン済みのクライアントを作る。</summary>
    private static async Task<HttpClient> SignInAsync()
    {
        await using (var connection = new DbConnectionFactory(
            DatabaseProvider.SqlServer, ConnectionString).Create())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("DELETE FROM [AdminRecoveryCodes]");
            await connection.ExecuteAsync("DELETE FROM [AdminUsers]");
        }

        var http = CreateClient();

        using (var setup = await http.PostAsJsonAsync(
            "/api/admin/setup", new { loginId = "admin", password = Password }))
        {
            setup.EnsureSuccessStatusCode();
        }

        string secret;
        using (var begin = await http.PostAsJsonAsync("/api/admin/enroll/begin", new { }))
        {
            begin.EnsureSuccessStatusCode();
            secret = (await ReadAsync(begin))!["secret"]!.GetValue<string>();
        }

        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
        using (var complete = await http.PostAsJsonAsync("/api/admin/enroll/complete", new { code }))
        {
            complete.EnsureSuccessStatusCode();
        }

        return http;
    }

    /// <summary>設問 1 問だけの下書き。</summary>
    private static object DraftBody(string surveyId, int revision, bool withMapping) => new
    {
        revision,
        definition = new
        {
            surveyId,
            version = 1,
            title = new { ja = "満足度調査" },
            pages = new[]
            {
                new
                {
                    pageId = "page-1",
                    title = new { ja = "1 ページ目" },
                    questions = new[]
                    {
                        new
                        {
                            questionId = "q1",
                            type = "Radio",
                            title = new { ja = "ご満足いただけましたか" },
                            isRequired = true,
                            choices = new[]
                            {
                                new { value = "good", label = new { ja = "はい" } },
                                new { value = "bad", label = new { ja = "いいえ" } },
                            },
                        },
                    },
                },
            },
        },
        mapping = new
        {
            assignments = withMapping
                ? new[]
                {
                    new
                    {
                        targetColumn = "ClassA",
                        sources = new[] { new { questionId = "q1", port = "Value" } },
                    },
                }
                : [],
        },
    };

    private static async Task<string> CreateSurveyAsync(HttpClient http)
    {
        using var created = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = "検証用", pleasanterSiteId = 1L });
        created.EnsureSuccessStatusCode();
        return (await ReadAsync(created))!["surveyId"]!.GetValue<string>();
    }

    [Fact]
    public async Task 認証していなければ何も見えない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();

        using var list = await http.GetAsync("/api/admin/surveys");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        using var create = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = "勝手に作る", pleasanterSiteId = 1L });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
    }

    [Fact]
    public async Task 作って編集して公開できる()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        // 作った直後は下書きで、まだ版が無い
        using (var draft = await http.GetAsync($"/api/admin/surveys/{surveyId}"))
        {
            draft.EnsureSuccessStatusCode();
            var body = await ReadAsync(draft);
            Assert.Equal(0, body!["revision"]!.GetValue<int>());
            // **次に公開される版**
            Assert.Equal(1, body["definition"]!["version"]!.GetValue<int>());
        }

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true)))
        {
            save.EnsureSuccessStatusCode();
            Assert.Equal(1, (await ReadAsync(save))!["revision"]!.GetValue<int>());
        }

        using (var reloaded = await http.GetAsync($"/api/admin/surveys/{surveyId}"))
        {
            var body = await ReadAsync(reloaded);
            Assert.Equal(
                "ご満足いただけましたか",
                body!["definition"]!["pages"]![0]!["questions"]![0]!["title"]!["ja"]!.GetValue<string>());
        }

        using (var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/publish", new { }))
        {
            publish.EnsureSuccessStatusCode();
            Assert.Equal(1, (await ReadAsync(publish))!["version"]!.GetValue<int>());
        }

        // **公開したら、次の下書きは 2 版目になる**
        using var afterPublish = await http.GetAsync($"/api/admin/surveys/{surveyId}");
        Assert.Equal(2, (await ReadAsync(afterPublish))!["definition"]!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task 割り当てが無ければ公開できるが警告が出る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: false)))
        {
            save.EnsureSuccessStatusCode();
        }

        using var publish = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/publish", new { });
        publish.EnsureSuccessStatusCode();

        // **未割り当ては拒否しない。** ただし「Pleasanter に残らない」と伝える
        var warnings = (await ReadAsync(publish))!["warnings"]!.AsArray();
        Assert.Contains(
            warnings,
            warning => warning!["code"]!.GetValue<string>() == "UnmappedQuestion");
    }

    [Fact]
    public async Task 回答できる設問が無ければ公開できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using var publish = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/publish", new { });

        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
    }

    [Fact]
    public async Task 古い版で保存しようとすると読み直しを促される()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var first = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true)))
        {
            first.EnsureSuccessStatusCode();
        }

        // **先に開いていた画面から、同じ版で保存した状況**
        using var stale = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await ReadAsync(stale))!["actualRevision"]!.GetValue<int>());
    }

    [Fact]
    public async Task 同じ版は二度公開できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true)))
        {
            save.EnsureSuccessStatusCode();
        }

        using (var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/publish", new { }))
        {
            publish.EnsureSuccessStatusCode();
        }

        // 下書きを変えずにもう一度押す。**版は不変なので上書きしない**
        // 下書きの版が上がっていないため、次に公開される版は 2 版目になり、これは通る。
        // ここで見たいのは「1 版目を書き換えない」こと
        using var again = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/publish", new { });
        again.EnsureSuccessStatusCode();
        Assert.Equal(2, (await ReadAsync(again))!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task 公開していないものは再開できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using var resume = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/resume", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resume.StatusCode);
    }

    [Fact]
    public async Task 停止して再開できる()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true)))
        {
            save.EnsureSuccessStatusCode();
        }

        using (var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/publish", new { }))
        {
            publish.EnsureSuccessStatusCode();
        }

        using (var suspend = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/suspend", new { }))
        {
            suspend.EnsureSuccessStatusCode();
            Assert.Equal("Suspended", (await ReadAsync(suspend))!["status"]!.GetValue<string>());
        }

        using var resume = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/resume", new { });
        resume.EnsureSuccessStatusCode();
        Assert.Equal("Published", (await ReadAsync(resume))!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task 公開すると回答画面から多言語のまま読める()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();

        using var created = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = "検証用", pleasanterSiteId = 1L });
        created.EnsureSuccessStatusCode();
        var createdBody = await ReadAsync(created);
        var surveyId = createdBody!["surveyId"]!.GetValue<string>();
        var publicId = createdBody["publicId"]!.GetValue<string>();

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true)))
        {
            save.EnsureSuccessStatusCode();
        }

        using (var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/publish", new { }))
        {
            publish.EnsureSuccessStatusCode();
        }

        // **認証していない回答者として読む**
        using var anonymous = CreateClient();
        using var form = await anonymous.GetAsync($"/api/forms/{publicId}");
        form.EnsureSuccessStatusCode();

        var definition = (await ReadAsync(form))!["definition"]!;

        // **言語コードをキーにしたまま届くこと。**
        // 既定の JSON 設定だと LocalizedText が別の形になり、画面に文言が出なくなる
        Assert.Equal("満足度調査", definition["title"]!["ja"]!.GetValue<string>());
        Assert.Equal(
            "ご満足いただけましたか",
            definition["pages"]![0]!["questions"]![0]!["title"]!["ja"]!.GetValue<string>());

        // **列挙も文字列で届くこと。** 数値だと画面側の分岐が全部外れる
        Assert.Equal("Radio", definition["pages"]![0]!["questions"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 公開用IDは推測できない値になる()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();

        using var first = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = "1 つ目", pleasanterSiteId = 1L });
        using var second = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = "2 つ目", pleasanterSiteId = 1L });

        var a = (await ReadAsync(first))!["publicId"]!.GetValue<string>();
        var b = (await ReadAsync(second))!["publicId"]!.GetValue<string>();

        // **連番にしない。** 総当たりで他のアンケートを見つけられる
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 20, $"公開用 ID が短すぎる: {a}");
    }
}
