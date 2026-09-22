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

        string next;
        using (var setup = await http.PostAsJsonAsync(
            "/api/admin/setup", new { loginId = "admin", password = Password }))
        {
            setup.EnsureSuccessStatusCode();
            next = await AdminTwoFactorE2E.NextOfAsync(setup);
        }

        // ⚠️ **2 要素の設定を決め打ちにしない**（Issue #174）
        await AdminTwoFactorE2E.CompleteIfRequiredAsync(http, next);

        return http;
    }

    /// <summary>設問 1 問だけの下書き。</summary>
    private static object DraftBody(
        string surveyId,
        int revision,
        bool withMapping,
        long assetHistorySiteId = 0,
        bool withAssetHistoryMapping = false) => new
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
        assetHistorySiteId,
        assetHistoryMapping = new
        {
            assignments = withAssetHistoryMapping
                ? new[]
                {
                    new
                    {
                        targetColumn = "ClassB",
                        sources = new[]
                        {
                            new
                            {
                                questionId = "",
                                port = "Value",
                                systemValue = "ReferenceId",
                            },
                        },
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

        Assert.Equal(1, (await PublishAsync(http, surveyId))!["version"]!.GetValue<int>());

        // **公開したら、次の下書きは 2 版目になる**
        using var afterPublish = await http.GetAsync($"/api/admin/surveys/{surveyId}");
        Assert.Equal(2, (await ReadAsync(afterPublish))!["definition"]!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task アーカイブと題名を確認して完全削除し削除前の値を監査ログへ残す()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var beforeArchive = await http.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/surveys/{surveyId}/delete")
        {
            Content = JsonContent.Create(new { title = "検証用" }),
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, beforeArchive.StatusCode);
        }

        using (var archive = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/archive", new { }))
        {
            archive.EnsureSuccessStatusCode();
        }

        using (var wrongTitle = await http.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/surveys/{surveyId}/delete")
        {
            Content = JsonContent.Create(new { title = "違う題名" }),
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongTitle.StatusCode);
        }

        using (var deleted = await http.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/surveys/{surveyId}/delete")
        {
            Content = JsonContent.Create(new { title = "検証用" }),
        }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var missing = await http.GetAsync($"/api/admin/surveys/{surveyId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        await using var connection = new DbConnectionFactory(
            DatabaseProvider.SqlServer, ConnectionString).Create();
        await connection.OpenAsync();
        // ⚠️ **1 行に絞らない。** 断られた 2 回ぶんも記録に残る
        // （経路の値から TargetId が入るため、成功したものと同じ条件で当たる）。
        // **見たいのは消えたときの 1 行**なので、値を載せている行を選ぶ
        var details = (await connection.QueryAsync<string>(
            "SELECT [DetailJson] FROM [AuditLogs] "
            + "WHERE [Action] = @Action AND [TargetId] = @TargetId",
            new
            {
                Action = "POST /api/admin/surveys/{surveyId}/delete",
                TargetId = surveyId.ToString(),
            })).ToList();

        var detail = Assert.Single(
            details,
            json => json.Contains("\"publicId\"", StringComparison.Ordinal));

        Assert.Contains("\"surveyId\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"publicId\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"検証用\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"pleasanterSiteId\":\"1\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"responseCount\":\"0\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"archivedAt\"", detail, StringComparison.Ordinal);
    }

    /// <summary>埋め込みを 1 つ持つ下書き（Issue #104 / #107）。</summary>
    private static object EmbedDraftBody(string surveyId, int revision, string url) => new
    {
        revision,
        definition = new
        {
            surveyId,
            version = 1,
            title = new { ja = "埋め込みの検証" },
            pages = new[]
            {
                new
                {
                    pageId = "page-1",
                    questions = new object[]
                    {
                        new
                        {
                            questionId = "e1",
                            type = "Embed",
                            title = new { ja = "会社のロゴ" },
                            settings = new
                            {
                                embed = new { kind = "Image", url },
                            },
                        },
                        new
                        {
                            questionId = "q1",
                            type = "Text",
                            title = new { ja = "ご意見" },
                        },
                    },
                },
            },
        },
        mapping = new { assignments = Array.Empty<object>() },
    };

    [Fact]
    public async Task 許した配信元の埋め込みは保存できる()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}",
            EmbedDraftBody(surveyId, 0, "https://www.example.com/logo.png"));

        save.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task 許していない配信元の埋め込みは保存できない()
    {
        if (!Enabled)
        {
            return;
        }

        // **管理者が任意のホストを書けると、実質 `frame-src https:` と変わらない。**
        // **公開のときではなく保存のときに断る**（直しようのある不備ではないため）
        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}",
            EmbedDraftBody(surveyId, 0, "https://evil.test/logo.png"));

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        var body = await ReadAsync(save);
        Assert.Equal("e1", body!["fields"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task 許した配信元でもhttpsでなければ保存できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}",
            EmbedDraftBody(surveyId, 0, "http://www.example.com/logo.png"));

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
    }

    [Fact]
    public async Task 許した配信元が応答のCSPに載る()
    {
        if (!Enabled)
        {
            return;
        }

        // **アンケートごとには出し分けない。**
        // 出し分けると、存在する公開 ID と存在しない公開 ID でヘッダが変わり、
        // **実在が漏れる**（`_documents/非機能設計.md` 1 章）
        using var http = CreateClient();

        using var response = await http.GetAsync("/");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("frame-src https://www.example.com https://*.example.net", csp);
        Assert.Contains("img-src 'self' data: https://www.example.com https://*.example.net", csp);
        Assert.Contains(
            "frame-ancestors https://localhost:9443 https://www.parent.example.com https://*.parent.example.net",
            csp);
    }

    [Fact]
    public async Task 許した配信元は管理画面から読める()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();

        using var response = await http.GetAsync("/api/admin/surveys/embed-options");
        response.EnsureSuccessStatusCode();

        var body = await ReadAsync(response);
        Assert.True(body!["enabled"]!.GetValue<bool>());
        Assert.Equal("www.example.com", body["allowedHosts"]![0]!.GetValue<string>());
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

        // **未割り当ては拒否しない。** ただし「Pleasanter に残らない」と伝える
        var warnings = (await PublishAsync(http, surveyId))!["warnings"]!.AsArray();
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

        // **弾くのはテスト公開のとき。** 版を固める前に止める
        using var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { });

        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
    }

    [Fact]
    public async Task 履歴投射先が回答先と同じなら公開できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}",
            DraftBody(
                surveyId,
                0,
                withMapping: true,
                assetHistorySiteId: 1,
                withAssetHistoryMapping: true)))
        {
            save.EnsureSuccessStatusCode();
        }

        using var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { });

        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        var body = await ReadAsync(publish);
        Assert.Contains(
            body!["problems"]!.AsArray(),
            problem => problem!["detail"]!.GetValue<string>() == "assetHistorySiteIdMustDiffer");
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

        // **版を固めるのはテスト公開の側**（Issue #223）。ここで 1 版目ができる
        using (var first = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { }))
        {
            first.EnsureSuccessStatusCode();
            Assert.Equal(1, (await ReadAsync(first))!["version"]!.GetValue<int>());
        }

        // 下書きへ戻して、変えずにもう一度固める。**版は不変なので上書きしない**
        // 下書きの版が上がっていないため、次に固まる版は 2 版目になり、これは通る。
        // ここで見たいのは「1 版目を書き換えない」こと
        using (var reverted = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/revert-to-draft", new { }))
        {
            reverted.EnsureSuccessStatusCode();
        }

        using var again = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { });
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

        await PublishAsync(http, surveyId);

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

        await PublishAsync(http, surveyId);

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
    public async Task 埋め込みを許可していないアンケートは枠内申告付きで読めない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await PublishAsync(http);
        var publicId = (await SummaryAsync(http, surveyId))["publicId"]!.GetValue<string>();

        using var anonymous = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/forms/{publicId}");
        request.Headers.Add("X-Questionnaire-Framed", "1");
        using var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "embeddingNotAllowed",
            (await ReadAsync(response))!["reason"]!.GetValue<string>());

        using (var settings = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings",
            new { responseLimit = (int?)null, allowEmbedding = true }))
        {
            settings.EnsureSuccessStatusCode();
        }

        using (var unchanged = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings",
            new { responseLimit = 10 }))
        {
            unchanged.EnsureSuccessStatusCode();
        }
        Assert.True((await SummaryAsync(http, surveyId))["allowEmbedding"]!.GetValue<bool>());

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/forms/{publicId}");
        allowedRequest.Headers.Add("X-Questionnaire-Framed", "1");
        using var allowed = await anonymous.SendAsync(allowedRequest);
        allowed.EnsureSuccessStatusCode();
    }

    /// <summary>公開済みのアンケートを 1 つ用意する。</summary>
    private static async Task<string> PublishAsync(HttpClient http)
    {
        var surveyId = await CreateSurveyAsync(http);

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, 0, withMapping: true)))
        {
            save.EnsureSuccessStatusCode();
        }

        await PublishAsync(http, surveyId);

        return surveyId;
    }

    /// <summary>下書きを公開する。**テスト公開を経由する**（Issue #223）。</summary>
    /// <remarks>
    /// ⚠️ **下書きから直接は公開できない。** 版を固めるのも検査を通すのも
    /// テスト公開の側なので、**版や警告はそちらの応答に入る。**
    /// </remarks>
    private static async Task<JsonNode?> PublishAsync(HttpClient http, string surveyId)
    {
        JsonNode? body;
        using (var testPublished = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { }))
        {
            testPublished.EnsureSuccessStatusCode();
            body = await ReadAsync(testPublished);
        }

        using var published = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/publish", new { });
        published.EnsureSuccessStatusCode();

        return body;
    }

    /// <summary>一覧からその 1 行を読む。</summary>
    private static async Task<JsonNode> SummaryAsync(HttpClient http, string surveyId)
    {
        using var list = await http.GetAsync("/api/admin/surveys");
        list.EnsureSuccessStatusCode();

        return (await ReadAsync(list))!["items"]!.AsArray()
            .Single(row => row!["surveyId"]!.GetValue<string>() == surveyId)!;
    }

    [Fact]
    public async Task 回答数の上限を編集できる()
    {
        if (!Enabled)
        {
            return;
        }

        // **列はあったが、どこからも書けなかった**（Issue #53）
        using var http = await SignInAsync();
        var surveyId = await PublishAsync(http);

        using (var settings = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings", new { responseLimit = 100 }))
        {
            settings.EnsureSuccessStatusCode();
        }

        var summary = await SummaryAsync(http, surveyId);
        Assert.Equal(100, summary["responseLimit"]!.GetValue<int>());
        Assert.Equal(0, summary["responseCount"]!.GetValue<int>());

        // **空にすれば上限なしへ戻せる**
        using (var cleared = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings", new { responseLimit = (int?)null }))
        {
            cleared.EnsureSuccessStatusCode();
        }

        Assert.Null((await SummaryAsync(http, surveyId))["responseLimit"]);
    }

    [Fact]
    public async Task 上限に0以下は指定できない()
    {
        if (!Enabled)
        {
            return;
        }

        // **0 を保存させない。** 公開しているのに誰も回答できないアンケートができる
        using var http = await SignInAsync();
        var surveyId = await PublishAsync(http);

        using var settings = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings", new { responseLimit = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, settings.StatusCode);
    }

    [Fact]
    public async Task 手で止めると理由が一覧に出る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await PublishAsync(http);

        using (var suspend = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/suspend", new { }))
        {
            suspend.EnsureSuccessStatusCode();
        }

        var suspended = await SummaryAsync(http, surveyId);
        Assert.Equal(
            (int)SurveySuspendedReason.Manual, suspended["suspendedReason"]!.GetValue<int>());
        Assert.NotNull(suspended["suspendedAt"]);

        using (var resume = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/resume", new { }))
        {
            resume.EnsureSuccessStatusCode();
        }

        // **再開したら理由を消す。** 動いているものに停止の理由が残らないこと
        Assert.Null((await SummaryAsync(http, surveyId))["suspendedReason"]);
    }

    [Fact]
    public async Task 上限に達したままでは再開できない()
    {
        if (!Enabled)
        {
            return;
        }

        // **再開しても最初の回答で再び自動停止するだけ。**
        // 押した人には「再開できたのに止まっている」としか見えない
        using var http = await SignInAsync();
        var surveyId = await PublishAsync(http);

        using (var settings = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings", new { responseLimit = 1 }))
        {
            settings.EnsureSuccessStatusCode();
        }

        // **受け付けた回答の代わりに、対応表へ直に 1 行入れる。**
        // 回答の送信は最短時間の判定を挟むので、ここで確かめたいこととは関係ない待ちが増える
        var tokens = new ResponseTokenStore(
            new DbConnectionFactory(DatabaseProvider.SqlServer, ConnectionString));
        await tokens.EnsureAsync($"tok-{Guid.NewGuid():N}", Guid.Parse(surveyId));

        using (var suspend = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/suspend", new { }))
        {
            suspend.EnsureSuccessStatusCode();
        }

        using var resume = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/resume", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resume.StatusCode);

        // **先に上限を引き上げれば再開できる**
        using (var raised = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/settings", new { responseLimit = 2 }))
        {
            raised.EnsureSuccessStatusCode();
        }

        using var again = await http.PostAsJsonAsync($"/api/admin/surveys/{surveyId}/resume", new { });
        again.EnsureSuccessStatusCode();
    }

    /// <summary>添付の設問 1 つを添付列へ繋ぐ下書き。</summary>
    private static object AttachmentDraftBody(string surveyId, int revision, string port) => new
    {
        revision,
        definition = new
        {
            surveyId,
            version = 1,
            title = new { ja = "添付の割り当て" },
            pages = new[]
            {
                new
                {
                    pageId = "page-1",
                    questions = new[]
                    {
                        new
                        {
                            questionId = "qf",
                            type = "File",
                            title = new { ja = "資料" },
                            isRequired = false,
                            choices = Array.Empty<object>(),
                        },
                    },
                },
            },
        },
        mapping = new
        {
            assignments = new[]
            {
                new
                {
                    targetColumn = "AttachmentsA",
                    sources = new[] { new { questionId = "qf", port } },
                    converter = (object?)null,
                },
            },
        },
    };

    [Fact]
    public async Task 添付の割り当ては不備なく保存できる()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", AttachmentDraftBody(surveyId, 0, "Files")))
        {
            save.EnsureSuccessStatusCode();
        }

        using var problems = await http.GetAsync($"/api/admin/surveys/{surveyId}/problems");
        problems.EnsureSuccessStatusCode();

        Assert.Empty((await ReadAsync(problems))!.AsArray());
    }

    [Fact]
    public async Task 添付列に名前だけを繋ぐと公開できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        // 名前だけを添付列へ入れても、ファイルとしては取り出せない
        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", AttachmentDraftBody(surveyId, 0, "FileNames")))
        {
            // **直している途中でも保存はできる**
            save.EnsureSuccessStatusCode();
        }

        using (var problems = await http.GetAsync($"/api/admin/surveys/{surveyId}/problems"))
        {
            var found = (await ReadAsync(problems))!.AsArray();
            Assert.Contains(
                found,
                problem => problem!["code"]!.GetValue<string>() == "AttachmentColumnNeedsFilePort");
        }

        // **拒否するのはテスト公開のときだけ**
        using var publish = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { });
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
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

    /// <summary>
    /// 一覧が**全件を返さない**こと（Issue #79）。増え続ける表なので、
    /// 上限と絞り込みが無いと開けなくなる。
    /// </summary>
    [Fact]
    public async Task 一覧は件数を絞ってページ送りできる()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();

        for (var index = 0; index < 3; index++)
        {
            using var created = await http.PostAsJsonAsync(
                "/api/admin/surveys",
                new { title = $"ページ送りの検証 {index}", pleasanterSiteId = 1L });
            created.EnsureSuccessStatusCode();
        }

        using (var first = await http.GetAsync("/api/admin/surveys?limit=1"))
        {
            first.EnsureSuccessStatusCode();
            var body = (await ReadAsync(first))!;

            Assert.Single(body["items"]!.AsArray());
            // **総数は数えていない。** 1 件多く読んで、余ったかどうかだけを見る
            Assert.True(body["hasMore"]!.GetValue<bool>());
        }

        // **読み飛ばした先も同じ形で返る**
        using var second = await http.GetAsync("/api/admin/surveys?limit=1&offset=1");
        second.EnsureSuccessStatusCode();
        Assert.Single((await ReadAsync(second))!["items"]!.AsArray());
    }

    /// <summary>題名と状態で絞り込めること（Issue #79）。</summary>
    [Fact]
    public async Task 一覧は題名と状態で絞り込める()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var marker = Guid.NewGuid().ToString("N")[..8];

        using (var created = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = $"絞り込み_{marker}", pleasanterSiteId = 1L }))
        {
            created.EnsureSuccessStatusCode();
        }

        using (var filtered = await http.GetAsync($"/api/admin/surveys?title={marker}"))
        {
            filtered.EnsureSuccessStatusCode();
            var items = (await ReadAsync(filtered))!["items"]!.AsArray();

            Assert.Single(items);
            Assert.Contains(marker, items[0]!["title"]!.GetValue<string>());
        }

        // **作ったばかりは下書き。** 公開中で絞れば出てこない
        using (var published = await http.GetAsync(
            $"/api/admin/surveys?title={marker}&status=1"))
        {
            published.EnsureSuccessStatusCode();
            Assert.Empty((await ReadAsync(published))!["items"]!.AsArray());
        }

        // **知らない状態は断る。** 黙って 0 件にすると「消えた」と見える
        using var unknown = await http.GetAsync("/api/admin/surveys?status=99");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task 選んだ設問を安全な写しとして取り込める()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var sourceId = await CreateSurveyAsync(http);
        var targetId = await CreateSurveyAsync(http);
        var sourceDraft = new
        {
            revision = 0,
            definition = new
            {
                surveyId = sourceId,
                version = 1,
                title = new { ja = "取り込み元" },
                pages = new[]
                {
                    new
                    {
                        pageId = "page-source",
                        questions = new object[]
                        {
                            new
                            {
                                questionId = "q-branch",
                                type = "Radio",
                                title = new { ja = "分岐する設問" },
                                description = new
                                {
                                    ja = "[資料](asset:11111111-1111-4111-8111-111111111111)",
                                },
                                isRequired = false,
                                choices = new[]
                                {
                                    new
                                    {
                                        value = "yes",
                                        label = new { ja = "はい" },
                                        next = new { kind = "Submit" },
                                    },
                                },
                                settings = new { descriptionFormat = "Markup" },
                            },
                            new
                            {
                                questionId = "q-condition",
                                type = "Text",
                                title = new { ja = "条件付き設問" },
                                isRequired = false,
                                choices = Array.Empty<object>(),
                                visibleWhen = new
                                {
                                    match = "All",
                                    rules = new[]
                                    {
                                        new
                                        {
                                            questionId = "q-branch",
                                            @operator = "Equals",
                                            value = "yes",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            mapping = new
            {
                assignments = new[]
                {
                    new
                    {
                        targetColumn = "ClassA",
                        sources = new[] { new { questionId = "q-branch", port = "Value" } },
                    },
                },
            },
        };

        using (var save = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{sourceId}", sourceDraft))
        {
            save.EnsureSuccessStatusCode();
        }

        using (var candidates = await http.GetAsync(
            $"/api/admin/surveys/{targetId}/question-import/{sourceId}"))
        {
            candidates.EnsureSuccessStatusCode();
            Assert.Equal(
                2,
                (await ReadAsync(candidates))!["pages"]![0]!["questions"]!.AsArray().Count);
        }

        using var importedResponse = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{targetId}/question-import/{sourceId}",
            new
            {
                questionIds = new[] { "q-branch", "q-condition" },
                existingQuestionIds = new[] { "q-existing" },
            });
        importedResponse.EnsureSuccessStatusCode();
        var imported = (await ReadAsync(importedResponse))!;
        var questions = imported["questions"]!.AsArray();

        Assert.Equal(2, questions.Count);
        Assert.All(questions, question =>
        {
            Assert.NotEqual("q-branch", question!["questionId"]!.GetValue<string>());
            Assert.NotEqual("q-condition", question["questionId"]!.GetValue<string>());
            Assert.NotEqual("q-existing", question["questionId"]!.GetValue<string>());
            Assert.Null(question["visibleWhen"]);
        });
        Assert.Null(questions[0]!["choices"]![0]!["next"]);
        Assert.Equal("資料", questions[0]!["description"]!["ja"]!.GetValue<string>());
        Assert.Equal(1, imported["removedChoiceTransitions"]!.GetValue<int>());
        Assert.Equal(1, imported["removedVisibilityConditions"]!.GetValue<int>());
        Assert.Equal(1, imported["removedAssetReferences"]!.GetValue<int>());
        Assert.Null(imported["mapping"]);
    }

    /// <summary>
    /// 複製が下書きとして作られ、**公開用 ID を使い回さない**こと（Issue #46）。
    /// </summary>
    [Fact]
    public async Task 複製すると別の公開用IDを持つ下書きができる()
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

        using var original = await http.GetAsync("/api/admin/surveys");
        var originalPublicId = (await ReadAsync(original))!["items"]!.AsArray()
            .Single(row => row!["surveyId"]!.GetValue<string>() == surveyId)!["publicId"]!
            .GetValue<string>();

        string copyId;
        using (var duplicate = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/duplicate", new { pleasanterSiteId = 2L }))
        {
            Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
            var body = await ReadAsync(duplicate);
            copyId = body!["surveyId"]!.GetValue<string>();

            // **使い回さない**（_documents/データモデル設計.md 3 章）
            Assert.NotEqual(originalPublicId, body["publicId"]!.GetValue<string>());
        }

        Assert.NotEqual(surveyId, copyId);

        using (var draft = await http.GetAsync($"/api/admin/surveys/{copyId}"))
        {
            draft.EnsureSuccessStatusCode();
            var body = await ReadAsync(draft);
            Assert.Equal(0, body!["revision"]!.GetValue<int>());
            Assert.Equal("満足度調査のコピー", body["definition"]!["title"]!["ja"]!.GetValue<string>());
            // **マッピングも写る**
            Assert.Single(body["mapping"]!["assignments"]!.AsArray());
        }

        using (var list = await http.GetAsync("/api/admin/surveys"))
        {
            var copy = (await ReadAsync(list))!["items"]!.AsArray()
                .Single(row => row!["surveyId"]!.GetValue<string>() == copyId)!;

            // **公開状態も公開済みの版も写さない**
            Assert.Equal(0, copy["status"]!.GetValue<int>());
            Assert.Null(copy["publishedVersion"]);
            Assert.Equal(2L, copy["pleasanterSiteId"]!.GetValue<long>());
        }
    }

    /// <summary>
    /// **1 アンケート = 1 サイト。** 元と同じサイトを指定させない（Issue #46）。
    /// </summary>
    [Fact]
    public async Task 複製先に元と同じサイトは指定できない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignInAsync();
        var surveyId = await CreateSurveyAsync(http);

        // 元は pleasanterSiteId = 1
        using var same = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/duplicate", new { pleasanterSiteId = 1L });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        using var missing = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/duplicate", new { pleasanterSiteId = 0L });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // **無いアンケートは複製できない**
        using var unknown = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{Guid.NewGuid()}/duplicate", new { pleasanterSiteId = 2L });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
