using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class AttachmentOptionsTests
{
    private sealed class InfectedScanner : IVirusScanner
    {
        public Task<ScanVerdict> ScanAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ScanVerdict.Infected);
    }

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

    [Theory]
    [InlineData("QUESTIONNAIRE_ASSET_MAXFILESIZEBYTES", "0")]
    [InlineData("QUESTIONNAIRE_ASSET_MAXFILECOUNT", "-1")]
    public void 配布資産の読めない値は既定値へ倒さずに落とす(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AssetOptions.FromConfiguration(
                Configuration((key, value)), virusScanEnabled: false));
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

    [Fact]
    public void 配布資産は独立した既定値と設定を持つ()
    {
        var defaults = AssetOptions.FromConfiguration(Configuration(), virusScanEnabled: false);

        Assert.Equal(
            new[] { ".pdf", ".docx", ".xlsx", ".pptx", ".png", ".jpg", ".jpeg", ".gif", ".webp" },
            defaults.AllowedExtensions);
        Assert.Equal(10 * 1024 * 1024, defaults.MaxFileSizeBytes);
        Assert.Equal(20, defaults.MaxFileCount);

        var configured = AssetOptions.FromConfiguration(Configuration(
            ("QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS", "pdf, .PNG"),
            ("QUESTIONNAIRE_ASSET_MAXFILESIZEBYTES", "2097152"),
            ("QUESTIONNAIRE_ASSET_MAXFILECOUNT", "3")), virusScanEnabled: true);

        Assert.Equal(new[] { ".pdf", ".png" }, configured.AllowedExtensions);
        Assert.Equal(2097152, configured.MaxFileSizeBytes);
        Assert.Equal(3, configured.MaxFileCount);
        Assert.True(configured.ToPolicy().VirusScanEnabled);
    }

    [Theory]
    [InlineData(".html")]
    [InlineData(".svg")]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".ps1")]
    [InlineData(".unknown")]
    public void 配布に危険または型不明の拡張子は設定できない(string extension)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AssetOptions.FromConfiguration(Configuration(
                ("QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS", extension)),
                virusScanEnabled: true));
    }

    [Fact]
    public async Task 配布資産も共通のウイルススキャナで拒否する()
    {
        var options = AssetOptions.FromConfiguration(
            Configuration(), virusScanEnabled: true);
        var inspector = new AssetInspector(options, new InfectedScanner());
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        var rejections = await inspector.InspectAsync(
            new IncomingAttachment("catalog.pdf", pdf));

        Assert.Contains(
            rejections,
            rejection => rejection.Reason == AttachmentRejectionReason.Infected);
    }

    [Fact]
    public async Task 配布資産はOffice文書のZIP署名を検査する()
    {
        var options = AssetOptions.FromConfiguration(
            Configuration(), virusScanEnabled: false);
        var inspector = new AssetInspector(options);

        var accepted = await inspector.InspectAsync(
            new IncomingAttachment(
                "catalog.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
        var rejected = await inspector.InspectAsync(
            new IncomingAttachment(
                "catalog.docx", new byte[] { 0x25, 0x50, 0x44, 0x46 }));

        Assert.Empty(accepted);
        Assert.Contains(
            rejected,
            rejection => rejection.Reason == AttachmentRejectionReason.ContentDoesNotMatchExtension);
    }

    [Theory]
    [InlineData("a\n.pdf")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf")]
    public async Task 配布資産は配信に不適切なファイル名を拒否する(string fileName)
    {
        var inspector = new AssetInspector(
            AssetOptions.FromConfiguration(Configuration(), virusScanEnabled: false));

        var rejected = await inspector.InspectAsync(
            new IncomingAttachment(fileName, new byte[] { 0x25, 0x50, 0x44, 0x46 }));

        Assert.Contains(
            rejected,
            rejection => rejection.Reason == AttachmentRejectionReason.InvalidFileName);
    }

    [Theory]
    [InlineData("catalog.pdf", "application/pdf")]
    [InlineData("spec.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("price.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    public void 配信型は拡張子から決める(string fileName, string expected)
    {
        var options = AssetOptions.FromConfiguration(Configuration(), virusScanEnabled: false);

        Assert.Equal(expected, options.ContentTypeOf(fileName));
        Assert.True(options.IsAllowed(fileName, expected));
    }

    [Fact]
    public void 画像だけを埋め込み対象と判定する()
    {
        Assert.True(ContentAsset.IsImage("image/png"));
        Assert.False(ContentAsset.IsImage("application/pdf"));
    }
}
