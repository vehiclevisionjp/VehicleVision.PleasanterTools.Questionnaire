using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Attachments;

public class AttachmentInspectorTests
{
    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static AttachmentPolicy Policy(bool scanEnabled = false) =>
        AttachmentPolicy.Create(
            allowedExtensions: ["png", ".pdf", "txt"],
            maxFileSizeBytes: 1024,
            maxFileCount: 2,
            virusScanEnabled: scanEnabled);

    private static IncomingAttachment Png(string name = "a.png") => new(name, PngHeader);

    private sealed class FakeScanner(ScanVerdict verdict) : IVirusScanner
    {
        public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken ct) =>
            Task.FromResult(verdict);
    }

    private sealed class UnavailableScanner : IVirusScanner
    {
        public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken ct) =>
            throw new VirusScannerUnavailableException("到達できない");
    }

    [Fact]
    public async Task 許可された拡張子で中身が一致していれば通る()
    {
        var rejections = await new AttachmentInspector(Policy()).InspectAsync([Png()]);

        Assert.Empty(rejections);
    }

    [Fact]
    public async Task 許可リストに無い拡張子は拒否する()
    {
        var rejections = await new AttachmentInspector(Policy())
            .InspectAsync([new IncomingAttachment("a.exe", PngHeader)]);

        Assert.Equal(AttachmentRejectionReason.ExtensionNotAllowed, rejections.Single().Reason);
    }

    [Fact]
    public async Task 拡張子を偽っていれば拒否する()
    {
        // 中身は PNG ではないのに .png と名乗っている
        var rejections = await new AttachmentInspector(Policy())
            .InspectAsync([new IncomingAttachment("a.png", new byte[] { 0x4D, 0x5A, 0x00 })]);

        Assert.Equal(
            AttachmentRejectionReason.ContentDoesNotMatchExtension,
            rejections.Single().Reason);
    }

    [Fact]
    public async Task 先頭バイトを照合できない拡張子は中身を問わない()
    {
        // .txt に決まった先頭バイトは無い。**照合できないことと安全であることは別**
        var rejections = await new AttachmentInspector(Policy())
            .InspectAsync([new IncomingAttachment("a.txt", new byte[] { 0x00, 0x01 })]);

        Assert.Empty(rejections);
    }

    [Fact]
    public async Task サイズ上限を超えたら拒否する()
    {
        var rejections = await new AttachmentInspector(Policy())
            .InspectAsync([new IncomingAttachment("a.txt", new byte[2048])]);

        Assert.Equal(AttachmentRejectionReason.TooLarge, rejections.Single().Reason);
    }

    [Fact]
    public async Task 個数上限を超えたら拒否する()
    {
        var rejections = await new AttachmentInspector(Policy())
            .InspectAsync([Png("a.png"), Png("b.png"), Png("c.png")]);

        Assert.Contains(rejections, r => r.Reason == AttachmentRejectionReason.TooMany);
    }

    [Theory]
    [InlineData("../etc/passwd.png")]
    [InlineData("")]
    public async Task ファイル名が不正なら拒否する(string fileName)
    {
        var rejections = await new AttachmentInspector(Policy())
            .InspectAsync([new IncomingAttachment(fileName, PngHeader)]);

        Assert.Contains(rejections, r => r.Reason == AttachmentRejectionReason.InvalidFileName);
    }

    [Fact]
    public async Task スキャンが有効でスキャナが無ければ通さない()
    {
        // **「スキャンできなかったので通す」にしない**
        var rejections = await new AttachmentInspector(Policy(scanEnabled: true), scanner: null)
            .InspectAsync([Png()]);

        Assert.Equal(AttachmentRejectionReason.ScannerUnavailable, rejections.Single().Reason);
    }

    [Fact]
    public async Task スキャナへ到達できなければ通さない()
    {
        var rejections = await new AttachmentInspector(Policy(scanEnabled: true), new UnavailableScanner())
            .InspectAsync([Png()]);

        Assert.Equal(AttachmentRejectionReason.ScannerUnavailable, rejections.Single().Reason);
    }

    [Fact]
    public async Task 検出したら拒否する()
    {
        var rejections = await new AttachmentInspector(
                Policy(scanEnabled: true), new FakeScanner(ScanVerdict.Infected))
            .InspectAsync([Png()]);

        Assert.Equal(AttachmentRejectionReason.Infected, rejections.Single().Reason);
    }

    [Fact]
    public async Task 検出が無ければ通る()
    {
        var rejections = await new AttachmentInspector(
                Policy(scanEnabled: true), new FakeScanner(ScanVerdict.Clean))
            .InspectAsync([Png()]);

        Assert.Empty(rejections);
    }

    [Fact]
    public async Task スキャンが無効ならスキャナを呼ばない()
    {
        // 無効なら、到達できないスキャナを渡しても通る
        var rejections = await new AttachmentInspector(Policy(scanEnabled: false), new UnavailableScanner())
            .InspectAsync([Png()]);

        Assert.Empty(rejections);
    }
}
