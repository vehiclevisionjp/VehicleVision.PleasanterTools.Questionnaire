using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>ほかのアンケートから設問を取り込むための変換（Issue #358）。</summary>
public static partial class QuestionImport
{
    /// <summary>設問を取り込み先で安全に使える形へ写す。</summary>
    /// <param name="source">取り込む設問。**元の値は変えない。**</param>
    /// <param name="existingQuestionIds">取り込み先ですでに使っている設問 ID。</param>
    /// <param name="newQuestionId">新しい設問 ID を作る関数。</param>
    public static QuestionImportResult Copy(
        IEnumerable<Question> source,
        IEnumerable<string> existingQuestionIds,
        Func<string> newQuestionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(existingQuestionIds);
        ArgumentNullException.ThrowIfNull(newQuestionId);

        var usedIds = existingQuestionIds.ToHashSet(StringComparer.Ordinal);
        var imported = ImmutableArray.CreateBuilder<Question>();
        var removedTransitions = 0;
        var removedConditions = 0;
        var removedAssetReferences = 0;

        foreach (var question in source)
        {
            var choices = question.Choices
                .Select(choice =>
                {
                    if (choice.Next is not null)
                    {
                        removedTransitions++;
                    }

                    return choice with { Next = null };
                })
                .ToImmutableArray();

            if (question.VisibleWhen is not null)
            {
                removedConditions++;
            }

            imported.Add(question with
            {
                QuestionId = NextUniqueId(usedIds, newQuestionId),
                Description = UsesAssetMarkup(question)
                    ? RemoveAssetReferences(question.Description, ref removedAssetReferences)
                    : question.Description,
                Choices = choices,
                VisibleWhen = null,
            });
        }

        return new QuestionImportResult(
            imported.ToImmutable(),
            removedTransitions,
            removedConditions,
            removedAssetReferences);
    }

    private static string NextUniqueId(HashSet<string> usedIds, Func<string> newQuestionId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = newQuestionId();
            if (!string.IsNullOrWhiteSpace(candidate) && usedIds.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("一意な設問 ID を作成できませんでした。");
    }

    private static bool UsesAssetMarkup(Question question) =>
        question.Type is QuestionType.Note
        || question.Settings.DescriptionFormat is DescriptionFormat.Markup;

    private static LocalizedText? RemoveAssetReferences(
        LocalizedText? description,
        ref int removedCount)
    {
        if (description is null)
        {
            return null;
        }

        var changed = false;
        var removedInDescription = 0;
        var byLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in description.Languages)
        {
            var text = description.Get(language);
            var withoutAssets = AssetMarkup().Replace(text, match =>
            {
                removedInDescription++;
                changed = true;
                return match.Groups["label"].Value;
            });
            byLanguage[language] = withoutAssets;
        }

        removedCount += removedInDescription;
        return changed ? new LocalizedText(byLanguage) : description;
    }

    [GeneratedRegex(
        @"!?\[(?<label>[^\]]*)\]\(asset:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AssetMarkup();
}

/// <summary>設問を取り込み用に写した結果。</summary>
/// <param name="Questions">取り込み先へ追加する設問。</param>
/// <param name="RemovedChoiceTransitions">除去した選択肢の分岐数。</param>
/// <param name="RemovedVisibilityConditions">除去した表示条件の数。</param>
/// <param name="RemovedAssetReferences">平文へ置き換えた資産参照の数。</param>
public sealed record QuestionImportResult(
    ImmutableArray<Question> Questions,
    int RemovedChoiceTransitions,
    int RemovedVisibilityConditions,
    int RemovedAssetReferences);
