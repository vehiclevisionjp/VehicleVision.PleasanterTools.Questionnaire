using System.Collections.Immutable;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Answers;

/// <summary>設問ごとの指定に従って自由入力の回答を変換する。</summary>
public static class AnswerNormalizer
{
    private static readonly IReadOnlyDictionary<string, string> HalfWidthKana = BuildHalfWidthKana();

    /// <summary>アンケート定義に従って回答を変換する。</summary>
    public static IReadOnlyCollection<Answer> Normalize(
        SurveyDefinition definition,
        IReadOnlyCollection<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(answers);

        var questions = definition.AllQuestions.ToDictionary(
            question => question.QuestionId,
            StringComparer.Ordinal);

        return
        [
            .. answers.Select(answer =>
                questions.TryGetValue(answer.QuestionId, out var question)
                && question.Type is QuestionType.Text or QuestionType.Paragraph
                    ? answer with
                    {
                        Values =
                        [
                            .. answer.Values.Select(value => Normalize(value, question.Settings)),
                        ],
                    }
                    : answer),
        ];
    }

    /// <summary>1 つの文字列を指定に従って変換する。</summary>
    public static string Normalize(string value, QuestionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = value;

        if (settings.ConvertFullWidthAsciiToHalfWidth)
        {
            normalized = ConvertFullWidthAscii(normalized);
        }

        if (settings.ConvertHalfWidthKanaToFullWidth)
        {
            normalized = ConvertHalfWidthKana(normalized);
        }

        if (settings.ConvertFullWidthSpacesToHalfWidth)
        {
            normalized = normalized.Replace('\u3000', ' ');
        }

        if (settings.TrimWhitespace)
        {
            normalized = normalized.Trim();
        }

        return normalized;
    }

    private static string ConvertFullWidthAscii(string value)
    {
        var converted = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            converted.Append(character is >= '\uFF01' and <= '\uFF5E'
                ? (char)(character - 0xFEE0)
                : character);
        }

        return converted.ToString();
    }

    private static string ConvertHalfWidthKana(string value)
    {
        var converted = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++)
        {
            var pair = index + 1 < value.Length
                ? value.Substring(index, 2)
                : string.Empty;

            if (pair.Length > 0 && HalfWidthKana.TryGetValue(pair, out var paired))
            {
                converted.Append(paired);
                index++;
                continue;
            }

            var single = value[index].ToString();
            converted.Append(HalfWidthKana.TryGetValue(single, out var replacement)
                ? replacement
                : single);
        }

        return converted.ToString();
    }

    private static IReadOnlyDictionary<string, string> BuildHalfWidthKana()
    {
        const string halfWidth =
            "｡｢｣､･ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝﾞﾟ";
        const string fullWidth =
            "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜";

        var mappings = halfWidth
            .Select((character, index) => (Key: character.ToString(), Value: fullWidth[index].ToString()))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        const string voicedBase = "ｳｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾊﾋﾌﾍﾎ";
        const string voiced = "ヴガギグゲゴザジズゼゾダヂヅデドバビブベボ";
        for (var index = 0; index < voicedBase.Length; index++)
        {
            mappings[$"{voicedBase[index]}ﾞ"] = voiced[index].ToString();
        }

        const string semiVoicedBase = "ﾊﾋﾌﾍﾎ";
        const string semiVoiced = "パピプペポ";
        for (var index = 0; index < semiVoicedBase.Length; index++)
        {
            mappings[$"{semiVoicedBase[index]}ﾟ"] = semiVoiced[index].ToString();
        }

        return mappings;
    }
}
