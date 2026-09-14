using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

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
    ILogger<AutoReplyDispatcher> logger)
{
    /// <summary>必要なら 1 通積む。**積んだら <c>true</c>。**</summary>
    /// <param name="surveyId">アンケート。**知らせを分けるために持たせる。**</param>
    /// <param name="definition">受け付けた版の定義。</param>
    /// <param name="payload">受け付けた回答。</param>
    /// <param name="language">回答者が使っていた言語。</param>
    /// <param name="cancellationToken">中断。</param>
    public async Task<bool> TryEnqueueAsync(
        Guid surveyId,
        SurveyDefinition definition,
        ResponsePayload payload,
        string? language,
        CancellationToken cancellationToken = default)
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
            var mail = AutoReplyComposer.Compose(definition, payload, language);
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
