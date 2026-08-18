using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>マッピングのノード種別。</summary>
/// <remarks>
/// FBD 風のノードエディタへ載せ替えられるよう、**最初からノードとエッジで持つ**
/// （<c>_documents/データモデル設計.md</c> 2.4）。
/// </remarks>
public enum MappingNodeType
{
    /// <summary>入力。設問の回答を取り出す。**入り口なので入力エッジを持たない。**</summary>
    QuestionInput,

    /// <summary>変換。値を加工する。**1 入力 → 複数出力もある。**</summary>
    Transform,

    /// <summary>条件。回答に応じて流れを分ける。</summary>
    Condition,

    /// <summary>出力。Pleasanter の列へ書く。**出口なので出力エッジを持たない。**</summary>
    ColumnOutput,
}

/// <summary>マッピングのノード 1 つ。</summary>
public sealed record MappingNode
{
    public required string NodeId { get; init; }

    public required MappingNodeType Type { get; init; }

    /// <summary><see cref="MappingNodeType.QuestionInput"/> のときの設問 ID。</summary>
    public string? QuestionId { get; init; }

    /// <summary><see cref="MappingNodeType.ColumnOutput"/> のときの Pleasanter の列名。</summary>
    public string? ColumnName { get; init; }

    /// <summary><see cref="MappingNodeType.Transform"/> / <see cref="MappingNodeType.Condition"/> の種別。</summary>
    public string? Operation { get; init; }

    /// <summary>種別ごとの設定。</summary>
    public ImmutableDictionary<string, string> Config { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

/// <summary>ノードとノードのつながり。</summary>
/// <param name="FromNodeId">出力元。</param>
/// <param name="FromPort">出力元のポート名。条件ノードの分岐先を区別するのに使う。</param>
/// <param name="ToNodeId">入力先。</param>
/// <param name="ToPort">入力先のポート名。</param>
public sealed record MappingEdge(
    string FromNodeId,
    string FromPort,
    string ToNodeId,
    string ToPort)
{
    /// <summary>ポートを 1 つしか持たないノード同士をつなぐ。</summary>
    public static MappingEdge Simple(string fromNodeId, string toNodeId) =>
        new(fromNodeId, DefaultPort, toNodeId, DefaultPort);

    /// <summary>既定のポート名。</summary>
    public const string DefaultPort = "out";
}

/// <summary>設問から Pleasanter の列へのマッピング。</summary>
public sealed record MappingGraph
{
    public ImmutableArray<MappingNode> Nodes { get; init; } = [];

    public ImmutableArray<MappingEdge> Edges { get; init; } = [];

    public MappingNode? FindNode(string nodeId) =>
        Nodes.FirstOrDefault(node => node.NodeId == nodeId);
}
