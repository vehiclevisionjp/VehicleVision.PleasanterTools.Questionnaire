using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Flow;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>公開前の検査で見つかった、定義そのものの不備（Issue #1009）。</summary>
/// <param name="MappingProblems">マッピングの不備（警告を含む）。</param>
/// <param name="Flow">分岐の不備。</param>
/// <param name="AutoReply">自動返信の不備。</param>
/// <param name="AssetTicketDaysInvalid">配布資産の引換券の日数が範囲外。</param>
/// <param name="Settings">設問の設定の不備。</param>
/// <param name="LookupStructure">ルックアップの構造の不備。</param>
/// <param name="NoAnswerableQuestion">答えられる設問が 1 つも無い。</param>
public sealed record PublishInspection(
    ImmutableArray<MappingProblem> MappingProblems,
    ImmutableArray<FlowProblem> Flow,
    ImmutableArray<AutoReplyProblem> AutoReply,
    bool AssetTicketDaysInvalid,
    ImmutableArray<SettingsProblem> Settings,
    ImmutableArray<LookupProblem> LookupStructure,
    bool NoAnswerableQuestion)
{
    /// <summary>公開・取り込みを止める不備が 1 つでもあるか。</summary>
    public bool HasBlocking =>
        MappingProblems.Any(problem => problem.IsBlocking)
        || !Flow.IsEmpty
        || !AutoReply.IsEmpty
        || AssetTicketDaysInvalid
        || !Settings.IsEmpty
        || !LookupStructure.IsEmpty
        || NoAnswerableQuestion;
}

/// <summary>
/// 公開前の検査のうち、**定義とマッピングだけで決まるもの**（Issue #1009）。
/// </summary>
/// <remarks>
/// <para>
/// **テスト公開と取り込みが同じ検査を通る。** 取り込みの側に写しを持つと、
/// 検査を足したときに片方だけ漏れ、**取り込めたのに公開できない**定義が出る。
/// </para>
/// <para>
/// 外部（Pleasanter・資格情報の保管）を呼ぶ検査は含めない
/// （<see cref="LookupPublishValidator"/>）。取り込みでは資格情報を運ばないので、
/// その検査は公開のときに行う。
/// </para>
/// </remarks>
public static class PublishInspector
{
    public static PublishInspection Inspect(SurveyDefinition definition, MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(mapping);

        var lookups = definition.GetLookups();
        return new PublishInspection(
            MappingValidator.Validate(
                mapping,
                definition,
                null,
                PleasanterColumn.IsAttachment,
                column => PleasanterColumn.RecordPropertyOf(column)?.ValueKind),
            SurveyFlowValidator.Validate(definition),
            AutoReplyValidator.Validate(definition),
            definition.AssetDelivery is
            {
                Expiration: AssetTicketExpiration.DaysAfterResponse,
                Days: < 1 or > AssetDeliverySettings.MaxDays,
            },
            QuestionSettingsValidator.Validate(definition),
            lookups.IsEmpty ? [] : LookupValidator.Validate(definition, lookups),
            definition.AllQuestions.All(question => question.IsDisplayOnly));
    }
}
