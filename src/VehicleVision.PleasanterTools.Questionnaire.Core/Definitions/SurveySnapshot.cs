using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>公開時に固めた、ある版のアンケート一式。</summary>
/// <remarks>
/// **不変。** 編集は下書き側で行い、公開するまで実行へ影響させない
/// （<c>_documents/データモデル設計.md</c> 1 章）。
/// </remarks>
/// <param name="Definition">設問定義。</param>
/// <param name="Mapping">列への割り当て。</param>
/// <param name="PleasanterSiteId">書き込み先の Pleasanter サイト。</param>
/// <param name="ResponseJsonColumn">
/// 回答の正本を入れる列。**割り当ては任意**なので <c>null</c> があり得る。
/// <c>null</c> のとき、応答不明の <c>Create</c> を照合できない
/// （<c>_documents/アーキテクチャ方針.md</c> 9 章）。
/// </param>
public sealed record SurveySnapshot(
    SurveyDefinition Definition,
    MappingDefinition Mapping,
    long PleasanterSiteId,
    string? ResponseJsonColumn,
    long AssetHistorySiteId = 0,
    MappingDefinition? AssetHistoryMapping = null)
{
    public bool IsAssetHistoryEnabled =>
        AssetHistorySiteId > 0 && AssetHistoryMapping is { Assignments.Length: > 0 };
}

/// <summary>固めた版の一覧の 1 行。</summary>
/// <param name="Version">版。</param>
/// <param name="PublishedAt">固めた時刻（UTC）。</param>
public sealed record SurveyVersionSummary(int Version, DateTime PublishedAt);

/// <summary>版を指定してスナップショットを引く。</summary>
public interface ISurveySnapshotStore
{
    Task<SurveySnapshot?> FindAsync(
        Guid surveyId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 固めた版を新しい順に並べる（Issue #544）。管理画面が、過去の版を編集へ読み込むために使う。
    /// </summary>
    /// <remarks>
    /// 既定は空。**回答画面や送信ワーカーが使う実装（試験の偽物を含む）に、
    /// 管理画面だけの口を強いない**ための既定実装。
    /// </remarks>
    Task<IReadOnlyList<SurveyVersionSummary>> ListAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SurveyVersionSummary>>([]);
}
