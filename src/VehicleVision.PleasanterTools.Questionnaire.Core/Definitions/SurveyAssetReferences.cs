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

    private static IEnumerable<string> Markups(SurveyDefinition definition)
    {
        if (definition.ConfirmationMessage is { } confirmation)
        {
            foreach (var language in confirmation.Languages)
            {
                yield return confirmation.Get(language);
            }
        }

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
}
