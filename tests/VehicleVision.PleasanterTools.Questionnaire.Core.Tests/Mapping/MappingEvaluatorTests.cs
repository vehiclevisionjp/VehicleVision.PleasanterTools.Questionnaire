using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Mapping;

public class MappingEvaluatorTests
{
    private static MappingNode Input(string nodeId, string questionId) => new()
    {
        NodeId = nodeId,
        Type = MappingNodeType.QuestionInput,
        QuestionId = questionId,
    };

    private static MappingNode Transform(
        string nodeId,
        string operation,
        params (string Key, string Value)[] config) => new()
        {
            NodeId = nodeId,
            Type = MappingNodeType.Transform,
            Operation = operation,
            Config = config.ToImmutableDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };

    private static MappingNode Condition(
        string nodeId,
        string operation,
        params (string Key, string Value)[] config) => new()
        {
            NodeId = nodeId,
            Type = MappingNodeType.Condition,
            Operation = operation,
            Config = config.ToImmutableDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };

    private static MappingNode Output(string nodeId, string columnName) => new()
    {
        NodeId = nodeId,
        Type = MappingNodeType.ColumnOutput,
        ColumnName = columnName,
    };

    [Fact]
    public void 設問の値をそのまま列へ写す()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA")],
            Edges = [MappingEdge.Simple("n1", "n2")],
        };

        var result = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "満足")]);

        Assert.Equal(["満足"], result["ClassA"].ToArray());
    }

    [Fact]
    public void 流れてこなかった列は結果に含めない()
    {
        // **未接続の出力列を空文字で上書きしない**
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA"), Output("n3", "ClassB")],
            Edges = [MappingEdge.Simple("n1", "n2")],
        };

        var result = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "満足")]);

        Assert.True(result.ContainsKey("ClassA"));
        Assert.False(result.ContainsKey("ClassB"));
    }

    [Fact]
    public void 未回答なら列に値を出さない()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA")],
            Edges = [MappingEdge.Simple("n1", "n2")],
        };

        Assert.Empty(MappingEvaluator.Evaluate(graph, []));
    }

    [Fact]
    public void 選択肢をコードへ置き換える()
    {
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Transform("n2", TransformOperations.Map, ("map.とても満足", "5"), ("map.満足", "4")),
                Output("n3", "NumA"),
            ],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n3")],
        };

        var result = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "とても満足")]);

        Assert.Equal(["5"], result["NumA"].ToArray());
    }

    [Fact]
    public void 複数選択を連結して一つの列へ入れる()
    {
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Transform("n2", TransformOperations.Join, ("separator", ",")),
                Output("n3", "ClassA"),
            ],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n3")],
        };

        var result = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "A", "C")]);

        Assert.Equal(["A,C"], result["ClassA"].ToArray());
    }

    [Fact]
    public void 複数選択から複数のチェック列をONOFFする()
    {
        // **1 設問 = 1 列を前提にしない。** 1 入力から複数の列へ分岐する
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Transform("n2", TransformOperations.ToCheck, ("value", "A")),
                Transform("n3", TransformOperations.ToCheck, ("value", "B")),
                Transform("n4", TransformOperations.ToCheck, ("value", "C")),
                Output("o1", "CheckA"),
                Output("o2", "CheckB"),
                Output("o3", "CheckC"),
            ],
            Edges =
            [
                MappingEdge.Simple("n1", "n2"),
                MappingEdge.Simple("n1", "n3"),
                MappingEdge.Simple("n1", "n4"),
                MappingEdge.Simple("n2", "o1"),
                MappingEdge.Simple("n3", "o2"),
                MappingEdge.Simple("n4", "o3"),
            ],
        };

        var result = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "A", "C")]);

        Assert.Equal(["true"], result["CheckA"].ToArray());
        Assert.Equal(["false"], result["CheckB"].ToArray());
        Assert.Equal(["true"], result["CheckC"].ToArray());
    }

    [Fact]
    public void 文字列の内容を判定してフラグを立てる()
    {
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Transform("n2", TransformOperations.Contains, ("keyword", "至急")),
                Output("n3", "CheckD"),
            ],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n3")],
        };

        var hit = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "至急対応してほしい")]);
        var miss = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "特にありません")]);

        Assert.Equal(["true"], hit["CheckD"].ToArray());
        Assert.Equal(["false"], miss["CheckD"].ToArray());
    }

    [Fact]
    public void 条件で流す先を分ける()
    {
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Condition("c1", ConditionOperations.EqualsValue, ("value", "はい")),
                Output("o1", "ClassYes"),
                Output("o2", "ClassNo"),
            ],
            Edges =
            [
                MappingEdge.Simple("n1", "c1"),
                new MappingEdge("c1", ConditionPorts.True, "o1", "in"),
                new MappingEdge("c1", ConditionPorts.False, "o2", "in"),
            ],
        };

        var yes = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "はい")]);
        var no = MappingEvaluator.Evaluate(graph, [Answer.Of("q1", "いいえ")]);

        Assert.Equal(["はい"], yes["ClassYes"].ToArray());
        Assert.False(yes.ContainsKey("ClassNo"));

        Assert.Equal(["いいえ"], no["ClassNo"].ToArray());
        Assert.False(no.ContainsKey("ClassYes"));
    }

    [Fact]
    public void 循環参照があっても止まる()
    {
        // 保存時に弾いているが、**実行時に踏んでも無限ループさせない**
        var graph = new MappingGraph
        {
            Nodes =
            [
                Transform("n1", TransformOperations.Identity),
                Transform("n2", TransformOperations.Identity),
                Output("o1", "ClassA"),
            ],
            Edges =
            [
                MappingEdge.Simple("n1", "n2"),
                MappingEdge.Simple("n2", "n1"),
                MappingEdge.Simple("n2", "o1"),
            ],
        };

        var result = MappingEvaluator.Evaluate(graph, []);

        Assert.Empty(result);
    }

    [Fact]
    public void 同じ回答からは常に同じ結果が出る()
    {
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Transform("n2", TransformOperations.Join, ("separator", "/")),
                Output("n3", "ClassA"),
            ],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n3")],
        };
        var answers = new[] { Answer.Of("q1", "A", "B") };

        var first = MappingEvaluator.Evaluate(graph, answers);
        var second = MappingEvaluator.Evaluate(graph, answers);

        Assert.Equal(first["ClassA"].ToArray(), second["ClassA"].ToArray());
    }
}
