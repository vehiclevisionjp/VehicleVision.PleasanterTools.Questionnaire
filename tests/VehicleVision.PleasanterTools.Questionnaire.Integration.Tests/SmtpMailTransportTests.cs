using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>本物の SMTP へ実際に送る（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_MAIL_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 受け側は compose の <c>mail</c> プロファイル（Mailpit）。
/// </para>
/// <code>
/// DEV_MAIL_ENABLED=true docker compose --profile mail up -d --wait
/// QUESTIONNAIRE_MAIL_INTEGRATION=1 dotnet test
/// </code>
/// <para>
/// **Mailpit は受け取るだけの行き止まり**で、外へは 1 通も出ない。
/// **受け取ったメールを HTTP API で読めるので、「送れたつもり」ではなく
/// 実際に届いた中身で確かめられる。**
/// </para>
/// </remarks>
public class SmtpMailTransportTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_MAIL_INTEGRATION") == "1";

    private static string Host =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_MAIL_SMTP_HOST") ?? "localhost";

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("QUESTIONNAIRE_MAIL_SMTP_PORT"), out var port)
            ? port
            : 1025;

    private static string ApiBase =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_MAILPIT_API") ?? "http://localhost:8025";

    private static MailOptions Options => new()
    {
        Enabled = true,
        Host = Host,
        Port = Port,
        // ⚠️ **検証環境だけ。** 本番では必ず StartTls ＋ 認証を使う
        Security = SmtpSecurity.None,
        FromAddress = "noreply@questionnaire.test",
        FromName = "アンケート",
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static SmtpMailTransport Transport(MailOptions? options = null) =>
        new(options ?? Options, NullLogger<SmtpMailTransport>.Instance);

    /// <summary>Mailpit が溜めているものを全部消す。**前の実行の残りで判定を揺らさない。**</summary>
    private static async Task ClearAsync(HttpClient client) =>
        await client.DeleteAsync(new Uri($"{ApiBase}/api/v1/messages"));

    /// <summary>いちばん新しい 1 通を読む。</summary>
    private static async Task<JsonElement> LatestAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"{ApiBase}/api/v1/messages?limit=1"));
        var id = list.GetProperty("messages")[0].GetProperty("ID").GetString();
        return await client.GetFromJsonAsync<JsonElement>(
            new Uri($"{ApiBase}/api/v1/message/{id}"));
    }

    [Fact]
    public async Task 実際に送れて中身がそのまま届く()
    {
        if (!Enabled)
        {
            return;
        }

        using var client = new HttpClient();
        await ClearAsync(client);

        await Transport().SendAsync(
            new OutgoingMail("respondent@example.test", "ご回答ありがとうございました", "受け付けました。"));

        var message = await LatestAsync(client);

        Assert.Equal(
            "respondent@example.test",
            message.GetProperty("To")[0].GetProperty("Address").GetString());
        Assert.Equal("ご回答ありがとうございました", message.GetProperty("Subject").GetString());
        Assert.Contains(
            "受け付けました。", message.GetProperty("Text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 自動生成であることが実際のヘッダに載る()
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **不在通知との無限往復を止める**（RFC 3834）。
        // 組み立てだけでなく、**実際に届いたものに載っている**ことを見る
        using var client = new HttpClient();
        await ClearAsync(client);

        await Transport().SendAsync(new OutgoingMail("a@example.test", "件名", "本文"));

        var headers = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"{ApiBase}/api/v1/message/latest/headers"));

        Assert.Equal(
            "auto-generated",
            headers.GetProperty(MailMessageFactory.AutoSubmittedHeader)[0].GetString());
    }

    [Fact]
    public async Task 差出人の表示名が化けない()
    {
        if (!Enabled)
        {
            return;
        }

        // **日本語の表示名は符号化されて送られる。** 受け側で戻ることまで見る
        using var client = new HttpClient();
        await ClearAsync(client);

        await Transport().SendAsync(new OutgoingMail("a@example.test", "件名", "本文"));

        var message = await LatestAsync(client);

        Assert.Equal("アンケート", message.GetProperty("From").GetProperty("Name").GetString());
    }

    [Fact]
    public async Task 繋がらなければ一時の失敗にする()
    {
        if (!Enabled)
        {
            return;
        }

        // **時間を置けば通る見込みがある**ので、再送する側へ倒す
        var options = Options with { Port = 1, Timeout = TimeSpan.FromSeconds(3) };

        var exception = await Assert.ThrowsAsync<MailDeliveryException>(() =>
            Transport(options).SendAsync(new OutgoingMail("a@example.test", "件名", "本文")));

        Assert.True(exception.IsTransient);
    }
}
