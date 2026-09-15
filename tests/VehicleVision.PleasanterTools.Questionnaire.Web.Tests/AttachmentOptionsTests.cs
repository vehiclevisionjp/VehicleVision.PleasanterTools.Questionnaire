using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class AttachmentOptionsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    [Fact]
    public void 既定ではウイルススキャンは無効()
    {
        // **導入先にスキャナが無い環境でも動くようにする**
        var options = AttachmentOptions.FromConfiguration(Configuration());

        Assert.False(options.VirusScan.Enabled);
        Assert.False(options.ToPolicy().VirusScanEnabled);
        Assert.Equal(VirusScanProvider.ClamAv, options.VirusScan.Provider);
    }

    [Fact]
    public void 設定で有効にできる()
    {
        var options = AttachmentOptions.FromConfiguration(Configuration(
            ("QUESTIONNAIRE_VIRUSSCAN_ENABLED", "true"),
            ("QUESTIONNAIRE_VIRUSSCAN_PROVIDER", "DefenderForStorage"),
            ("QUESTIONNAIRE_VIRUSSCAN_HOST", "clamav"),
            ("QUESTIONNAIRE_VIRUSSCAN_PORT", "3310")));

        Assert.True(options.VirusScan.Enabled);
        Assert.Equal(VirusScanProvider.DefenderForStorage, options.VirusScan.Provider);
        Assert.Equal("clamav", options.VirusScan.ClamAvHost);
        Assert.Equal(3310, options.VirusScan.ClamAvPort);
    }

    [Theory]
    [InlineData("QUESTIONNAIRE_VIRUSSCAN_ENABLED", "yes")]
    [InlineData("QUESTIONNAIRE_VIRUSSCAN_PROVIDER", "Clam")]
    [InlineData("QUESTIONNAIRE_VIRUSSCAN_PORT", "0")]
    [InlineData("QUESTIONNAIRE_ATTACHMENT_MAXFILESIZEBYTES", "-1")]
    [InlineData("QUESTIONNAIRE_ATTACHMENT_MAXFILECOUNT", "たくさん")]
    public void 読めない値は既定値へ倒さずに落とす(string key, string value)
    {
        // **「有効にしたつもりが無効だった」を作らない**
        Assert.Throws<InvalidOperationException>(
            () => AttachmentOptions.FromConfiguration(Configuration((key, value))));
    }

    [Fact]
    public void 許可する拡張子は設定で差し替えられる()
    {
        var options = AttachmentOptions.FromConfiguration(Configuration(
            ("QUESTIONNAIRE_ATTACHMENT_ALLOWEDEXTENSIONS", "pdf, .PNG ,csv")));

        Assert.Equal(new[] { ".pdf", ".png", ".csv" }, options.AllowedExtensions.ToArray());
    }

    [Fact]
    public void 合計の上限は設定で決まり要求本文の上限もそこから決まる()
    {
        var options = AttachmentOptions.FromConfiguration(Configuration(
            ("QUESTIONNAIRE_ATTACHMENT_MAXTOTALBYTES", "1048576")));

        Assert.Equal(1048576, options.ToPolicy().MaxTotalBytes);
        // 回答本文とヘッダの分だけ要求本文の上限は大きい
        Assert.True(options.MaxRequestBodyBytes > options.MaxTotalBytes);
    }
}
