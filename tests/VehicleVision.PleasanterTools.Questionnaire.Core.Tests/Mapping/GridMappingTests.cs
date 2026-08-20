using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Mapping;

/// <summary>グリッドとランキングを Pleasanter の列へ写せること。</summary>
/// <remarks>
/// **どう写すかを決めるのは使う人**（Issue #54）。
/// 行ごとに列へ繋ぐことも、まとめて 1 列へ入れることも、同じ仕組みでできる。
/// </remarks>
public class GridMappingTests
{
    private static Question Grid(bool multi = false) => new()
    {
        QuestionId = "q-grid",
        Type = multi ? QuestionType.CheckboxGrid : QuestionType.Grid,
        Title = LocalizedText.Japanese("満足度"),
        Choices =
        [
            new Choice("good", LocalizedText.Japanese("よい")),
            new Choice("bad", LocalizedText.Japanese("わるい")),
        ],
        Settings = new QuestionSettings
        {
            Rows =
            [
                new GridRow("price", LocalizedText.Japanese("価格")),
                new GridRow("quality", LocalizedText.Japanese("品質")),
            ],
        },
    };

    private static Question Ranking() => new()
    {
        QuestionId = "q-rank",
        Type = QuestionType.Ranking,
        Title = LocalizedText.Japanese("大事な順"),
        Choices =
        [
            new Choice("price", LocalizedText.Japanese("価格")),
            new Choice("speed", LocalizedText.Japanese("速さ")),
            new Choice("support", LocalizedText.Japanese("対応")),
        ],
    };

    private static SurveyDefinition Definition(params Question[] questions) => new()
    {
        SurveyId = Guid.NewGuid().ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("見本"),
        Pages = [new Page { PageId = "p1", Questions = [.. questions] }],
    };

    private static Answer GridAnswer() => new("q-grid", [])
    {
        Rows = ImmutableDictionary<string, ImmutableArray<string>>.Empty
            .Add("price", ["good"])
            .Add("quality", ["bad"]),
    };

    // ---- 行ごとに 1 列 ------------------------------------------------------

