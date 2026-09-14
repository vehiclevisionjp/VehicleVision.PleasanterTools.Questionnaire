using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Mail.Tests;

public class MailOptionsTests
{
    private static MailOptions Read(params (string Key, string Value)[] settings) =>
        MailOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    private static (string, string)[] Minimum =>
    [
        ("QUESTIONNAIRE_MAIL_ENABLED", "true"),
        ("QUESTIONNAIRE_MAIL_SMTP_HOST", "smtp.example.test"),
        ("QUESTIONNAIRE_MAIL_FROM_ADDRESS", "noreply@example.test"),
    ];

    [Fact]
    public void 何も設定しなければ無効()
    {
        var options = Read();

        Assert.False(options.Enabled);
        Assert.False(options.IsReady);
    }

    [Fact]
    public void 無効なら他の設定の不備で落とさない()
    {
        // **使わない設定の不備で起動を止めない。** ホストが空でも例外にしない
        var options = Read(("QUESTIONNAIRE_MAIL_ENABLED", "false"), ("QUESTIONNAIRE_MAIL_SMTP_PORT", "これは整数ではない"));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 既定のポートは587()
    {
        // **25 番にしない。** Azure は 25 番の外向きを塞ぐ
        Assert.Equal(587, Read(Minimum).Port);
    }

    [Fact]
    public void 既定の暗号化はStartTls()
    {
        Assert.Equal(SmtpSecurity.StartTls, Read(Minimum).Security);
    }

    [Fact]
    public void 有効なのにホストが無ければ落とす()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read(
            ("QUESTIONNAIRE_MAIL_ENABLED", "true"),
            ("QUESTIONNAIRE_MAIL_FROM_ADDRESS", "noreply@example.test")));

        Assert.Contains("SMTP_HOST", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 有効なのに差出人が無ければ落とす()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read(
            ("QUESTIONNAIRE_MAIL_ENABLED", "true"),
            ("QUESTIONNAIRE_MAIL_SMTP_HOST", "smtp.example.test")));

        Assert.Contains("FROM_ADDRESS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    public void 範囲外のポートは落とす(string port)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Read([.. Minimum, ("QUESTIONNAIRE_MAIL_SMTP_PORT", port)]));
    }

    [Fact]
    public void 読めない値を黙って既定へ落とさない()
    {
        // **設定したつもりが効いていない状態を作らない**（ResponseSenderOptions と同じ）
        Assert.Throws<InvalidOperationException>(() =>
            Read([.. Minimum, ("QUESTIONNAIRE_MAIL_SMTP_SECURITY", "tls1.2")]));
    }

    [Fact]
    public void 暗号化の指定は大文字小文字を問わない()
    {
        Assert.Equal(
            SmtpSecurity.ImplicitTls,
            Read([.. Minimum, ("QUESTIONNAIRE_MAIL_SMTP_SECURITY", "implicittls")]).Security);
    }
}
