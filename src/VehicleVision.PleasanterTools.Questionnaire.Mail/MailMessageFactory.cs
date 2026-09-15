using System.Net.Mail;
using MimeKit;
using MimeKit.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Mail;

/// <summary>送る 1 通を、実際のメールの形へ組み立てる。</summary>
/// <remarks>
/// **通信を含まない。** ここだけなら網無しで試験できる。
/// </remarks>
public static class MailMessageFactory
{
    /// <summary>
    /// 自動で作ったメールであることを示すヘッダ（RFC 3834）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **自動返信に自動返信が返ると無限に往復する。**
    /// まともな不在通知はこのヘッダを見て返信を止める。**全通に必ず付ける。**
    /// </remarks>
    public const string AutoSubmittedHeader = "Auto-Submitted";

    /// <summary>組み立てる。</summary>
    /// <exception cref="MailDeliveryException">
    /// アドレスの形が壊れている。**再送しても直らないので恒久の失敗として投げる。**
    /// </exception>
    public static MimeMessage Build(MailOptions options, OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mail);
        mail.Validate();

        var message = new MimeMessage();
        message.From.Add(Parse(options.FromAddress, options.FromName, "差出人"));
        message.To.Add(Parse(mail.ToAddress, name: null, "宛先"));

        if (!string.IsNullOrWhiteSpace(options.ReplyToAddress))
        {
            message.ReplyTo.Add(Parse(options.ReplyToAddress, name: null, "返信先"));
        }

        message.Subject = mail.Subject;

        // **自動生成であることを明示する**（RFC 3834）。不在通知との往復を止める
        message.Headers.Add(AutoSubmittedHeader, "auto-generated");

        message.Body = new TextPart(TextFormat.Plain) { Text = mail.Body };

        return message;
    }

    private static MailboxAddress Parse(string address, string? name, string role)
    {
        // ⚠️ **MimeKit の解析は緩く、`@` が無い文字列も通す。**
        // 回答の検証と同じ `MailAddress.TryCreate` で先に落とす
        // （`Core/Validation/AnswerValidator` の `TextFormat.Email`）。
        // **画面が受け取った値と、送れる値の判定を割らない。**
        if (!MailAddress.TryCreate(address, out _))
        {
            throw new MailDeliveryException(
                $"{role}のアドレスの形が正しくない", isTransient: false);
        }

        try
        {
            return string.IsNullOrWhiteSpace(name)
                ? MailboxAddress.Parse(address)
                : new MailboxAddress(name, address);
        }
        catch (ParseException exception)
        {
            // ⚠️ **アドレスそのものを文言へ入れない。** この文言はデッドレターに残る
            throw new MailDeliveryException(
                $"{role}のアドレスの形が正しくない", isTransient: false, exception);
        }
    }
}
