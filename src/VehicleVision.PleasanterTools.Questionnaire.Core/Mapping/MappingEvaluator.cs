using System.Collections.Immutable;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>変換ノードの種別。</summary>
public static class TransformOperations
{
    /// <summary>そのまま通す。</summary>
    public const string Identity = "identity";

    /// <summary>複数の値を連結して 1 つにする。設定 <c>separator</c>。</summary>
    public const string Join = "join";

    /// <summary>値を別の値へ置き換える。設定 <c>map.{元の値}</c> = 置き換え後。</summary>
    public const string Map = "map";

    /// <summary>設定 <c>value</c> が含まれていれば <c>true</c>。チェック列向け。</summary>
    public const string ToCheck = "toCheck";

    /// <summary>設定 <c>keyword</c> を含む文字列があれば <c>true</c>。</summary>
    public const string Contains = "contains";

    /// <summary>入力によらず設定 <c>value</c> を出す。</summary>
    public const string Constant = "constant";
}

/// <summary>条件ノードの種別。</summary>
public static class ConditionOperations
{
    /// <summary>設定 <c>value</c> と一致するか。</summary>
    public const string EqualsValue = "equals";
}

/// <summary>条件ノードの出力ポート。</summary>
public static class ConditionPorts
{
    public const string True = "true";
    public const string False = "false";
}

/// <summary>マッピングを適用して、列名と値の対応を作る。</summary>
/// <remarks>
/// **評価は決定的にする。** 同じ回答からは常に同じ列の値が出ること
/// （<c>_documents/アプリケーション設計.md</c> 7 章）。
/// 値は文字列のまま返す。Pleasanter の型（Class / Num / Date …）への変換は
/// <c>.Pleasanter</c> 側の責務。
/// </remarks>
public static class MappingEvaluator
{
    /// <summary>マッピングを適用する。</summary>
    /// <returns>
    /// 列名 → 値。**流れてこなかった列は含めない。**
    /// 未接続の出力列を空文字で上書きしないため。
    /// </returns>
    public static ImmutableDictionary<string, ImmutableArray<string>> Evaluate(
        MappingGraph graph,
        IReadOnlyCollection<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(answers);

        var answerByQuestion = answers
            .GroupBy(answer => answer.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // ノード ID → ポート名 → 値
        var outputs = new Dictionary<string, Dictionary<string, ImmutableArray<string>>>(
            StringComparer.Ordinal);
        var evaluating = new HashSet<string>(StringComparer.Ordinal);

        var results = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var output in graph.Nodes.Where(node => node.Type is MappingNodeType.ColumnOutput))
        {
            if (string.IsNullOrEmpty(output.ColumnName))
            {
                continue;
            }

            var values = Incoming(output.NodeId);
            if (!values.IsDefaultOrEmpty)
            {
                results[output.ColumnName] = values;
            }
        }

        return results.ToImmutable();

        // 指定したノードへ流れ込む値を集める
        ImmutableArray<string> Incoming(string nodeId)
        {
            var collected = ImmutableArray.CreateBuilder<string>();

            foreach (var edge in graph.Edges.Where(edge => edge.ToNodeId == nodeId))
            {
                var fromValues = EvaluateNode(edge.FromNodeId, edge.FromPort);
                if (!fromValues.IsDefaultOrEmpty)
                {
                    collected.AddRange(fromValues);
                }
            }

            return collected.ToImmutable();
        }

        // 指定したノードの、指定したポートの出力を返す
        ImmutableArray<string> EvaluateNode(string nodeId, string port)
        {
            if (outputs.TryGetValue(nodeId, out var cached)
                && cached.TryGetValue(port, out var cachedValues))
            {
                return cachedValues;
            }

            // 循環参照は保存時に弾いている。**実行時に踏んだら空を返して止める**
            if (!evaluating.Add(nodeId))
            {
                return [];
            }

            try
            {
                var node = graph.FindNode(nodeId);
                if (node is null)
                {
                    return [];
                }

                var byPort = ComputeNode(node);
                outputs[nodeId] = byPort;
                return byPort.TryGetValue(port, out var values) ? values : [];
            }
            finally
            {
                evaluating.Remove(nodeId);
            }
        }

        Dictionary<string, ImmutableArray<string>> ComputeNode(MappingNode node)
        {
            switch (node.Type)
            {
                case MappingNodeType.QuestionInput:
                    var answer = node.QuestionId is not null
                        && answerByQuestion.TryGetValue(node.QuestionId, out var found)
                            ? found
                            : null;
                    return Single(answer is null ? [] : answer.Values);

                case MappingNodeType.Transform:
                    return Single(ApplyTransform(node, Incoming(node.NodeId)));

                case MappingNodeType.Condition:
                    return ApplyCondition(node, Incoming(node.NodeId));

                default:
                    return Single(Incoming(node.NodeId));
            }
        }

        static Dictionary<string, ImmutableArray<string>> Single(ImmutableArray<string> values) =>
            new(StringComparer.Ordinal) { [MappingEdge.DefaultPort] = values };
    }

    private static ImmutableArray<string> ApplyTransform(
        MappingNode node,
        ImmutableArray<string> input)
    {
        switch (node.Operation)
        {
            case TransformOperations.Join:
                if (input.IsDefaultOrEmpty)
                {
                    return [];
                }

                var separator = node.Config.GetValueOrDefault("separator", ",");
                return [string.Join(separator, input)];

            case TransformOperations.Map:
                return
                [
                    .. input.Select(value =>
                        node.Config.TryGetValue($"map.{value}", out var mapped) ? mapped : value),
                ];

            case TransformOperations.ToCheck:
                var target = node.Config.GetValueOrDefault("value", string.Empty);
                return [Bool(input.Contains(target, StringComparer.Ordinal))];

            case TransformOperations.Contains:
                var keyword = node.Config.GetValueOrDefault("keyword", string.Empty);
                return
                [
                    Bool(!string.IsNullOrEmpty(keyword)
                        && input.Any(value => value.Contains(keyword, StringComparison.Ordinal))),
                ];

            case TransformOperations.Constant:
                return [node.Config.GetValueOrDefault("value", string.Empty)];

            case TransformOperations.Identity:
            default:
                return input;
        }
    }

    private static Dictionary<string, ImmutableArray<string>> ApplyCondition(
        MappingNode node,
        ImmutableArray<string> input)
    {
        var matched = node.Operation switch
        {
            ConditionOperations.EqualsValue =>
                input.Contains(node.Config.GetValueOrDefault("value", string.Empty), StringComparer.Ordinal),
            _ => false,
        };

        return new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            [ConditionPorts.True] = matched ? input : [],
            [ConditionPorts.False] = matched ? [] : input,
        };
    }

    private static string Bool(bool value) =>
        value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
}
