using VehicleVision.PleasanterTools.Questionnaire.Core.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>定義の記法から参照される自前資産を調べる。</summary>
public static class SurveyAssetReferences
{
    /// <summary>定義が指定した資産を参照しているか。</summary>
    public static bool Contains(SurveyDefinition definition, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Markups(definition)
            .SelectMany(markup => NoteMarkup.Parse(markup))
            .SelectMany(block => block.Inlines.Concat(
                block.Items.SelectMany(item => item.Inlines)))
            .Any(inline => inline.AssetId == assetId);
    }

    /// <summary>完了画面からだけ参照されるため、引換券を要する資産か。</summary>
    public static bool RequiresTicket(SurveyDefinition definition, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return ConfirmationMarkups(definition).Any(markup => Contains(markup, assetId))
            && !QuestionMarkups(definition).Any(markup => Contains(markup, assetId));
    }

    /// <summary>完了画面に配布資産が 1 件以上あるか。</summary>
    public static bool HasTicketedAssets(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var questionAssets = QuestionMarkups(definition)
            .SelectMany(AssetIds)
            .ToHashSet();
        return ConfirmationMarkups(definition)
            .SelectMany(AssetIds)
            .Any(assetId => !questionAssets.Contains(assetId));
    }

    private static IEnumerable<string> Markups(SurveyDefinition definition)
    {
        foreach (var markup in ConfirmationMarkups(definition))
        {
            yield return markup;
        }

        foreach (var markup in QuestionMarkups(definition))
        {
            yield return markup;
        }
    }

    private static IEnumerable<string> ConfirmationMarkups(SurveyDefinition definition)
    {
        if (definition.ConfirmationMessage is not { } confirmation)
        {
            yield break;
        }

        foreach (var language in confirmation.Languages)
        {
            yield return confirmation.Get(language);
        }
    }

    private static IEnumerable<string> QuestionMarkups(SurveyDefinition definition)
    {
        foreach (var question in definition.AllQuestions)
        {
            if (question.Description is null
                || (question.Type is not QuestionType.Note
                    && question.Settings.DescriptionFormat is not DescriptionFormat.Markup))
            {
                continue;
            }

            foreach (var language in question.Description.Languages)
            {
                yield return question.Description.Get(language);
            }
        }
    }

    private static bool Contains(string markup, Guid assetId) => AssetIds(markup).Contains(assetId);

    private static IEnumerable<Guid> AssetIds(string markup) =>
        NoteMarkup.Parse(markup)
            .SelectMany(block => block.Inlines.Concat(
                block.Items.SelectMany(item => item.Inlines)))
            .Where(inline => inline.AssetId is not null)
            .Select(inline => inline.AssetId!.Value);
}
