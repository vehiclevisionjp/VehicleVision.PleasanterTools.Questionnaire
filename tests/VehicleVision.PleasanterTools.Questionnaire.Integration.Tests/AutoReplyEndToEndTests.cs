using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 自動返信メールを、動いているアプリと本物の SMTP へ当てて確かめる（Issue #189）。
/// </summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動はこう。
/// </para>
/// <code>
/// DEV_MAIL_ENABLED=true docker compose --profile sqlserver --profile mail up -d --wait
/// </code>
/// <para>
/// **ここでしか確かめられないものがある。** 部品の試験は「送信待ちへ積んだ」までしか
/// 見ておらず、**受け付け → 積む → ワーカーが送る → 実際に届く**の通し（4 つの部品と
/// 常駐のワーカーをまたぐ）は、アプリを起こさないと通らない。
/// </para>
/// <para>
/// **Mailpit は受け取るだけの行き止まり。** 外へは 1 通も出ない。
/// </para>
/// </remarks>
public class AutoReplyEndToEndTests
{
    private const string Password = "long-enough-password";

    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    /// <summary>メールの受けが立っているときだけ走る。</summary>
    /// <remarks>**旗を分けてある。** DB は要らず、メールの受けだけが要る試験のため。</remarks>
    private static bool MailEnabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_MAIL_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static string MailpitApi =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_MAILPIT_API") ?? "http://localhost:8025";

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

    /// <summary>メールアドレスを訊く 1 問だけの定義。</summary>
    /// <remarks>
    /// ⚠️ **自動返信は「メールアドレス形式の記述式」しか宛先にできない**（Issue #189）。
    /// </remarks>
    private static object DraftBody(string surveyId, bool includeAnswers) => new
    {
        revision = 0,
        definition = new
        {
            surveyId,
            version = 1,
            title = new { ja = "満足度調査" },
            displayMode = "Paged",
            showProgress = false,
            allowEditingAfterSubmit = true,
            autoReply = new
            {
                enabled = true,
                toQuestionId = "q-mail",
                subject = new { ja = "ご回答ありがとうございました" },
                body = new { ja = "受け付けました。" },
                includeAnswers,
            },
            pages = new[]
            {
                new
                {
                    pageId = "page-1",
                    questions = new object[]
                    {
                        new
                        {
                            questionId = "q-mail",
                            type = "Text",
                            title = new { ja = "メールアドレス" },
                            isRequired = true,
                            choices = Array.Empty<object>(),
                            settings = new { format = "Email" },
                        },
                        new
                        {
                            questionId = "q-free",
                            type = "Paragraph",
                            title = new { ja = "ご意見" },
                            isRequired = false,
                            choices = Array.Empty<object>(),
                            settings = new { maxLength = 500 },
                        },
                    },
                },
            },
        },
        mapping = new { assignments = Array.Empty<object>() },
    };

