using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Mapping;

public class MappingGraphValidatorTests
{
    private static SurveyDefinition Definition(params string[] questionIds) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    .. questionIds.Select(id => new Question
                    {
                        QuestionId = id,
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese(id),
                    }),
                ],
            },
        ],
    };

    private static MappingNode Input(string nodeId, string questionId) => new()
    {
        NodeId = nodeId,
        Type = MappingNodeType.QuestionInput,
        QuestionId = questionId,
    };

    private static MappingNode Transform(string nodeId, string operation = "identity") => new()
    {
        NodeId = nodeId,
        Type = MappingNodeType.Transform,
        Operation = operation,
    };

    private static MappingNode Output(string nodeId, string columnName) => new()
    {
        NodeId = nodeId,
        Type = MappingNodeType.ColumnOutput,
        ColumnName = columnName,
    };

    private static MappingProblemCode[] Codes(ImmutableArray<MappingProblem> problems) =>
        [.. problems.Select(problem => problem.Code)];

    [Fact]
    public void 設問から列まで繋がっていれば不備なし()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Transform("n2"), Output("n3", "ClassA")],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n3")],
        };

        Assert.Empty(MappingGraphValidator.Validate(graph, Definition("q1")));
    }

    [Fact]
    public void 循環参照を検出する()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Transform("n2"), Transform("n3"), Output("n4", "ClassA")],
            Edges =
            [
                MappingEdge.Simple("n1", "n2"),
                MappingEdge.Simple("n2", "n3"),
                MappingEdge.Simple("n3", "n2"),
                MappingEdge.Simple("n3", "n4"),
            ],
        };

        var problems = MappingGraphValidator.Validate(graph, Definition("q1"));

        Assert.Contains(MappingProblemCode.CircularReference, Codes(problems));
    }

    [Fact]
    public void 循環参照は保存を拒否する不備として扱う()
    {
        var graph = new MappingGraph
        {
            Nodes = [Transform("n1"), Transform("n2")],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n1")],
        };

        var problems = MappingGraphValidator.Validate(graph, Definition());

        Assert.Contains(
            problems,
            problem => problem.Code is MappingProblemCode.CircularReference && problem.IsBlocking);
    }

    [Fact]
    public void 自己参照も循環として検出する()
    {
        var graph = new MappingGraph
        {
            Nodes = [Transform("n1")],
            Edges = [MappingEdge.Simple("n1", "n1")],
        };

        Assert.Contains(
            MappingProblemCode.CircularReference,
            Codes(MappingGraphValidator.Validate(graph, Definition())));
    }

    [Fact]
    public void 存在しないノードを指すエッジを検出する()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1")],
            Edges = [MappingEdge.Simple("n1", "存在しない")],
        };

        Assert.Contains(
            MappingProblemCode.UnknownNodeReference,
            Codes(MappingGraphValidator.Validate(graph, Definition("q1"))));
    }

    [Fact]
    public void 定義に無い設問を指す入力を検出する()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "存在しない設問"), Output("n2", "ClassA")],
            Edges = [MappingEdge.Simple("n1", "n2")],
        };

        Assert.Contains(
            MappingProblemCode.QuestionNotInDefinition,
            Codes(MappingGraphValidator.Validate(graph, Definition())));
    }

    [Fact]
    public void 入力ノードへ入力エッジが来たら不備()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Transform("n2")],
            Edges = [MappingEdge.Simple("n2", "n1")],
        };

        Assert.Contains(
            MappingProblemCode.InputHasIncomingEdge,
            Codes(MappingGraphValidator.Validate(graph, Definition("q1"))));
    }

    [Fact]
    public void 出力ノードから出力エッジが出ていたら不備()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA"), Transform("n3")],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n2", "n3")],
        };

        Assert.Contains(
            MappingProblemCode.OutputHasOutgoingEdge,
            Codes(MappingGraphValidator.Validate(graph, Definition("q1"))));
    }

    [Fact]
    public void 同じ列へ複数の出力が繋がっていたら不備()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA"), Output("n3", "ClassA")],
            Edges = [MappingEdge.Simple("n1", "n2"), MappingEdge.Simple("n1", "n3")],
        };

        Assert.Contains(
            MappingProblemCode.DuplicateColumnOutput,
            Codes(MappingGraphValidator.Validate(graph, Definition("q1"))));
    }

    [Fact]
    public void 未接続の設問は警告するが保存は拒否しない()
    {
        // **「この設問は Pleasanter に残らない」と伝えたうえで保存させる**
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA")],
            Edges = [MappingEdge.Simple("n1", "n2")],
        };

        var problems = MappingGraphValidator.Validate(graph, Definition("q1", "q2"));

        Assert.Contains(
            problems,
            problem => problem.Code is MappingProblemCode.UnconnectedQuestion && !problem.IsBlocking);
        Assert.DoesNotContain(problems, problem => problem.IsBlocking);
    }

    [Fact]
    public void 未接続の出力列は警告するが保存は拒否しない()
    {
        var graph = new MappingGraph
        {
            Nodes = [Input("n1", "q1"), Output("n2", "ClassA"), Output("n3", "ClassB")],
            Edges = [MappingEdge.Simple("n1", "n2")],
        };

        var problems = MappingGraphValidator.Validate(graph, Definition("q1"));

        Assert.Contains(
            problems,
            problem => problem.Code is MappingProblemCode.UnconnectedOutput && !problem.IsBlocking);
        Assert.DoesNotContain(problems, problem => problem.IsBlocking);
    }

    [Fact]
    public void 説明文ブロックは未接続でも警告しない()
    {
        // Note は Pleasanter の列へ写さない要素なので、繋がっていなくて当然
        var definition = new SurveyDefinition
        {
            SurveyId = "s1",
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Pages =
            [
                new Page
                {
                    PageId = "p1",
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "note1",
                            Type = QuestionType.Note,
                            Title = LocalizedText.Japanese("説明"),
                        },
                    ],
                },
            ],
        };

        Assert.Empty(MappingGraphValidator.Validate(new MappingGraph(), definition));
    }

    [Fact]
    public void 一つの入力から複数の列へ分岐できる()
    {
        // 複数選択から複数のチェック列を ON/OFF する形。**1 設問 = 1 列を前提にしない**
        var graph = new MappingGraph
        {
            Nodes =
            [
                Input("n1", "q1"),
                Transform("n2", "toCheckColumns"),
                Output("n3", "CheckA"),
                Output("n4", "CheckB"),
            ],
            Edges =
            [
                MappingEdge.Simple("n1", "n2"),
                new MappingEdge("n2", "a", "n3", "in"),
                new MappingEdge("n2", "b", "n4", "in"),
            ],
        };

        Assert.Empty(MappingGraphValidator.Validate(graph, Definition("q1")));
    }
}
