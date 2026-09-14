namespace VehicleVision.PleasanterTools.Questionnaire.Mail.Tests;

/// <summary>件名と本文の差し込み（Issue #209）。</summary>
public class MailPlaceholdersTests
{
    private static readonly DateTimeOffset SubmittedAt =
        new(2026, 9, 14, 15, 4, 5, TimeSpan.FromHours(9));

    private static string Fill(string text) =>
        MailPlaceholders.Fill(text, "満足度調査", SubmittedAt);

    [Fact]
    public void 題名を差し込む()
    {
        Assert.Equal("満足度調査 へのご回答", Fill("{{title}} へのご回答"));
    }

    [Fact]
    public void 受付日時を差し込む()
    {
        // **秒は出さない。** 受け付けた時刻の精度に意味は無い
        Assert.Equal("受付: 2026-09-14 15:04", Fill("受付: {{submittedAt}}"));
    }

    [Fact]
    public void 同じ差し込みを何度でも使える()
    {
        Assert.Equal("満足度調査/満足度調査", Fill("{{title}}/{{title}}"));
    }

    [Fact]
    public void 前後の空白を許す()
    {
        Assert.Equal("満足度調査", Fill("{{ title }}"));
    }

    [Fact]
    public void 知らない差し込みはそのまま残す()
    {
        // **消すと文面の一部が黙って欠ける。** 書き間違いに気付けるほうがよい
        Assert.Equal("{{name}} 様", Fill("{{name}} 様"));
    }

    [Fact]
    public void 波括弧が1つなら差し込まない()
    {
        Assert.Equal("{title}", Fill("{title}"));
    }

    [Fact]
    public void 差し込みが無ければそのまま()
    {
        Assert.Equal("ご回答ありがとうございました。", Fill("ご回答ありがとうございました。"));
    }

    [Fact]
    public void 空文字はそのまま()
    {
        Assert.Equal(string.Empty, Fill(string.Empty));
    }

    [Fact]
    public void 差し込んだ値の中の波括弧は再解釈しない()
    {
        // **題名に {{submittedAt}} と書いてあっても、日時にはしない**
        Assert.Equal("{{submittedAt}}", MailPlaceholders.Fill("{{title}}", "{{submittedAt}}", SubmittedAt));
    }
}
