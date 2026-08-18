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

    [Fact]
    public async Task 送信全体の合計が上限を超えたら拒否する()
    {
        // **1 件あたりの上限だけでは足りない。** 上限内の添付を設問の数だけ並べられる
        var policy = AttachmentPolicy.Create(
            allowedExtensions: ["txt"],
            maxFileSizeBytes: 1024,
            maxFileCount: 2,
            maxTotalBytes: 1500);

        var rejections = await new AttachmentInspector(policy).InspectSubmissionAsync(
        [
            new AnsweredAttachment("q1", new IncomingAttachment("a.txt", new byte[1000])),
            new AnsweredAttachment("q2", new IncomingAttachment("b.txt", new byte[1000])),
        ]);

        Assert.Equal(AttachmentRejectionReason.TotalTooLarge, rejections.Single().Reason);
    }

    [Fact]
    public async Task 合計の上限は既定で1件あたりと個数から決まる()
    {
        // 1024 バイト × 2 件 = 2048 バイトまで
        var rejections = await new AttachmentInspector(Policy()).InspectSubmissionAsync(
        [
            new AnsweredAttachment("q1", new IncomingAttachment("a.txt", new byte[1024])),
            new AnsweredAttachment("q2", new IncomingAttachment("b.txt", new byte[1024])),
            new AnsweredAttachment("q3", new IncomingAttachment("c.txt", new byte[1])),
        ]);

        Assert.Equal(AttachmentRejectionReason.TotalTooLarge, rejections.Single().Reason);
    }

    [Fact]
    public async Task 個数の上限は設問ごとに見る()
    {
        // 設問をまたいで合算しない。**上限は「1 設問あたり」**
        var rejections = await new AttachmentInspector(Policy()).InspectSubmissionAsync(
        [
            new AnsweredAttachment("q1", Png("a.png")),
            new AnsweredAttachment("q1", Png("b.png")),
            new AnsweredAttachment("q2", Png("c.png")),
        ]);

        Assert.Empty(rejections);
    }

    [Fact]
    public async Task 設問ごとの上限で絞り込める()
    {
        // 設問側が「1 件まで」と言っているので、共通の上限（2 件）より厳しくなる
        var rejections = await new AttachmentInspector(Policy()).InspectSubmissionAsync(
            [
                new AnsweredAttachment("q1", Png("a.png")),
                new AnsweredAttachment("q1", Png("b.png")),
            ],
            policyFor: _ => Policy().Tighten(maxFileCount: 1, maxFileSizeBytes: null));

        var rejection = rejections.Single();
        Assert.Equal(AttachmentRejectionReason.TooMany, rejection.Reason);
        Assert.Equal("q1", rejection.QuestionId);
    }

    [Fact]
    public void 設問ごとの上限で共通の上限より緩くはならない()
    {
        // **アンケートを作る人が書いた値で、運用者が決めた上限を超えられないこと**
        var tightened = Policy().Tighten(maxFileCount: 100, maxFileSizeBytes: 100_000);

        Assert.Equal(2, tightened.MaxFileCount);
        Assert.Equal(1024, tightened.MaxFileSizeBytes);
    }

    [Fact]
    public async Task 拒否した添付にはどの設問かが付く()
    {
        var rejections = await new AttachmentInspector(Policy()).InspectSubmissionAsync(
            [new AnsweredAttachment("q1", new IncomingAttachment("a.exe", PngHeader))]);

        var rejection = rejections.Single();
        Assert.Equal(AttachmentRejectionReason.ExtensionNotAllowed, rejection.Reason);
        Assert.Equal("q1", rejection.QuestionId);
        Assert.Equal("a.exe", rejection.FileName);
    }
}
