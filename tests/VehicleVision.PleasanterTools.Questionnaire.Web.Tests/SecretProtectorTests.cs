using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class SecretProtectorTests
{
    private static SecretProtector Create() => new(SecretProtector.GenerateKey());

    [Fact]
    public void 暗号化して復号すると元に戻る()
    {
        var protector = Create();

        var protectedValue = protector.Protect("JBSWY3DPEHPK3PXP");

        Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(protectedValue));
    }

    [Fact]
    public void 暗号文に元の値が残らない()
    {
        var protector = Create();

        var protectedValue = protector.Protect("JBSWY3DPEHPK3PXP");

        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", protectedValue, StringComparison.Ordinal);
    }

    [Fact]
    public void 同じ値でも毎回違う暗号文になる()
    {
        var protector = Create();

        // **使い捨ての値を毎回変える。** 同じになると、
        // 同じ共有鍵を使っている利用者が見分けられる
        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void 別の鍵では復号できない()
    {
        var protectedValue = Create().Protect("JBSWY3DPEHPK3PXP");

        Assert.Null(Create().Unprotect(protectedValue));
    }

    [Fact]
    public void 改竄されていれば復号できない()
    {
        var protector = Create();
        var parts = protector.Protect("JBSWY3DPEHPK3PXP").Split('$');

        // 暗号文の 1 文字を差し替える
        var cipher = parts[3].ToCharArray();
        cipher[0] = cipher[0] == 'A' ? 'B' : 'A';
        var tampered = string.Join('$', parts[0], parts[1], parts[2], new string(cipher));

        Assert.Null(protector.Unprotect(tampered));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1$壊れている")]
    [InlineData("v0$AAAA$AAAA$AAAA")]
    public void 壊れた値でも例外にならない(string value)
    {
        Assert.Null(Create().Unprotect(value));
    }

    [Fact]
    public void 長さの足りない鍵は受け付けない()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        Assert.Throws<ArgumentException>(() => new SecretProtector(shortKey));
    }

    [Fact]
    public void Base64でない鍵は受け付けない()
    {
        Assert.Throws<ArgumentException>(() => new SecretProtector("これは Base64 ではない"));
    }
}
