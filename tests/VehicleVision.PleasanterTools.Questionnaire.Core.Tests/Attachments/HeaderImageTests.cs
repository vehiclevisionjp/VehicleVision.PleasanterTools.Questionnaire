using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Attachments;

/// <summary>ヘッダ画像の受け入れ（Issue #56）。</summary>
/// <remarks>
/// **管理者が上げるものでも検査を緩めない。**
/// 公開アンケートを見た全員へ配るファイルなので、
/// 乗っ取られた 1 つの管理者の口が、そのまま配布の口になる。
/// </remarks>
public class HeaderImageTests
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    [Fact]
    public async Task 中身と拡張子が合っている画像は通る()
    {
        Assert.Empty(await HeaderImage.InspectAsync(new IncomingAttachment("banner.png", Png)));
        Assert.Empty(await HeaderImage.InspectAsync(new IncomingAttachment("banner.jpg", Jpeg)));
    }

    [Theory]
    // **SVG を許可しない。** 中に script を書けるうえ、先頭バイトで見分けられない
    [InlineData("banner.svg")]
    // 画像以外
    [InlineData("banner.pdf")]
    [InlineData("banner.html")]
    [InlineData("banner.zip")]
    // 拡張子が無い
    [InlineData("banner")]
    public async Task 画像以外の拡張子は拒否する(string fileName)
    {
        var rejections = await HeaderImage.InspectAsync(new IncomingAttachment(fileName, Png));

        Assert.Equal(
            AttachmentRejectionReason.ExtensionNotAllowed, rejections.Single().Reason);
    }

    [Fact]
    public async Task 拡張子を偽っていれば拒否する()
    {
        // HTML を png と名乗らせても、先頭バイトで落ちる
        var html = "<html><script>alert(1)</script>"u8.ToArray();

        var rejections = await HeaderImage.InspectAsync(new IncomingAttachment("banner.png", html));

        Assert.Equal(
            AttachmentRejectionReason.ContentDoesNotMatchExtension, rejections.Single().Reason);
    }

    [Fact]
    public async Task 上限を超える大きさは拒否する()
    {
        var large = new byte[HeaderImage.MaxBytes + 1];
        Png.CopyTo(large, 0);

        var rejections = await HeaderImage.InspectAsync(new IncomingAttachment("banner.png", large));

        Assert.Equal(AttachmentRejectionReason.TooLarge, rejections.Single().Reason);
    }

    [Fact]
    public async Task パス区切りを含む名前は拒否する()
    {
        var rejections = await HeaderImage.InspectAsync(
            new IncomingAttachment("../../banner.png", Png));

        Assert.Equal(AttachmentRejectionReason.InvalidFileName, rejections.Single().Reason);
    }

    [Theory]
    [InlineData("banner.png", "image/png")]
    [InlineData("banner.PNG", "image/png")]
    [InlineData("banner.jpg", "image/jpeg")]
    [InlineData("banner.jpeg", "image/jpeg")]
    [InlineData("banner.gif", "image/gif")]
    [InlineData("banner.webp", "image/webp")]
    public void 配信する型は拡張子から決める(string fileName, string expected)
    {
        // **ブラウザが名乗った Content-Type は使わない**
        Assert.Equal(expected, HeaderImage.ContentTypeOf(fileName));
    }

    [Theory]
    [InlineData("banner.svg")]
    [InlineData("banner.html")]
    [InlineData("banner")]
    public void 知らない拡張子には型を決めない(string fileName)
    {
        Assert.Null(HeaderImage.ContentTypeOf(fileName));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public void 画像以外の型は配らない(string? contentType)
    {
        Assert.False(HeaderImage.IsAllowedContentType(contentType));
    }

    [Fact]
    public void 許可する拡張子はすべて先頭バイトを照合できる()
    {
        // **照合できない形式を足すと 2 層目が素通りになる**
        foreach (var extension in HeaderImage.AllowedExtensions)
        {
            Assert.True(
                FileSignature.CanVerify(extension),
                $"{extension} は先頭バイトを照合できない。**許可リストへ入れないこと**");
        }
    }

    [Fact]
    public void 一枚しか受け付けない()
    {
        Assert.Equal(1, HeaderImage.Policy.MaxFileCount);
    }

    [Fact]
    public void ウイルススキャンには掛けない()
    {
        // **スキャナの生死に管理画面の保存を引きずらせない**
        Assert.False(HeaderImage.Policy.VirusScanEnabled);
    }
}
