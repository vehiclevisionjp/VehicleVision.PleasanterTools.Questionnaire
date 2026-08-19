using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>回答用 URL に使う値（<c>_documents/データモデル設計.md</c> 3 章）。</summary>
public class SurveyPublicIdTests
{
    /// <summary>
    /// **使い回さない。** 複製で元の値を写すと、
    /// 2 つのアンケートが同じ回答用 URL を指すことになる（Issue #46）。
    /// </summary>
    [Fact]
    public void 呼ぶたびに違う値になる()
    {
        var generated = Enumerable.Range(0, 100).Select(_ => SurveyPublicId.Generate()).ToList();

        Assert.Equal(generated.Count, generated.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary><c>Surveys.PublicId</c> は 64 文字。**溢れない長さにする。**</summary>
    [Fact]
    public void 印と_16_進_32_文字でできている()
    {
        var publicId = SurveyPublicId.Generate();

        Assert.StartsWith(SurveyPublicId.Prefix, publicId, StringComparison.Ordinal);
        var body = publicId[SurveyPublicId.Prefix.Length..];
        Assert.Equal(32, body.Length);
        Assert.All(body, character => Assert.Contains(character, "0123456789abcdef"));
    }
}
