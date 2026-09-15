using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Scripting.Tests;

/// <summary>打ち切りがマッピングの不備として表に出ることを確かめる。</summary>
/// <remarks>
/// **黙って空の値を入れないことがこの試験の主眼**（Issue #83）。
/// 不備が 1 つでもあれば、送信ワーカーは回答をデッドレターへ回す（<c>ResponseSender</c>）。
/// </remarks>
public class MappingWithJintTests
{
    private static MappingResult Evaluate(string script, params string[] answers)
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Converted(
                    "ClassA",
                    MappingConverter.Of(ConverterOperations.Script, ("script", script)),
                    new MappingSource("q1")),
            ],
        };

        var evaluator = new MappingEvaluator(new JintScriptConverter(
            new ScriptConverterOptions { TimeLimit = TimeSpan.FromMilliseconds(100) }));

        return evaluator.Evaluate(mapping, [.. answers.Select(value => Answer.Of("q1", value))]);
    }

    [Fact]
    public void 普通のスクリプトは列の値になる()
    {
        var result = Evaluate("return input[0] + '円';", "1000");

        Assert.Empty(result.Problems);
        Assert.Equal(["1000円"], result.Columns["ClassA"].ToArray());
    }

    [Fact]
    public void 止まらないスクリプトは不備になり列を作らない()
    {
        // **打ち切ったことが理由に残る。** ここが空文字で埋まると、
        // 「変換に失敗した」ことに誰も気付けないまま Pleasanter へ書かれてしまう
        var result = Evaluate("while (true) { }", "1000");

        var problem = Assert.Single(result.Problems);
        Assert.Equal("ClassA", problem.TargetColumn);
        Assert.Contains("実行時間の上限", problem.Reason, StringComparison.Ordinal);
        Assert.False(result.Columns.ContainsKey("ClassA"));
    }
}
