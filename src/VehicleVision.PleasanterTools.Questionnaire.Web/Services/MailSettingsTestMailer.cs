using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>保存前のメール設定で、管理者本人へ試験メールを直接送る。</summary>
public sealed class MailSettingsTestMailer(
    IMailSettingsProvider settings,
    ILoggerFactory loggerFactory)
{
    public async Task SendAsync(
        IReadOnlyDictionary<string, string?> values,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var testValues = new Dictionary<string, string?>(values, StringComparer.Ordinal)
        {
            [AppSettingsProvider.MailEnabledKey] = "true",
        };
        var options = await settings.PreviewAsync(testValues, cancellationToken)
            .ConfigureAwait(false);
        var mail = new OutgoingMail(
            recipient,
            "アンケートシステム メール設定の試験送信",
            "このメールは、管理画面で入力した保存前のメール設定から送信されました。");

        IMailTransport transport = options.Transport switch
        {
            MailTransportKind.AmazonSes =>
                new SesMailTransport(options, loggerFactory.CreateLogger<SesMailTransport>()),
            MailTransportKind.AzureCommunicationServices =>
                new AcsMailTransport(options, loggerFactory.CreateLogger<AcsMailTransport>()),
            _ => new SmtpMailTransport(options, loggerFactory.CreateLogger<SmtpMailTransport>()),
        };

        try
        {
            await transport.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }
}
