using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>マッピングの不備。</summary>
public enum MappingProblemCode
{
    /// <summary>ノード ID が重複している。</summary>
    DuplicateNodeId,

    /// <summary>エッジが存在しないノードを指している。</summary>
    UnknownNodeReference,

    /// <summary>入力ノードに設問が設定されていない。</summary>
    MissingQuestionId,

    /// <summary>出力ノードに列名が設定されていない。</summary>
    MissingColumnName,

    /// <summary>定義に存在しない設問を指している。</summary>
    QuestionNotInDefinition,

    /// <summary>入力ノードへ入力エッジが繋がっている。</summary>
    InputHasIncomingEdge,

    /// <summary>出力ノードから出力エッジが出ている。</summary>
    OutputHasOutgoingEdge,

    /// <summary>循環参照がある。**保存を拒否する。**</summary>
    CircularReference,

    /// <summary>同じ列へ複数の出力が繋がっている。</summary>
    DuplicateColumnOutput,

    /// <summary>どこにも繋がっていない出力ノード。**拒否はしないが警告する。**</summary>
    UnconnectedOutput,

    /// <summary>どこへも繋がっていない設問。**拒否はしないが警告する。**</summary>
    UnconnectedQuestion,
}

/// <summary>マッピングの不備 1 件。</summary>
/// <param name="Code">不備の種別。</param>
/// <param name="NodeId">対象のノード。全体に対するものでは <c>null</c>。</param>
/// <param name="Detail">補足。</param>
public sealed record MappingProblem(MappingProblemCode Code, string? NodeId, string? Detail = null)
{
    /// <summary>保存を拒否すべき不備か。</summary>
    /// <remarks>
    /// 未接続は拒否しない。**「この設問は Pleasanter に残らない」と伝えたうえで保存させる**
    /// （<c>_documents/画面設計.md</c> 2 章）。
    /// </remarks>
    public bool IsBlocking =>
        Code is not (MappingProblemCode.UnconnectedOutput or MappingProblemCode.UnconnectedQuestion);
}

/// <summary>マッピングを保存する前に検査する。</summary>
public static class MappingGraphValidator
{
    public static ImmutableArray<MappingProblem> Validate(
        MappingGraph graph,
        SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(definition);

        var problems = ImmutableArray.CreateBuilder<MappingProblem>();

        ValidateNodes(graph, definition, problems);
        ValidateEdges(graph, problems);
        ValidateConnectivity(graph, definition, problems);
        DetectCycles(graph, problems);

        return problems.ToImmutable();
    }

    private static void ValidateNodes(
        MappingGraph graph,
        SurveyDefinition definition,
        ImmutableArray<MappingProblem>.Builder problems)
    {
        foreach (var duplicate in graph.Nodes
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            problems.Add(new MappingProblem(MappingProblemCode.DuplicateNodeId, duplicate.Key));
        }

        foreach (var node in graph.Nodes)
        {
            switch (node.Type)
            {
                case MappingNodeType.QuestionInput when string.IsNullOrEmpty(node.QuestionId):
                    problems.Add(new MappingProblem(MappingProblemCode.MissingQuestionId, node.NodeId));
                    break;

                case MappingNodeType.QuestionInput
                    when definition.FindQuestion(node.QuestionId!) is null:
                    problems.Add(new MappingProblem(
                        MappingProblemCode.QuestionNotInDefinition, node.NodeId, node.QuestionId));
                    break;

                case MappingNodeType.ColumnOutput when string.IsNullOrEmpty(node.ColumnName):
                    problems.Add(new MappingProblem(MappingProblemCode.MissingColumnName, node.NodeId));
                    break;

                default:
                    break;
            }
        }

        foreach (var duplicate in graph.Nodes
            .Where(node => node.Type is MappingNodeType.ColumnOutput
                && !string.IsNullOrEmpty(node.ColumnName))
            .GroupBy(node => node.ColumnName!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            problems.Add(new MappingProblem(
                MappingProblemCode.DuplicateColumnOutput, null, duplicate.Key));
        }
    }

    private static void ValidateEdges(
        MappingGraph graph,
        ImmutableArray<MappingProblem>.Builder problems)
    {
        foreach (var edge in graph.Edges)
        {
            var from = graph.FindNode(edge.FromNodeId);
            var to = graph.FindNode(edge.ToNodeId);

            if (from is null)
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.UnknownNodeReference, edge.FromNodeId));
            }

            if (to is null)
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.UnknownNodeReference, edge.ToNodeId));
            }

            if (from is { Type: MappingNodeType.ColumnOutput })
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.OutputHasOutgoingEdge, from.NodeId));
            }

            if (to is { Type: MappingNodeType.QuestionInput })
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.InputHasIncomingEdge, to.NodeId));
            }
        }
    }

    private static void ValidateConnectivity(
        MappingGraph graph,
        SurveyDefinition definition,
        ImmutableArray<MappingProblem>.Builder problems)
    {
        var hasIncoming = graph.Edges.Select(edge => edge.ToNodeId).ToHashSet(StringComparer.Ordinal);
        var hasOutgoing = graph.Edges.Select(edge => edge.FromNodeId).ToHashSet(StringComparer.Ordinal);

        foreach (var node in graph.Nodes.Where(node => node.Type is MappingNodeType.ColumnOutput))
        {
            if (!hasIncoming.Contains(node.NodeId))
            {
                // **未接続の出力列は触らない。** 空文字で上書きしない
                problems.Add(new MappingProblem(MappingProblemCode.UnconnectedOutput, node.NodeId));
            }
        }

        foreach (var node in graph.Nodes.Where(node => node.Type is MappingNodeType.QuestionInput))
        {
            if (!hasOutgoing.Contains(node.NodeId))
            {
                problems.Add(new MappingProblem(MappingProblemCode.UnconnectedQuestion, node.NodeId));
            }
        }

        // 定義にあるのにマッピングへ現れない設問も、回答が Pleasanter に残らない
        var mapped = graph.Nodes
            .Where(node => node.Type is MappingNodeType.QuestionInput && node.QuestionId is not null)
            .Select(node => node.QuestionId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var question in definition.AllQuestions.Where(question => !question.IsDisplayOnly))
        {
            if (!mapped.Contains(question.QuestionId))
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.UnconnectedQuestion, null, question.QuestionId));
            }
        }
    }

    /// <summary>循環参照を検出する。**実行時に無限ループさせないため、保存時に弾く。**</summary>
    private static void DetectCycles(
        MappingGraph graph,
        ImmutableArray<MappingProblem>.Builder problems)
    {
        var outgoing = graph.Edges
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.ToNodeId).ToArray(),
                StringComparer.Ordinal);

        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            Visit(node.NodeId);
        }

        void Visit(string nodeId)
        {
            if (state.TryGetValue(nodeId, out var current))
            {
                if (current is VisitState.Visiting && reported.Add(nodeId))
                {
                    problems.Add(new MappingProblem(MappingProblemCode.CircularReference, nodeId));
                }

                return;
            }

            state[nodeId] = VisitState.Visiting;

            if (outgoing.TryGetValue(nodeId, out var nexts))
            {
                foreach (var next in nexts)
                {
                    Visit(next);
                }
            }

            state[nodeId] = VisitState.Visited;
        }
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
