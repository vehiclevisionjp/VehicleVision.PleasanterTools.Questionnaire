using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>招待に使うトークン。</summary>
public class InvitationTokenTests
{
    [Fact]
    public void 同じトークンは二度作られない()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => InvitationToken.Create()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void トークンはURLに載せられる文字だけでできている()
    {
        var token = InvitationToken.Create();

        // **招待は URL やメールに載る。** 途中で化ける文字を混ぜない
        Assert.All(token, character => Assert.True(
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_',
            $"URL に載せられない文字が混ざっている: {character}"));
    }

    [Fact]
    public void 保存する形からトークンは読み取れない()
    {
        var token = InvitationToken.Create();

        var hash = InvitationToken.HashOf(token);

        // **ハッシュのみを置く。** 表を読めた人が招待を使えては意味が無い
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void 同じトークンからは同じハッシュになる()
    {
        var token = InvitationToken.Create();

        // ハッシュで直に引くので、ここがぶれると招待が使えなくなる
        Assert.Equal(InvitationToken.HashOf(token), InvitationToken.HashOf(token));

        // 前後の空白は落として照合する（紙から打ち直す前提）
        Assert.Equal(InvitationToken.HashOf(token), InvitationToken.HashOf($"  {token} "));
    }

    [Fact]
    public void 違うトークンは違うハッシュになる()
    {
        Assert.NotEqual(
            InvitationToken.HashOf(InvitationToken.Create()),
            InvitationToken.HashOf(InvitationToken.Create()));
    }
}
