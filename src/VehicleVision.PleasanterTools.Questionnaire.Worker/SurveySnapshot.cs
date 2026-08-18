using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>送信に必要な、ある版のアンケートの情報。</summary>
/// <remarks>
/// **公開時に固めた不変のスナップショット**（<c>_documents/データモデル設計.md</c> 1 章）。
/// 回答レコードが持つ版で引く。
/// </remarks>
/// <param name="Definition">設問定義。</param>
/// <param name="Mapping">列への割り当て。</param>
/// <param name="PleasanterSiteId">書き込み先の Pleasanter サイト。</param>
/// <param name="ResponseJsonColumn">
/// 回答の正本を入れる列。**割り当ては任意**なので <c>null</c> があり得る。
/// <c>null</c> のとき、応答不明の <c>Create</c> を照合できない。
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