    /// <summary>自動返信つきのアンケートを 1 本公開する。</summary>
    private static async Task<string> PublishAsync(HttpClient http, bool includeAnswers)
    {
        string surveyId;
        string publicId;
        using (var created = await http.PostAsJsonAsync(
            "/api/admin/surveys", new { title = "自動返信の検証", pleasanterSiteId = 1L }))
        {
            created.EnsureSuccessStatusCode();
            var body = await ReadAsync(created);
            surveyId = body!["surveyId"]!.GetValue<string>();

            // **公開用 ID は作ったときに返る。** 一覧を引き直さない
            publicId = body["publicId"]!.GetValue<string>();
        }

        using (var saved = await http.PutAsJsonAsync(
            $"/api/admin/surveys/{surveyId}", DraftBody(surveyId, includeAnswers)))
        {
            saved.EnsureSuccessStatusCode();
        }

        // **下書きから直接は公開できない。** テスト公開を経由する（Issue #223）
        using (var testPublished = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/test-publish", new { }))
        {
            testPublished.EnsureSuccessStatusCode();
        }

        using (var published = await http.PostAsJsonAsync(
            $"/api/admin/surveys/{surveyId}/publish", new { }))
        {
            // **自動返信の不備は公開のときに弾かれる。** 通ることまで含めて確かめる
            published.EnsureSuccessStatusCode();
        }

        return publicId;
    }

    /// <summary>回答を 1 件送る。</summary>
    private static async Task SubmitAsync(string publicId, string mailAddress, string opinion)
    {
        using var http = CreateClient();

        string responseToken;
        string ticket;
        string altcha;

        using (var issued = await http.PostAsJsonAsync(
            $"/api/forms/{publicId}/ticket", new { responseToken = (string?)null }))
        {
            issued.EnsureSuccessStatusCode();
            var body = await ReadAsync(issued);
            responseToken = body!["responseToken"]!.GetValue<string>();
            ticket = body["ticket"]!.GetValue<string>();

            // **proof-of-work も解く**（Issue #55）。画面と同じことをする
            altcha = AltchaSolver.Solve(body);
        }

        // ⚠️ **投稿までの最短時間がある**（既定 3 秒。SubmissionGuard）。
        // **待たずに送ると bot として弾かれる**
        await Task.Delay(TimeSpan.FromSeconds(4));

        using var submitted = await http.PutAsJsonAsync(
            $"/api/forms/{publicId}/responses/{responseToken}",
            new
            {
                answers = new object[]
                {
                    new { questionId = "q-mail", values = new[] { mailAddress } },
                    new { questionId = "q-free", values = new[] { opinion } },
                },
                ticket,
                trap = string.Empty,
                altcha,
            });

        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
    }

    /// <summary>Mailpit が溜めているものを全部消す。</summary>
    private static async Task ClearMailAsync(HttpClient client) =>
        await client.DeleteAsync(new Uri($"{MailpitApi}/api/v1/messages"));

    /// <summary>その宛先に届くまで待つ。**届かなければ <c>null</c>。**</summary>
    /// <remarks>
    /// ⚠️ **送るのは常駐のワーカー。** 受け付けた瞬間には届いていないので、
    /// **待つのは「非同期で送る」という設計どおりの姿**であって、試験の都合ではない。
    /// </remarks>
    private static async Task<JsonNode?> WaitForMailAsync(HttpClient client, string toAddress)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var response = await client.GetAsync(
                new Uri($"{MailpitApi}/api/v1/search?query={Uri.EscapeDataString("to:" + toAddress)}"));

            if (response.IsSuccessStatusCode)
            {
                var found = (await ReadAsync(response))!["messages"]?.AsArray();
                if (found is { Count: > 0 })
                {
                    var id = found[0]!["ID"]!.GetValue<string>();
                    using var message = await client.GetAsync(
                        new Uri($"{MailpitApi}/api/v1/message/{id}"));
                    message.EnsureSuccessStatusCode();
                    return await ReadAsync(message);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return null;
    }

    [Fact]
    public async Task 回答すると自動返信が実際に届く()
    {
        if (!Enabled || !MailEnabled)
        {
            return;
        }

        using var mailpit = new HttpClient();
        await ClearMailAsync(mailpit);

        using var http = await SignInAsync();
        var publicId = await PublishAsync(http, includeAnswers: false);

        var address = $"respondent-{Guid.NewGuid():N}@example.test";
        await SubmitAsync(publicId, address, "よかった");

        var mail = await WaitForMailAsync(mailpit, address);

        Assert.NotNull(mail);
        Assert.Equal("ご回答ありがとうございました", mail["Subject"]!.GetValue<string>());
        Assert.Contains("受け付けました。", mail["Text"]!.GetValue<string>(), StringComparison.Ordinal);

        // **写しを付けない設定なので、回答の中身は載らない**
        Assert.DoesNotContain("よかった", mail["Text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写しを付ける設定なら回答が載る()
    {
        if (!Enabled || !MailEnabled)
        {
            return;
        }

        using var mailpit = new HttpClient();
        await ClearMailAsync(mailpit);

        using var http = await SignInAsync();
        var publicId = await PublishAsync(http, includeAnswers: true);

        var address = $"respondent-{Guid.NewGuid():N}@example.test";
        await SubmitAsync(publicId, address, "とてもよかった");

        var mail = await WaitForMailAsync(mailpit, address);

        Assert.NotNull(mail);
        Assert.Contains(
            "ご意見: とてもよかった", mail["Text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 自動生成であることが実際のヘッダに載る()
    {
        if (!Enabled || !MailEnabled)
        {
            return;
        }

        // ⚠️ **不在通知との無限往復を止める**（RFC 3834）。
        // 組み立てだけでなく、**通しで送ったものに載っている**ことを見る
        using var mailpit = new HttpClient();
        await ClearMailAsync(mailpit);

        using var http = await SignInAsync();
        var publicId = await PublishAsync(http, includeAnswers: false);

        var address = $"respondent-{Guid.NewGuid():N}@example.test";
        await SubmitAsync(publicId, address, "ふつう");

        var mail = await WaitForMailAsync(mailpit, address);

        Assert.NotNull(mail);

        using var headers = await mailpit.GetAsync(
            new Uri($"{MailpitApi}/api/v1/message/{mail["ID"]!.GetValue<string>()}/headers"));
        headers.EnsureSuccessStatusCode();

        Assert.Equal(
            "auto-generated",
            (await ReadAsync(headers))!["Auto-Submitted"]![0]!.GetValue<string>());
    }
}
