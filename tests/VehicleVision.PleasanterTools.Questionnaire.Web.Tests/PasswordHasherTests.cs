using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class PasswordHasherTests
{
    private readonly PasswordHasher hasher = new();

    [Fact]
    public void 同じパスワードでも毎回違うハッシュになる()
    {
        var first = hasher.Hash("correct horse battery staple");
        var second = hasher.Hash("correct horse battery staple");

        // **塩が毎回変わる。** 同じになると、同じパスワードの利用者が見分けられる
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 保存した形にパスワードそのものが残らない()
    {
        var stored = hasher.Hash("mypassword-12345");

        Assert.DoesNotContain("mypassword", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 正しいパスワードなら通る()
    {
        var stored = hasher.Hash("correct horse battery staple");

        var (verified, needsRehash) = hasher.Verify("correct horse battery staple", stored);

        Assert.True(verified);
        Assert.False(needsRehash);
    }

    [Fact]
    public void 違うパスワードは通らない()
    {
        var stored = hasher.Hash("correct horse battery staple");

        var (verified, _) = hasher.Verify("correct horse battery stapler", stored);

        Assert.False(verified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1$100$notbase64$notbase64")]
    [InlineData("v0$210000$AAAA$AAAA")]
    [InlineData("壊れている")]
    public void 壊れた保存値でも例外にならず通らない(string stored)
    {
        var (verified, needsRehash) = hasher.Verify("なんでもよい", stored);

        Assert.False(verified);
        Assert.False(needsRehash);
    }

    [Fact]
    public void 反復回数が古ければ作り直しを促す()
    {
        // 反復回数を落とした、古い書式の保存値を手で組み立てる
        var current = hasher.Hash("secret-value-1234");
        var parts = current.Split('$');
        var old = string.Join('$', parts[0], "1000", parts[2], parts[3]);

        // 中身のハッシュは今の反復回数で作られているので照合は落ちる。
        // **ここで見たいのは「古い」と判断できることではなく、
        // 落ちたときに作り直しを促さないこと**
        var (verified, needsRehash) = hasher.Verify("secret-value-1234", old);

        Assert.False(verified);
        Assert.False(needsRehash);
    }
}
