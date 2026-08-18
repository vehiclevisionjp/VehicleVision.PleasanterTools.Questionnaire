using System.Collections.Immutable;
using System.Globalization;
using System.Net.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>回答が定義に沿っているかを検証する。</summary>
/// <remarks>
/// **サーバ側で必ず通すこと。** 画面側の検証は体験のためだけのもの
/// （<c>_documents/アプリケーション設計.md</c> 6 章）。
/// 判定規則はスナップショットの定義から導く。画面とサーバで規則を二重に書かない。
/// </remarks>
public static class AnswerValidator
{
    /// <summary>アンケート全体を検証する。</summary>
    public static ImmutableArray<ValidationError> Validate(
        SurveyDefinition definition,
        IReadOnlyCollection<Answer> answers)
        => Validate(definition, answers, pageId: null);

    /// <summary>1 ページ分だけ検証する。ページ遷移時に使う。</summary>
    /// <remarks>
    /// 必須チェックを最後にまとめて出すと、どのページへ戻ればよいか分からなくなる
    /// （<c>_documents/画面設計.md</c> 1 章）。
    /// </remarks>
    public static ImmutableArray<ValidationError> ValidatePage(
        SurveyDefinition definition,
        string pageId,
        IReadOnlyCollection<Answer> answers)
        => Validate(definition, answers, pageId);

    private static ImmutableArray<ValidationError> Validate(
        SurveyDefinition definition,
        IReadOnlyCollection<Answer> answers,
        string? pageId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(answers);

        var errors = ImmutableArray.CreateBuilder<ValidationError>();
        var answerByQuestion = answers
            .GroupBy(answer => answer.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var targets = pageId is null
            ? definition.AllQuestions.ToList()
            : definition.Pages
                .Where(page => page.PageId == pageId)
                .SelectMany(page => page.Questions)
                .ToList();

        // 定義に無い設問への回答は受け取らない
        var known = definition.AllQuestions
            .Select(question => question.QuestionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var answer in answers.Where(answer => !known.Contains(answer.QuestionId)))
        {
            errors.Add(new ValidationError(answer.QuestionId, ValidationErrorCode.UnknownQuestion));
        }

        foreach (var question in targets)
        {
            answerByQuestion.TryGetValue(question.QuestionId, out var answer);
            ValidateQuestion(question, answer, errors);
        }

        return errors.ToImmutable();
    }

    private static void ValidateQuestion(
        Question question,
        Answer? answer,
        ImmutableArray<ValidationError>.Builder errors)
    {
        if (question.IsDisplayOnly)
        {
            if (answer is not null && !answer.IsEmpty)
            {
                errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.AnswerNotAllowed));
            }

            return;
        }

        if (answer is null || answer.IsEmpty)
        {
            if (question.IsRequired)
            {
                errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.Required));
            }

            return;
        }

        if (!question.IsMultiValue && answer.Values.Length > 1)
        {
            errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.MultipleValuesNotAllowed));
            return;
        }

        if (question.HasChoices)
        {
            ValidateChoices(question, answer, errors);
            return;
        }

        foreach (var value in answer.Values)
        {
            ValidateScalar(question, value, errors);
        }
    }

    private static void ValidateChoices(
        Question question,
        Answer answer,
        ImmutableArray<ValidationError>.Builder errors)
    {
        var otherSelected = false;

        foreach (var value in answer.Values)
        {
            var choice = question.Choices.FirstOrDefault(candidate => candidate.Value == value);
            if (choice is null)
            {
                errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.UnknownChoice, value));
                continue;
            }

            otherSelected |= choice.IsOther;
        }

        // 「その他」を選んでいないのに自由記述だけ送られてきたら受け取らない
        if (!otherSelected && !string.IsNullOrWhiteSpace(answer.OtherText))
        {
            errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.OtherTextNotAllowed));
        }
    }

    private static void ValidateScalar(
        Question question,
        string value,
        ImmutableArray<ValidationError>.Builder errors)
    {
        var settings = question.Settings;

        switch (question.Type)
        {
            case QuestionType.Text:
            case QuestionType.Paragraph:
                if (settings.MaxLength is { } maxLength && value.Length > maxLength)
                {
                    errors.Add(new ValidationError(
                        question.QuestionId,
                        ValidationErrorCode.TooLong,
                        maxLength.ToString(CultureInfo.InvariantCulture)));
                }

                ValidateFormat(question, value, errors);
                break;

            case QuestionType.Scale:
            case QuestionType.Rating:
                ValidateNumber(question, value, settings.ScaleMinimum, settings.ScaleMaximum, errors);
                break;

            case QuestionType.Date:
                if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _))
                {
                    errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.NotADateTime));
                }

                break;

            case QuestionType.Time:
                if (!TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out _))
                {
                    errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.NotADateTime));
                }

                break;

            default:
                break;
        }
    }

    private static void ValidateFormat(
        Question question,
        string value,
        ImmutableArray<ValidationError>.Builder errors)
    {
        switch (question.Settings.Format)
        {
            case TextFormat.Email:
                // **正規表現を使わない。** ReDoS を持ち込まないため
                if (!MailAddress.TryCreate(value, out _))
                {
                    errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.InvalidEmail));
                }

                break;

            case TextFormat.Url:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.InvalidUrl));
                }

                break;

            default:
                break;
        }
    }

    private static void ValidateNumber(
        Question question,
        string value,
        int? minimum,
        int? maximum,
        ImmutableArray<ValidationError>.Builder errors)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.NotANumber));
            return;
        }

        if ((minimum is { } min && number < min) || (maximum is { } max && number > max))
        {
            errors.Add(new ValidationError(question.QuestionId, ValidationErrorCode.OutOfRange));
        }
    }
}
