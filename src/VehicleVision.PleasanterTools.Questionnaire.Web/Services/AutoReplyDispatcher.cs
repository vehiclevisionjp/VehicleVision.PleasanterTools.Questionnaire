using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>受け付けた回答から、自動返信メールを送信待ちへ積む（Issue #189）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **回答の受付を絶対に落とさない。** ここで起きた失敗は、
/// **記録して飲み込む。** メールは「受け付けたことの知らせ」であって、
/// 回答そのものではない。**知らせを積めなかったせいで、
/// 回答者に「送れませんでした」と返してはいけない。**
/// </para>
/// <para>
/// **送るのは新規の回答だけ。** 編集のたびに送ると、
/// 直すたびに同じ知らせが届く（<c>ResponseIntake</c> が判断して呼ぶ）。
/// </para>
/// <para>
/// **識別子は回答トークンから決める。** 同じ回答で二重に積まない
/// （<see cref="IMailOutbox.EnqueueAsync"/> が衝突で <c>false</c> を返す）。
/// </para>
/// </remarks>
public sealed class AutoReplyDispatcher(
    IMailOutbox outbox,
    IMailPayloadProtector protector,
    MailOptions options,
    ILogger<AutoReplyDispatcher> logger,
    PleasanterOptions? pleasanter = null,
    IResponseEditTokenStore? editTokens = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>差し込みの日時を出す時間帯（Issue #209）。</summary>
    /// <remarks>
    /// **運用側の時間帯（<c>QUESTIONNAIRE_PLEASANTER_TIMEZONE</c>）に合わせる。**
    /// 回答者がどこに居るかは分からないので、**Pleasanter に溜まる回答と同じ読み方**に揃える。
    /// 設定が無ければ UTC。
    /// </remarks>
    private TimeZoneInfo DisplayTimeZone
    {
        get
        {
            if (pleasanter?.ApiKeyUserTimeZoneId is not { Length: > 0 } id)
            {
                return TimeZoneInfo.Utc;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception exception)
                when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // **起動は止めない。** 送れないより、UTC で送れる方がまし
                logger.LogWarning("時間帯 {TimeZone} を解決できないので UTC で差し込む", id);
                return TimeZoneInfo.Utc;
            }
        }
    }

    /// <summary>必要なら 1 通積む。**積んだら <c>true</c>。**</summary>
    /// <param name="surveyId">アンケート。**知らせを分けるために持たせる。**</param>
    /// <param name="definition">受け付けた版の定義。</param>
    /// <param name="payload">受け付けた回答。</param>
    /// <param name="language">回答者が使っていた言語。</param>
    /// <param name="cancellationToken">中断。</param>
    /// <param name="publicId">
    /// アンケートの公開 ID（Issue #202）。**再編集リンクの URL に要る。**
    /// </param>
    /// <param name="acceptTo">
    /// 受付の終了日時（Issue #202）。**リンクの期限はこれを超えない。**
    /// </param>
    public async Task<bool> TryEnqueueAsync(
        Guid surveyId,
        SurveyDefinition definition,
        ResponsePayload payload,
        string? language,
        CancellationToken cancellationToken = default,
        string? publicId = null,
        DateTime? acceptTo = null,
        string? assetTicket = null,
        DateTime? assetTicketExpiresAt = null)
    {
        if (definition.AutoReply?.Enabled is not true)
        {
            return false;
        }

        if (!options.IsReady)
        {
            // **設定だけ有効で、送る口が無い。** 積むと送れないまま溜まる
            // （管理画面にも「サーバ側で無効」と出している）
            logger.LogWarning(
                "自動返信が有効だが、メールの送信が設定されていないので積まない（QUESTIONNAIRE_MAIL_*）");
            return false;
        }

        try
        {
            if (AutoReplyComposer.FindRecipient(definition.AutoReply, payload) is null)
            {
                // **宛先の設問に答えていないだけ。** トークンも作らない
                return false;
            }

            var now = _time.GetUtcNow();
            var values = new AutoReplyPlaceholderValues(
                AcceptTo: ToDisplayTime(acceptTo),
                FormUrl: FormUrl(publicId));

            // **再編集リンクの値を作る**（Issue #202 / #319）。
            // ⚠️ **回答本体のトークンは載せない。** 専用のトークンを 1 本発行する
            values = await WithEditLinkAsync(
                    values,
                    definition,
                    payload,
                    publicId,
                    surveyId,
                    acceptTo,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            values = WithAssetTicketLink(
                values, publicId, assetTicket, assetTicketExpiresAt);

            var submittedAt = TimeZoneInfo.ConvertTime(now, DisplayTimeZone);
            var mail = AutoReplyComposer.Compose(
                definition, payload, language, submittedAt, values);
            if (mail is null)
            {
                // **宛先の設問に答えていないだけ。** 異常ではない
                return false;
            }

            // ⚠️ **ここで初めて暗号化する。** 平文のまま DB へ渡る経路を作らない
            return await outbox.EnqueueAsync(
                MailIdOf(payload.Token),
                (int)MailKind.AutoReply,
                surveyId,
                protector.Protect(mail),
                cancellationToken).ConfigureAwait(false);
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // ⚠️ **受付の結果を変えない。** 回答は既に送信待ちへ入っている
            logger.LogError(exception, "自動返信メールを積めなかった。受付の結果は変えない");
            return false;
        }
    }

    private AutoReplyPlaceholderValues WithAssetTicketLink(
        AutoReplyPlaceholderValues values,
        string? publicId,
        string? assetTicket,
        DateTime? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(publicId)
            || string.IsNullOrWhiteSpace(assetTicket)
            || expiresAt is null
            || options.BaseUrl is not { Length: > 0 } baseUrl)
        {
            return values;
        }

        var url = AssetTicket.UrlOf(baseUrl, publicId, assetTicket);
        return values with
        {
            AssetsUrl = url,
            AssetsUrlExpiresAt = ToDisplayTime(expiresAt),
        };
    }

    /// <summary>再編集リンクの差し込み値を作る（Issue #202 / #319）。</summary>
    /// <remarks>
    /// <para>
    /// **付けられないなら、黙って付けずに送る。** リンクが無いだけで、
    /// 「受け付けた」という知らせそのものは届けたい。
    /// </para>
    /// <para>
    /// ⚠️ **期限は受付の終了を超えない。** 受け付けていない期間に開いても直せないので、
    /// **開けるのに直せないリンク**を送らない。
    /// </para>
    /// </remarks>
    private async Task<AutoReplyPlaceholderValues> WithEditLinkAsync(
        AutoReplyPlaceholderValues values,
        SurveyDefinition definition,
        ResponsePayload payload,
        string? publicId,
        Guid surveyId,
        DateTime? acceptTo,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = definition.AutoReply;
        if (settings is null
            || (!AutoReplyKeywords.Contains(settings, AutoReplyKeywords.EditUrl)
                && !AutoReplyKeywords.Contains(settings, AutoReplyKeywords.EditUrlExpiresAt))
            || !definition.AllowEditingAfterSubmit
            || editTokens is null
            || string.IsNullOrWhiteSpace(publicId))
        {
            return values;
        }

        if (options.BaseUrl is not { Length: > 0 } baseUrl)
        {
            // ⚠️ **要求の Host からは作らない**（host header injection。Issue #189）
            logger.LogWarning(
                "再編集リンクを付けられない（{Key}BASEURL が未設定）", MailOptions.Prefix);
            return values;
        }

        var expiresAt = now.UtcDateTime.AddDays(settings.EditLinkDays);

        // **受付の終了を超えない**
        if (acceptTo is { } until && until < expiresAt)
        {
            expiresAt = until;
        }

        if (expiresAt <= now.UtcDateTime)
        {
            // 既に受付が終わっている。**開いても直せないので付けない**
            return values;
        }

        var token = ResponseEditLink.Create();
        await editTokens
            .SaveAsync(
                ResponseEditLink.HashOf(token), payload.Token, surveyId, expiresAt, cancellationToken)
            .ConfigureAwait(false);

        var url = ResponseEditLink.UrlOf(baseUrl, publicId, token);
        return values with
        {
            EditUrl = url,
            EditUrlExpiresAt = ToDisplayTime(expiresAt),
        };
    }

    private string? FormUrl(string? publicId) =>
        options.BaseUrl is { Length: > 0 } baseUrl && !string.IsNullOrWhiteSpace(publicId)
            ? $"{baseUrl.TrimEnd('/')}/f/{Uri.EscapeDataString(publicId)}"
            : null;

    private DateTimeOffset? ToDisplayTime(DateTime? value) =>
        value is null
            ? null
            : TimeZoneInfo.ConvertTime(
                new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)),
                DisplayTimeZone);

    /// <summary>回答トークンから、送信待ちの識別子を決める。</summary>
    /// <remarks>
    /// <para>
    /// **同じ回答なら必ず同じ識別子。** 二重に積まないために要る。
    /// </para>
    /// <para>
    /// ⚠️ **トークンそのものを識別子にしない。** 送信待ちの行は管理画面から
    /// 件数として見えるところにあり、**回答トークンは回答を読み書きできる値**
    /// （<c>_documents/アーキテクチャ方針.md</c> 9 章）。
    /// 一方向に潰してから使う。
    /// </para>
    /// </remarks>
    public static Guid MailIdOf(string responseToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("autoreply:" + responseToken));
        return new Guid(hash.AsSpan(0, 16));
    }
}
