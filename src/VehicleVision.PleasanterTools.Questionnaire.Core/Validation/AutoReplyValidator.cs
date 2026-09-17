using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>自動返信の設定の不備（Issue #189）。</summary>
public enum AutoReplyProblemCode
{
    /// <summary>有効なのに、宛先にする設問が指定されていない。</summary>
    ToQuestionMissing,

    /// <summary>指定された設問が存在しない。**消した設問を指したまま。**</summary>
    ToQuestionNotFound,

    /// <summary>
    /// 指定された設問がメール形式ではない。
    /// **宛先にならない値が入り得る**ので、公開の前に止める。
    /// </summary>
    ToQuestionNotEmail,

    /// <summary>件名が空。</summary>
    SubjectMissing,

    /// <summary>本文が空。</summary>
    BodyMissing,

    /// <summary>再編集リンクの有効日数が範囲外（Issue #202）。</summary>
    EditLinkDaysInvalid,

    /// <summary>再編集を許していないため、再編集リンクのキーワードを使えない。</summary>
    EditLinkKeywordUnavailable,

    /// <summary>メールで渡せる配布物が無いため、配布リンクのキーワードを使えない。</summary>
    AssetsUrlKeywordUnavailable,
}

/// <summary>自動返信の設定の不備 1 件。</summary>
/// <param name="Code">不備の種類。</param>
/// <param name="Detail">補足（**宛先そのものは入れない**）。</param>
public sealed record AutoReplyProblem(AutoReplyProblemCode Code, string? Detail = null);

/// <summary>自動返信の設定が送れる形になっているかを見る（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **公開の前に止める。** 有効にしたのに送れない設定のまま公開すると、
/// **「送ったつもりで 1 通も出ていない」**状態になり、
/// 気付くのは回答者から問い合わせが来たときになる。
/// </para>
/// <para>
/// **無効なら何も見ない。** 使わない設定の不備で公開を止めない
/// （<c>MailOptions</c> が無効なときに他の設定を読まないのと同じ）。
/// </para>
/// </remarks>
public static class AutoReplyValidator
{
    /// <summary>アンケート全体の自動返信の設定を見る。</summary>
    public static ImmutableArray<AutoReplyProblem> Validate(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var settings = definition.AutoReply;
        if (settings is null || !settings.Enabled)
        {
            return [];
        }

        var problems = ImmutableArray.CreateBuilder<AutoReplyProblem>();

        if (string.IsNullOrWhiteSpace(settings.ToQuestionId))
        {
            problems.Add(new AutoReplyProblem(AutoReplyProblemCode.ToQuestionMissing));
        }
        else
        {
            var question = definition.FindQuestion(settings.ToQuestionId);
            if (question is null)
            {
                problems.Add(new AutoReplyProblem(
                    AutoReplyProblemCode.ToQuestionNotFound, settings.ToQuestionId));
            }
            else if (!IsEmailQuestion(question))
            {
                // **形式の検証が掛かっていない欄は宛先にできない。**
                // 何を書いても通る欄を宛先にすると、送信は必ず失敗してデッドレターが溜まる
                problems.Add(new AutoReplyProblem(
                    AutoReplyProblemCode.ToQuestionNotEmail, settings.ToQuestionId));
            }
        }

        if (IsBlank(settings.Subject))
        {
            problems.Add(new AutoReplyProblem(AutoReplyProblemCode.SubjectMissing));
        }

        if (IsBlank(settings.Body))
        {
            problems.Add(new AutoReplyProblem(AutoReplyProblemCode.BodyMissing));
        }

        var usesEditLink =
            AutoReplyKeywords.Contains(settings, AutoReplyKeywords.EditUrl)
            || AutoReplyKeywords.Contains(settings, AutoReplyKeywords.EditUrlExpiresAt);
        if (usesEditLink)
        {
            // 既知だが現在の設定では使えないキーワードは公開時に止める。
            // 打ち間違いである未知のキーワードは、プレビューで警告しつつ本文へ残す。
            if (!definition.AllowEditingAfterSubmit)
            {
                problems.Add(new AutoReplyProblem(AutoReplyProblemCode.EditLinkKeywordUnavailable));
            }

            // **永久に生きるリンクを作らせない**
            if (settings.EditLinkDays is < 1 or > AutoReplySettings.MaxEditLinkDays)
            {
                problems.Add(new AutoReplyProblem(
                    AutoReplyProblemCode.EditLinkDaysInvalid,
                    settings.EditLinkDays.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        var usesAssetsLink =
            AutoReplyKeywords.Contains(settings, AutoReplyKeywords.AssetsUrl)
            || AutoReplyKeywords.Contains(settings, AutoReplyKeywords.AssetsUrlExpiresAt);
        if (usesAssetsLink
            && (!SurveyAssetReferences.HasTicketedAssets(definition)
                || definition.AssetDelivery?.Expiration is AssetTicketExpiration.CompletedOnly))
        {
            problems.Add(new AutoReplyProblem(AutoReplyProblemCode.AssetsUrlKeywordUnavailable));
        }

        return problems.ToImmutable();
    }

    /// <summary>宛先にできる設問か。</summary>
    /// <remarks>
    /// **1 行の記述式（<see cref="QuestionType.Text"/>）で、メール形式の検証が
    /// 掛かっているものだけ。** 段落（長文）は形式の検証が意味を持たないので外す。
    /// </remarks>
    private static bool IsEmailQuestion(Question question) =>
        question.Type is QuestionType.Text && question.Settings.Format is TextFormat.Email;

    /// <summary>どの言語にも中身が無いか。</summary>
    /// <remarks>
    /// **既定の言語だけを見ない。** 英語だけ書いて日本語を書いていない場合、
    /// 日本語の回答者には空のメールが届く……が、**それは翻訳漏れであって不備ではない**
    /// （<see cref="LocalizedText.Get"/> が既定の言語へ落とす）。
    /// ここで止めたいのは**どの言語にも何も書いていない**場合。
    /// </remarks>
    private static bool IsBlank(LocalizedText? text) =>
        text is null
        || text.Languages.Count == 0
        || text.Languages.All(language => string.IsNullOrWhiteSpace(text.Get(language)));
}
