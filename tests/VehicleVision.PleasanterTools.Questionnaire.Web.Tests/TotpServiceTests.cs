using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class TotpServiceTests
{
    private readonly TotpService service = new();

    [Fact]
    public void 認証アプリが出した数字が通る()
    {
        var secret = service.GenerateSecret();
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var (verified, _) = service.Verify(secret, code);

        Assert.True(verified);
    }

    [Fact]
    public void 別の共有鍵の数字は通らない()
    {
        var code = new Totp(Base32Encoding.ToBytes(service.GenerateSecret())).ComputeTotp();

        var (verified, _) = service.Verify(service.GenerateSecret(), code);

        Assert.False(verified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("      ")]
    [InlineData("000000")]
    [InlineData("abcdef")]
    public void 合わない入力は通らない(string code)
    {
        var (verified, _) = service.Verify(service.GenerateSecret(), code);

        Assert.False(verified);
    }

    [Fact]
    public void 壊れた共有鍵でも例外にならない()
    {
        var (verified, _) = service.Verify("これは Base32 ではない", "123456");

        Assert.False(verified);
    }

    [Fact]
    public void 前後の空白があっても通る()
    {
        var secret = service.GenerateSecret();
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var (verified, _) = service.Verify(secret, $"  {code} ");

        Assert.True(verified);
    }

    [Fact]
    public void 共有鍵は毎回変わる()
    {
        Assert.NotEqual(service.GenerateSecret(), service.GenerateSecret());
    }

    [Fact]
    public void 認証アプリへ渡すURIに必要な項目が入る()
    {
        var uri = TotpService.BuildUri("アンケート", "admin@example.com", "JBSWY3DPEHPK3PXP");

        Assert.StartsWith("otpauth://totp/", uri, StringComparison.Ordinal);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
        // **記号はそのまま入れない。** ラベルの区切りと混ざる
        Assert.DoesNotContain("admin@example.com", uri, StringComparison.Ordinal);
    }
}

public class RecoveryCodeTests
{
    [Fact]
    public void 決めた本数を作る()
    {
        Assert.Equal(RecoveryCode.Count, RecoveryCode.Generate().Count);
    }

    [Fact]
    public void 同じコードは出ない()
    {
        var codes = RecoveryCode.Generate();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void 呼ぶたびに違う組になる()
    {
        Assert.NotEqual(RecoveryCode.Generate(), RecoveryCode.Generate());
    }

    [Fact]
    public void 紛らわしい文字を使わない()
    {
        foreach (var code in RecoveryCode.Generate())
        {
            // 0 と O、1 と I と L は書き写しで取り違える
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
        }
    }

    [Theory]
    [InlineData("abcde-fghjk", "ABCDEFGHJK")]
    [InlineData("ABCDE FGHJK", "ABCDEFGHJK")]
    [InlineData("  ABCDE-FGHJK  ", "ABCDEFGHJK")]
    public void 入力の揺れを吸収する(string input, string expected)
    {
        Assert.Equal(expected, RecoveryCode.Normalize(input));
    }
}
