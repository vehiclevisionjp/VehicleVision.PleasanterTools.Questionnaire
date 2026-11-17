using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>公開前に見つかったルックアップの不備 1 件。**値・列の中身・秘密を持たない。**</summary>
/// <param name="LookupId">対象のルックアップ。</param>
/// <param name="Kind"><c>structure</c>（定義の構造）・<c>credential</c>・<c>source</c>・<c>column</c>。</param>
/// <param name="Code">不備の種類（列挙名）。</param>
/// <param name="Detail">補足（設問 ID・列名。検索値は入れない）。</param>
public sealed record LookupPublishProblem(string? LookupId, string Kind, string Code, string? Detail = null);

/// <summary>公開前のルックアップ検証（仕様書 3.2・8 章・実機検証結果 12.4）。</summary>
/// <remarks>
/// <para>
/// 構造（<see cref="LookupValidator"/>）→ 資格情報の所属と有効性 → 参照元の接続・統合/継承 →
/// 列の型（<see cref="LookupColumnValidator"/>）の順に見る。
/// **参照元の列定義を取得できなければ有効化しない**（標準の列数を仮定して通さない）。
/// </para>
/// <para>同じ（資格情報・サイト）は 1 回だけ点検する。ルックアップが無ければ外部を呼ばない。</para>
/// </remarks>
public sealed class LookupPublishValidator(
    ILookupCredentialStore credentials,
    ILookupSiteInspectionService inspector)
{
    public async Task<IReadOnlyList<LookupPublishProblem>> ValidateAsync(
        Guid surveyId,
        SurveyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var lookups = definition.GetLookups();
        if (lookups.IsEmpty)
        {
            return [];
        }

        var problems = new List<LookupPublishProblem>();
        foreach (var problem in LookupValidator.Validate(definition, lookups))
        {
            problems.Add(new LookupPublishProblem(
                problem.LookupId, "structure", problem.Code.ToString(), problem.Detail));
        }

        var inspections = new Dictionary<(string, long), LookupSiteInspection>();
        foreach (var lookup in lookups)
        {
            if (lookup.SourceKind != LookupSourceKind.Pleasanter
                || string.IsNullOrWhiteSpace(lookup.CredentialId)
                || lookup.SourceSiteId is not > 0)
            {
                continue;
            }

            var status = await credentials
                .FindStatusAsync(surveyId, lookup.CredentialId, cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                problems.Add(new LookupPublishProblem(lookup.LookupId, "credential", "CredentialNotFound"));
                continue;
            }

            if (!status.IsEnabled)
            {
                problems.Add(new LookupPublishProblem(lookup.LookupId, "credential", "CredentialDisabled"));
                continue;
            }

            var cacheKey = (lookup.CredentialId, lookup.SourceSiteId.Value);
            if (!inspections.TryGetValue(cacheKey, out var inspection))
            {
                try
                {
                    inspection = await inspector
                        .InspectAsync(surveyId, lookup.CredentialId, lookup.SourceSiteId.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (PleasanterConfigurationException)
                {
                    inspection = new LookupSiteInspection
                    {
                        Status = LookupSearchStatus.SourceInvalid,
                        Failure = LookupSearchFailure.InvalidDefinition,
                    };
                }

                inspections[cacheKey] = inspection;
            }

            if (!inspection.IsSuccess)
            {
                problems.Add(new LookupPublishProblem(
                    lookup.LookupId, "source", "SourceUnavailable", inspection.Failure.ToString()));
                continue;
            }

            if (inspection.IsIntegrated)
            {
                problems.Add(new LookupPublishProblem(lookup.LookupId, "source", "SourceSiteIntegrated"));
            }

            if (inspection.InheritsPermission)
            {
                problems.Add(new LookupPublishProblem(lookup.LookupId, "source", "SourceSiteInheritsPermission"));
            }

            foreach (var column in LookupColumnValidator.Validate(definition, lookup, inspection.Columns))
            {
                problems.Add(new LookupPublishProblem(
                    column.LookupId, "column", column.Code.ToString(), column.Detail));
            }
        }

        return problems;
    }
}
