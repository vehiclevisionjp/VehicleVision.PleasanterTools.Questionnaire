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
    string? ResponseJsonColumn);

/// <summary>版を指定してスナップショットを引く。</summary>
public interface ISurveySnapshotStore
{
    Task<SurveySnapshot?> FindAsync(
        Guid surveyId,
        int version,
        CancellationToken cancellationToken = default);
}