    [Fact]
    public void 行ごとに別の列へ写せる()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q-grid", QuestionPort.Value, "price")),
                ColumnAssignment.Direct("ClassB", new MappingSource("q-grid", QuestionPort.Value, "quality")),
            ],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [GridAnswer()]);

        Assert.Equal(["good"], result.Columns["ClassA"].ToArray());
        Assert.Equal(["bad"], result.Columns["ClassB"].ToArray());
    }

    // ---- まとめて 1 列 ------------------------------------------------------

    [Fact]
    public void 複数の行をまとめて一列へ入れられる()
    {
        // **列を節約したい人はこちら。** 仕組みは既存の N : 1 : 1 のまま
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Converted(
                    "ClassA",
                    MappingConverter.Of("join", ("separator", "、")),
                    new MappingSource("q-grid", QuestionPort.Value, "price"),
                    new MappingSource("q-grid", QuestionPort.Value, "quality")),
            ],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [GridAnswer()]);

        Assert.Equal(["good、bad"], result.Columns["ClassA"].ToArray());
    }

    [Fact]
    public void 答えていない行は空になる()
    {
        var answer = new Answer("q-grid", [])
        {
            Rows = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("price", ["good"]),
        };

        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassB", new MappingSource("q-grid", QuestionPort.Value, "quality")),
            ],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [answer]);

        // **空配列は「消す」**（管理している列は結果へ必ず含める）
        Assert.Empty(result.Columns["ClassB"]);
    }

    // ---- ランキング ---------------------------------------------------------

    [Fact]
    public void ランキングは項目ごとに順位が取れる()
    {
        // 並べた順そのものが答え
        var answer = Answer.Of("q-rank", "speed", "price");

        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("NumA", new MappingSource("q-rank", QuestionPort.Value, "price")),
                ColumnAssignment.Direct("NumB", new MappingSource("q-rank", QuestionPort.Value, "speed")),
                ColumnAssignment.Direct("NumC", new MappingSource("q-rank", QuestionPort.Value, "support")),
            ],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [answer]);

        // **1 から数える。** 0 始まりだと画面で読み違える
        Assert.Equal(["2"], result.Columns["NumA"].ToArray());
        Assert.Equal(["1"], result.Columns["NumB"].ToArray());

        // **選ばれていない項目は空。** 「順位なし」を 0 で表さない
        Assert.Empty(result.Columns["NumC"]);
    }

    // ---- 公開前の検査 -------------------------------------------------------

    private static MappingProblemCode[] Codes(MappingDefinition mapping, SurveyDefinition definition) =>
        [.. MappingValidator.Validate(mapping, definition).Select(problem => problem.Code)];

    [Fact]
    public void 無い行を指したら咎める()
    {
        // **行を消したときの直し忘れがここで見つかる**
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q-grid", QuestionPort.Value, "居ない行")),
            ],
        };

        Assert.Contains(MappingProblemCode.RowNotInQuestion, Codes(mapping, Definition(Grid())));
    }

    [Fact]
    public void 行を持たない設問に行を指したら咎める()
    {
        var text = new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Text,
            Title = LocalizedText.Japanese("感想"),
        };

        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q1", QuestionPort.Value, "price")),
            ],
        };

        Assert.Contains(MappingProblemCode.RowNotSupported, Codes(mapping, Definition(text)));
    }

    [Fact]
    public void 行を持つ設問で行を指さなければ咎める()
    {
        // **指さないと「行をまたいだ全部」が入り、どの行の答えか分からなくなる**
        var mapping = new MappingDefinition
        {
            Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q-grid"))],
        };

        Assert.Contains(MappingProblemCode.RowRequired, Codes(mapping, Definition(Grid())));
    }

    [Fact]
    public void 素直な割り当ては咎めない()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q-grid", QuestionPort.Value, "price")),
                ColumnAssignment.Direct("ClassB", new MappingSource("q-grid", QuestionPort.Value, "quality")),
                ColumnAssignment.Direct("NumA", new MappingSource("q-rank", QuestionPort.Value, "price")),
                ColumnAssignment.Direct("NumB", new MappingSource("q-rank", QuestionPort.Value, "speed")),
                ColumnAssignment.Direct("NumC", new MappingSource("q-rank", QuestionPort.Value, "support")),
            ],
        };

        Assert.Empty(MappingValidator.Validate(mapping, Definition(Grid(), Ranking())));
    }

    // ---- 列の本数 -----------------------------------------------------------

    [Fact]
    public void 型ごとに使っている本数を数える()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q-grid", QuestionPort.Value, "price")),
                ColumnAssignment.Direct("ClassB", new MappingSource("q-grid", QuestionPort.Value, "quality")),
                ColumnAssignment.Direct("NumA", new MappingSource("q-rank", QuestionPort.Value, "price")),
            ],
        };

        var usage = ColumnBudget.Measure(mapping);

        Assert.Equal(2, usage.Single(entry => entry.Prefix == "Class").Used);
        Assert.Equal(1, usage.Single(entry => entry.Prefix == "Num").Used);
        Assert.All(usage, entry => Assert.True(entry.Fits));
    }

    [Fact]
    public void 上限を超えたら足りないと分かる()
    {
        // **保存や公開で初めて分かると作り直しになる**
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                .. Enumerable.Range(0, 30).Select(index =>
                    ColumnAssignment.Direct(
                        $"Class{index:000}",
                        new MappingSource("q-grid", QuestionPort.Value, "price"))),
            ],
        };

        var usage = Assert.Single(ColumnBudget.Measure(mapping));

        Assert.False(usage.Fits);
        Assert.Equal(-4, usage.Remaining);
    }

    [Fact]
    public void 行ごとに写すと何本要るかを見積もれる()
    {
        // グリッド 2 行 ＋ ランキング 3 項目 = 5 本
        Assert.Equal(5, ColumnBudget.RequiredPortCount(Definition(Grid(), Ranking())));
    }
}
