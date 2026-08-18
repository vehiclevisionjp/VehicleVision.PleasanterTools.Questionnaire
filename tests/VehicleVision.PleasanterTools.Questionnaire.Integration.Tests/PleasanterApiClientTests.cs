using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>実機の Pleasanter に当てる結合テスト。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 設定していない場合は何も検証せずに終わる。**緑を「通った」と読まないこと。**
/// </para>
/// <para>
/// 環境の起動は <c>docker compose --profile postgres up -d --wait</c>。
/// 手順は <c>tools/pleasanter-testenv/README.md</c>。
/// </para>
/// </remarks>
public class PleasanterApiClientTests
{
    private const string ApiKey =
        "6ce6c0fd2f4aea3c093c3ebdd7d4ea3250a132b4867d68e7d1abe35c2499664abb398d74603c2b2a38e31a21319955b3c43ede30394ead7be37ddd615c34e1f6";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_PLEASANTER_BASEURL")
            ?? "http://localhost:8080";

    private static PleasanterOptions Options() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        ApiKeyUserTimeZoneId = "Asia/Tokyo",
    };

    private static (PleasanterApiClient Client, HttpClient Http) Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return (new PleasanterApiClient(http, Options()), http);
    }

    /// <summary>検証用のサイトを作る。運用では管理者が Pleasanter 上で用意する。</summary>
    private static async Task<long> CreateSiteAsync(HttpClient http, string title)
    {
        var body = new
        {
            ApiKey,
            Title = title,
            ReferenceType = "Results",
            SiteSettings = new
            {
                Columns = new object[]
                {
                    new { ColumnName = "ClassA", LabelText = "文字" },
                    new { ColumnName = "NumA", LabelText = "数値" },
                    new { ColumnName = "DateA", LabelText = "日付" },
                    new { ColumnName = "CheckA", LabelText = "チェック" },
                    new { ColumnName = "DescriptionA", LabelText = "回答JSON" },
                },
                EditorColumnHash = new Dictionary<string, string[]>
                {
                    ["General"] = ["ClassA", "NumA", "DateA", "CheckA", "DescriptionA"],
                },
            },
        };

        using var response = await http.PostAsJsonAsync($"{BaseUrl}/api/items/0/CreateSite", body);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node?["Id"]?.GetValue<long>()
            ?? throw new InvalidOperationException("検証用サイトを作成できなかった");
    }

    [Fact]
    public async Task 回答を作成し照合し更新できる()
    {
        if (!Enabled)
        {
            return;
        }

        var (client, http) = Create();
        using var _ = http;

        var siteId = await CreateSiteAsync(http, $"結合検証 {Guid.NewGuid():N}");
        var builder = new PleasanterRecordBuilder(new PleasanterDateTime("Asia/Tokyo"));
        var token = $"tok-{Guid.NewGuid():N}";

        // --- 作成 ---
        var columns = new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ClassA"] = ["満足"],
            ["NumA"] = ["5"],
            ["DateA"] = ["2026-03-01"],
            ["CheckA"] = ["true"],
        };
        var record = builder.Build(
            columns,
            responseJsonColumn: "DescriptionA",
            responseJson: JsonSerializer.Serialize(new { token, answers = new { q1 = new[] { "5" } } }));

        Assert.Empty(record.Problems);

        var created = await client.CreateAsync(siteId, record.Body);
        Assert.True(created.IsSuccess, $"作成に失敗した: {created.StatusCode} {created.Message}");
        Assert.NotNull(created.Id);

        // --- 照合（応答不明の Create を想定） ---
        var found = await client.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        Assert.True(found.IsSuccess);
        var rows = found.Body?["Response"]?["Data"]?.AsArray();
        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(created.Id, rows[0]!["ResultId"]!.GetValue<long>());

        // --- 更新 ---
        var updatedColumns = new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ClassA"] = ["とても満足"],
            ["NumA"] = [],
        };
        var updateRecord = builder.Build(updatedColumns);
        var updated = await client.UpdateAsync(created.Id!.Value, updateRecord.Body);
        Assert.True(updated.IsSuccess, $"更新に失敗した: {updated.StatusCode} {updated.Message}");

        var after = await client.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var row = after.Body?["Response"]?["Data"]?.AsArray()?[0];
        Assert.Equal("とても満足", row?["ClassHash"]?["ClassA"]?.GetValue<string>());
    }

    [Fact]
    public async Task 存在しないトークンでは何も見つからない()
    {
        if (!Enabled)
        {
            return;
        }

        var (client, http) = Create();
        using var _ = http;

        var siteId = await CreateSiteAsync(http, $"結合検証 {Guid.NewGuid():N}");

        var found = await client.FindByResponseTokenAsync(siteId, "DescriptionA", "tok-存在しない");

        Assert.True(found.IsSuccess);
        Assert.Empty(found.Body?["Response"]?["Data"]?.AsArray() ?? []);
    }

    [Fact]
    public async Task 到達できなければ応答不明として扱う()
    {
        if (!Enabled)
        {
            return;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
        using var _ = http;
        var client = new PleasanterApiClient(http, new PleasanterOptions
        {
            BaseUrl = "http://127.0.0.1:59999",
            ApiKey = ApiKey,
            ApiKeyUserTimeZoneId = "Asia/Tokyo",
        });

        var response = await client.CreateAsync(1, new Dictionary<string, object?>());

        // **「失敗」ではなく「分からない」。照合してから判断する**
        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
    }

    [Fact]
    public async Task 権限の無いサイトは恒久的な失敗として扱う()
    {
        if (!Enabled)
        {
            return;
        }

        var (client, http) = Create();
        using var _ = http;

        var response = await client.CreateAsync(999999, new Dictionary<string, object?>());

        Assert.Equal(PleasanterErrorKind.Permanent, response.ErrorKind);
    }
}
