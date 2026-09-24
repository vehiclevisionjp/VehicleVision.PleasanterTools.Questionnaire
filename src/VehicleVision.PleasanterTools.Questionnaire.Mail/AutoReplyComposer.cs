using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Mail;

/// <summary>回答から自動返信メールを組み立てる（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **通信も DB も触らない。** 定義と回答だけから 1 通を作る。
/// </para>
/// <para>
/// **送らない場面を <c>null</c> で返す。** 例外にしない。
/// 「無効」「宛先の設問に答えていない」は**異常ではなく通常の分岐**で、
/// 例外にすると回答の受付の側で握り潰す処理が要る。
/// </para>
/// </remarks>
public static class AutoReplyComposer
{
    /// <summary>1 通を組み立てる。**送らないなら <c>null</c>。**</summary>
    /// <param name="definition">公開済みの定義。</param>
    /// <param name="payload">受け付けた回答。</param>
    /// <param name="language">回答者が使っていた言語。</param>
    /// <param name="submittedAt">
    /// 受け付けた日時（Issue #209）。**表示する時間帯へ直したものを渡すこと。**
    /// 差し込み <c>{{submittedAt}}</c> に使う。**省くと現在時刻**。
    /// </param>
    public static OutgoingMail? Compose(
        SurveyDefinition definition,
        ResponsePayload payload,
        string? language,
        DateTimeOffset? submittedAt = null,
        AutoReplyPlaceholderValues? values = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(payload);

        var settings = definition.AutoReply;
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.ToQuestionId))
        {
            return null;
        }

        var toAddress = FindRecipient(settings, payload);
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            // **宛先の設問に答えていない。** 任意の設問を宛先にできる以上、普通に起きる
            return null;
        }

        // **差し込みは件名にも効かせる。** 「{{title}} へのご回答」と書けること
        var title = definition.Title.Get(language, definition.FallbackLanguage);
        var filledAt = submittedAt ?? DateTimeOffset.UtcNow;
        values = (values ?? new AutoReplyPlaceholderValues()) with
        {
            Answers = DescribeAnswers(definition, payload, language),
        };

        var subject = MailPlaceholders.Fill(
            settings.Subject?.Get(language, definition.FallbackLanguage) ?? string.Empty,
            title,
            filledAt,
            values);
        if (string.IsNullOrWhiteSpace(subject))
        {
            // **公開のときに弾いているはず**（AutoReplyValidator）。
            // ここまで来るのは、公開後に定義を直接書き換えた場合だけ
            return null;
        }

        var body = MailPlaceholders.Fill(
            settings.Body?.Get(language, definition.FallbackLanguage) ?? string.Empty,
            title,
            filledAt,
            values);

        return new OutgoingMail(
            toAddress.Trim(),
            subject,
            body,
            settings.FromName?.Get(language, definition.FallbackLanguage),
            settings.ReplyToAddress?.Trim(),
            settings.BccAddress?.Trim());
    }

    /// <summary>設定された設問から宛先を得る。**送れないなら <c>null</c>。**</summary>
    public static string? FindRecipient(AutoReplySettings settings, ResponsePayload payload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(payload);

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ToQuestionId))
        {
            return null;
        }

        return FindAnswerValue(payload, settings.ToQuestionId) is { } value
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
    }

    /// <summary>回答の写しを作る。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **添付の中身は載せない。** ファイル名だけにする。
    /// 本文に Base64 を載せると、送れない大きさのメールが出来上がる。
    /// </para>
    /// <para>
    /// **定義の順に出す。** 回答の配列の順は、回答者が触った順に依存し得る。
    /// </para>
    /// </remarks>
    private static string DescribeAnswers(
        SurveyDefinition definition,
        ResponsePayload payload,
        string? language)
    {
        var answers = payload.Answers.ToDictionary(
            answer => answer.QuestionId, StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var question in definition.AllQuestions)
        {
            // **説明文や埋め込みは回答ではない。** 写しに出さない
            if (question.IsDisplayOnly)
            {
                continue;
            }

            if (!answers.TryGetValue(question.QuestionId, out var answer))
            {
                continue;
            }

            var text = Describe(answer);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add($"{question.Title.Get(language, definition.FallbackLanguage)}: {text}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>回答 1 件を 1 行の文字列にする。</summary>
    private static string Describe(PayloadAnswer answer)
    {
        var parts = new List<string>();

        if (!answer.Values.IsDefaultOrEmpty)
        {
            parts.AddRange(answer.Values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        // **行のある設問（グリッド）は「行: 値」で並べる**
        if (answer.Rows is not null)
        {
            parts.AddRange(answer.Rows
                .Where(row => !row.Value.IsDefaultOrEmpty)
                .Select(row => $"{row.Key}: {string.Join(", ", row.Value)}"));
        }

        if (!string.IsNullOrWhiteSpace(answer.OtherText))
        {
            parts.Add(answer.OtherText);
        }

        // ⚠️ **名前だけ。中身（Base64）は載せない**
        if (!answer.FileNames.IsDefaultOrEmpty)
        {
            parts.AddRange(answer.FileNames.Where(name => !string.IsNullOrWhiteSpace(name)));
        }

        // **改行を潰す。** 段落の回答がそのまま入ると、写しの行と行の境が分からなくなる
        return string.Join(", ", parts).ReplaceLineEndings(" ");
    }

    private static string? FindAnswerValue(ResponsePayload payload, string questionId) =>
        payload.Answers
            .FirstOrDefault(answer => string.Equals(answer.QuestionId, questionId, StringComparison.Ordinal))
            ?.Values
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
