using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>保存を断る不備 1 件。</summary>
/// <param name="MessageKey"><see cref="ServerMessageKeys"/> の鍵。</param>
/// <param name="Fields">原因の項目。</param>
public sealed record SaveRuleViolation(string MessageKey, IReadOnlyList<string> Fields);

/// <summary>
/// 下書きを保存する前に断る定義の不備（保存の口とインポートの口で共通。Issue #1009）。
/// </summary>
/// <remarks>
/// **設問の不備（公開のときに弾く）と違い、画面が作り得ない値や、
/// 運用側が許していない値**だけを見る。保存とインポートで線が割れると、
/// インポートが保存の関所を迂回する道になる。
/// </remarks>
public static class DefinitionSaveRules
{
    public static SaveRuleViolation? Check(SurveyDefinition definition, EmbedOptions embeds)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(embeds);

        if (!SupportedLanguages.All.Contains(definition.FallbackLanguage, StringComparer.Ordinal))
        {
            return new SaveRuleViolation(
                ServerMessageKeys.FallbackLanguageUnsupported, ["fallbackLanguage"]);
        }

        // **色は形を検査してからでないと通さない**（Issue #56）
        var invalidColors = definition.Theme?.InvalidColors() ?? [];
        if (!invalidColors.IsEmpty)
        {
            return new SaveRuleViolation(ServerMessageKeys.ThemeColorInvalid, [.. invalidColors]);
        }

        // **許していない配信元は通さない**（Issue #104 / #107）
        var blockedEmbeds = definition.AllQuestions
            .Where(question => question.Type == QuestionType.Embed)
            .Where(question => !embeds.IsAllowed(question.Settings.Embed?.Url))
            .Select(question => question.QuestionId)
            .ToArray();
        return blockedEmbeds.Length > 0
            ? new SaveRuleViolation(ServerMessageKeys.EmbedHostNotAllowed, blockedEmbeds)
            : null;
    }
}
