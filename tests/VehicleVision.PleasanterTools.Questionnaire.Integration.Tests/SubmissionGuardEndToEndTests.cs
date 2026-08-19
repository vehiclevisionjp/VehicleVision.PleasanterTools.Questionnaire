using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>bot 対策を、動いているアプリへ HTTP で当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **ここで見たいのは「区別が付かないこと」。** 送信チケットの発行と bot の判定は
/// DB を見る前に済ませてあるので、**存在しない公開 ID でも実在する公開 ID でも
/// まったく同じ応答**になるはずで、それは部品の試験では確かめられない。
/// </para>
/// <para>
/// **アンケートを作らずに済ませている。** bot 対策はアンケートの実在を確かめる前に
/// 効くので、公開 ID は乱数でよい。**DB を汚さない。**
/// </para>
/// <para>
/// **枠を使い切る試験は書かない**（送信は 1 分に 20 回まで）。
/// 同じ IP から動く他の試験を巻き添えにする。
/// </para>
/// </remarks>
public class SubmissionGuardEndToEndTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseUrl) };

    /// <summary>実在しない公開 ID。**推測不能な乱数という点は本物と同じ。**</summary>
    private static string NewPublicId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>送信チケットを取る。</summary>
    private static async Task<(string ResponseToken, string Ticket, string Altcha)> IssueAsync(
        HttpClient http, string publicId, string? responseToken = null)
    {
        using var response = await http.PostAsJsonAsync(
            $"/api/forms/{publicId}/ticket", new { responseToken });
        response.EnsureSuccessStatusCode();

        var body = await ReadAsync(response);

        // **proof-of-work も解いておく**（Issue #55）。画面と同じことをする
        return (
            body!["responseToken"]!.GetValue<string>(),
            body["ticket"]!.GetValue<string>(),
            AltchaSolver.Solve(body));
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        HttpClient http,
        string publicId,
        string responseToken,
        string? ticket,
        string trap = "",
        string altcha = "") =>
        http.PutAsJsonAsync(
            $"/api/forms/{publicId}/responses/{responseToken}",
            new { answers = Array.Empty<object>(), ticket, trap, altcha });

    [Fact]
    public async Task 実在しない公開IDでもチケットは同じように出る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();

        var (token, ticket, altcha) = await IssueAsync(http, NewPublicId());

        // **サーバが決めた回答トークン。** 24 バイトを 16 進で書いた 48 文字
        Assert.Equal(48, token.Length);
        Assert.StartsWith("t1.", ticket, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端末が持っているトークンを渡すと同じものが返る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        var publicId = NewPublicId();

        var (first, _, _) = await IssueAsync(http, publicId);
        var (second, _, _) = await IssueAsync(http, publicId, first);

        // **同じ回答を指し続けられないと、編集のたびに別の回答になる**
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task 壊れたトークンを渡すとサーバが作り直す()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();

        var (token, _, _) = await IssueAsync(http, NewPublicId(), "not-a-token");

        Assert.Equal(48, token.Length);
        Assert.NotEqual("not-a-token", token);
    }

    [Fact]
    public async Task チケットが無ければ送信を断る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        var publicId = NewPublicId();
        var (token, _, _) = await IssueAsync(http, publicId);

        using var response = await SubmitAsync(http, publicId, token, ticket: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("rejected", (await ReadAsync(response))!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ハニーポットが埋まっていたら送信を断る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        var publicId = NewPublicId();
        var (token, ticket, altcha) = await IssueAsync(http, publicId);

        using var response = await SubmitAsync(http, publicId, token, ticket, trap: "http://spam", altcha: altcha);

        // **なぜ断ったかは返さない。** 返すと bot にどこを直せばよいか教えることになる
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("rejected", (await ReadAsync(response))!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task 発行した直後の送信は断る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        var publicId = NewPublicId();
        var (token, ticket, altcha) = await IssueAsync(http, publicId);

        // **人間はチケットを受け取った直後には送信できない**
        using var response = await SubmitAsync(http, publicId, token, ticket, altcha: altcha);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("rejected", (await ReadAsync(response))!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task 別のアンケートのチケットは使えない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        var (token, ticket, altcha) = await IssueAsync(http, NewPublicId());

        using var response = await SubmitAsync(http, NewPublicId(), token, ticket);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
