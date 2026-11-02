using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

public sealed class LookupKeyValueValidatorTests
{
    private static LookupKeyValueResult Text(string? raw, LookupMatchMode mode = LookupMatchMode.Exact) =>
        LookupKeyValueValidator.Validate(raw, LookupColumnKind.Text, mode);

    [Theory]
    [InlineData("ABC-001")]
    [InlineData("00123")]
    [InlineData("千代田区 千代田")]
    [InlineData("ＡＢＣ　００１")]
    [InlineData("𠮷野家")]
    [InlineData("Own")]
    public void 許可文字だけの文字列は通る(string raw)
    {
        var result = Text(raw);

        Assert.True(result.IsValid);
        Assert.Equal(raw, result.Value);
    }

    [Fact]
    public void 前後の空白は除くが先頭ゼロは保つ()
    {
        var result = Text("  00123 ");

        Assert.Equal("00123", result.Value);
    }

    [Fact]
    public void 欠落は拒否する()
    {
        Assert.Equal(LookupKeyValueProblem.Missing, Text(null).Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　")]
    public void 空と空白のみは拒否する(string raw)
    {
        Assert.Equal(LookupKeyValueProblem.Empty, Text(raw).Problem);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("\tabc")]
    [InlineData("abc\n")]
    public void 制御文字は前後でも拒否する(string raw)
    {
        Assert.Equal(LookupKeyValueProblem.ControlCharacter, Text(raw).Problem);
    }

    [Theory]
    [InlineData("a%b")]
    [InlineData("a_c")]
    [InlineData("a[b]")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("[\"a\",\"b\"]")]
    [InlineData("1,3")]
    [InlineData("a;--")]
    [InlineData("a b")]
    public void ワイルドカード_引用符_配列風は拒否する(string raw)
    {
        Assert.Equal(LookupKeyValueProblem.DisallowedCharacter, Text(raw).Problem);
    }

    [Fact]
    public void 最大長を超えたら拒否する()
    {
        Assert.True(Text(new string('a', LookupKeyValueValidator.MaxLength)).IsValid);
        Assert.Equal(
            LookupKeyValueProblem.TooLong,
            Text(new string('a', LookupKeyValueValidator.MaxLength + 1)).Problem);
    }

    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("abcd", true)]
    [InlineData("あい", false)]
    [InlineData("あいう", true)]
    public void 前方一致は最短3文字(string raw, bool valid)
    {
        Assert.Equal(valid, Text(raw, LookupMatchMode.Prefix).IsValid);
    }

    [Fact]
    public void 完全一致は短くてもよい()
    {
        Assert.True(Text("a").IsValid);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("00123", true)]
    [InlineData("-5", true)]
    [InlineData("1.0", false)]
    [InlineData("abc", false)]
    [InlineData("１２３", false)]
    [InlineData("1,3", false)]
    [InlineData("-", false)]
    [InlineData("1234567890123456789", false)]
    public void 数値列は整数の数字だけを文字列のまま通す(string raw, bool valid)
    {
        var result = LookupKeyValueValidator.Validate(raw, LookupColumnKind.Number, LookupMatchMode.Exact);

        Assert.Equal(valid, result.IsValid);
        if (valid)
        {
            Assert.Equal(raw, result.Value);
        }
    }

    [Fact]
    public void 数値列の前方一致は拒否する()
    {
        var result = LookupKeyValueValidator.Validate("123", LookupColumnKind.Number, LookupMatchMode.Prefix);

        Assert.Equal(LookupKeyValueProblem.UnsupportedColumnKind, result.Problem);
    }

    [Theory]
    [InlineData(LookupColumnKind.Date)]
    [InlineData(LookupColumnKind.Check)]
    [InlineData(LookupColumnKind.Other)]
    public void キーにできない列の種類は拒否する(LookupColumnKind kind)
    {
        var result = LookupKeyValueValidator.Validate("1", kind, LookupMatchMode.Exact);

        Assert.Equal(LookupKeyValueProblem.UnsupportedColumnKind, result.Problem);
    }
}
