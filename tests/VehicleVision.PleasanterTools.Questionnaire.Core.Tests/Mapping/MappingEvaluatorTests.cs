using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Mapping;

public class MappingEvaluatorTests
{
    private static MappingDefinition Mapping(params ColumnAssignment[] assignments) =>
        new() { Assignments = [.. assignments] };

    private static MappingResult Evaluate(
        MappingDefinition mapping,
        IReadOnlyCollection<Answer> answers,
        IScriptConverter? script = null) =>
        new MappingEvaluator(script).Evaluate(mapping, answers);

    [Fact]
    public void 入力をそのまま列へ写す()
    {
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "満足")]);

        Assert.Equal(["満足"], result.Columns["ClassA"].ToArray());
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void 管理している列は値が無くても結果へ含める()
    {
        // **空配列は「消す」を意味する。** 編集で回答を消したときに古い値を残さない
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("q1")));

        var result = Evaluate(mapping, []);

        Assert.True(result.Columns.ContainsKey("ClassA"));
        Assert.Empty(result.Columns["ClassA"]);
    }

    [Fact]
    public void 割り当てが無い列は結果に含めない()
    {
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "満足")]);

        Assert.False(result.Columns.ContainsKey("ClassB"));
    }

    [Fact]
    public void 選択肢をコードへ置き換える()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "NumA",
            MappingConverter.Of(ConverterOperations.Map, ("map.とても満足", "5"), ("map.満足", "4")),
            new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "とても満足")]);

        Assert.Equal(["5"], result.Columns["NumA"].ToArray());
    }

    [Fact]
    public void 複数の設問を連結して一つの列へ入れる()
    {
        // **N : 1 : 1。** 入力の順序は宣言順で決まる
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Join, ("separator", " ")),
            new MappingSource("姓"),
            new MappingSource("名")));

        var result = Evaluate(mapping, [Answer.Of("名", "太郎"), Answer.Of("姓", "山田")]);

        Assert.Equal(["山田 太郎"], result.Columns["ClassA"].ToArray());
    }

    [Fact]
    public void 入力の順序は宣言順で決まり回答の順序に依存しない()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Join, ("separator", "/")),
            new MappingSource("a"),
            new MappingSource("b")));

        var one = Evaluate(mapping, [Answer.Of("a", "1"), Answer.Of("b", "2")]);
        var other = Evaluate(mapping, [Answer.Of("b", "2"), Answer.Of("a", "1")]);

        Assert.Equal(["1/2"], one.Columns["ClassA"].ToArray());
        Assert.Equal(one.Columns["ClassA"].ToArray(), other.Columns["ClassA"].ToArray());
    }

    [Fact]
    public void 一つの設問を複数のチェック列へ展開する()
    {
        // **同じ設問を入力にした割り当てを、列の数だけ並べる**
        var mapping = Mapping(
            ColumnAssignment.Converted(
                "CheckA", MappingConverter.Of(ConverterOperations.ToCheck, ("value", "A")),
                new MappingSource("q1")),
            ColumnAssignment.Converted(
                "CheckB", MappingConverter.Of(ConverterOperations.ToCheck, ("value", "B")),
                new MappingSource("q1")),
            ColumnAssignment.Converted(
                "CheckC", MappingConverter.Of(ConverterOperations.ToCheck, ("value", "C")),
                new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "A", "C")]);

        Assert.Equal(["true"], result.Columns["CheckA"].ToArray());
        Assert.Equal(["false"], result.Columns["CheckB"].ToArray());
        Assert.Equal(["true"], result.Columns["CheckC"].ToArray());
    }

    [Fact]
    public void 文字列の内容を判定してフラグを立てる()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "CheckD",
            MappingConverter.Of(ConverterOperations.Contains, ("keyword", "至急")),
            new MappingSource("q1")));

        Assert.Equal(
            ["true"],
            Evaluate(mapping, [Answer.Of("q1", "至急対応してほしい")]).Columns["CheckD"].ToArray());
        Assert.Equal(
            ["false"],
            Evaluate(mapping, [Answer.Of("q1", "特にありません")]).Columns["CheckD"].ToArray());
    }

    [Fact]
    public void 条件で出す値を変える()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.When, ("when", "はい"), ("then", "要対応"), ("else", "不要")),
            new MappingSource("q1")));

        Assert.Equal(["要対応"], Evaluate(mapping, [Answer.Of("q1", "はい")]).Columns["ClassA"].ToArray());
        Assert.Equal(["不要"], Evaluate(mapping, [Answer.Of("q1", "いいえ")]).Columns["ClassA"].ToArray());
    }

    [Fact]
    public void その他の自由記述を別の列へ写す()
    {
        // **OtherText をグラフの外に置かない**
        var mapping = Mapping(ColumnAssignment.Direct(
            "ClassB", new MappingSource("q1", QuestionPort.OtherText)));
        var answer = new Answer("q1", ["other"]) { OtherText = "自由記述の内容" };

        var result = Evaluate(mapping, [answer]);

        Assert.Equal(["自由記述の内容"], result.Columns["ClassB"].ToArray());
    }

    [Fact]
    public void 添付の名前を列へ写す()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassC",
            MappingConverter.Of(ConverterOperations.Join, ("separator", ",")),
            new MappingSource("q1", QuestionPort.FileNames)));
        var answer = new Answer("q1", []) { FileNames = ["a.png", "b.pdf"] };

        var result = Evaluate(mapping, [answer]);

        Assert.Equal(["a.png,b.pdf"], result.Columns["ClassC"].ToArray());
    }

    [Fact]
    public void 入力が複数なのに変換が無ければ不備として扱う()
    {
        var mapping = Mapping(new ColumnAssignment
        {
            TargetColumn = "ClassA",
            Sources = [new MappingSource("q1"), new MappingSource("q2")],
            Converter = null,
        });

        var result = Evaluate(mapping, [Answer.Of("q1", "A"), Answer.Of("q2", "B")]);

        Assert.False(result.Columns.ContainsKey("ClassA"));
        Assert.Single(result.Problems);
    }

    [Fact]
    public void 同じ列への割り当てが重複していたら不備として扱う()
    {
        var mapping = Mapping(
            ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
            ColumnAssignment.Direct("ClassA", new MappingSource("q2")));

        var result = Evaluate(mapping, [Answer.Of("q1", "A"), Answer.Of("q2", "B")]);

        // 先勝ちで 1 件だけ入り、重複は不備として報告される
        Assert.Equal(["A"], result.Columns["ClassA"].ToArray());
        Assert.Single(result.Problems);
    }

    private sealed class FailingScript : IScriptConverter
    {
        public ImmutableArray<string> Convert(string script, ImmutableArray<string> input) =>
            throw new ScriptConverterException("実行時エラー");
    }

    private sealed class UpperCaseScript : IScriptConverter
    {
        public ImmutableArray<string> Convert(string script, ImmutableArray<string> input) =>
            [.. input.Select(value => value.ToUpperInvariant())];
    }

    [Fact]
    public void スクリプト変換を適用できる()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Script, ("script", "…")),
            new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "abc")], new UpperCaseScript());

        Assert.Equal(["ABC"], result.Columns["ClassA"].ToArray());
    }

    [Fact]
    public void スクリプトが失敗しても他の列の評価は続く()
    {
        // **回答そのものは捨てない。** 不備として報告し、人が対処する
        var mapping = Mapping(
            ColumnAssignment.Converted(
                "ClassA", MappingConverter.Of(ConverterOperations.Script, ("script", "…")),
                new MappingSource("q1")),
            ColumnAssignment.Direct("ClassB", new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "abc")], new FailingScript());

        Assert.False(result.Columns.ContainsKey("ClassA"));
        Assert.Equal(["abc"], result.Columns["ClassB"].ToArray());
        Assert.Single(result.Problems);
    }

    [Fact]
    public void スクリプト実行環境が無ければ不備として扱う()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Script, ("script", "…")),
            new MappingSource("q1")));

        var result = Evaluate(mapping, [Answer.Of("q1", "abc")], script: null);

        Assert.Single(result.Problems);
    }
}
