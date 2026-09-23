using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>DB と外部設定を解決した現在のメール設定を返す。</summary>
public interface IMailSettingsProvider
{
    Task<MailOptions> GetAsync(CancellationToken cancellationToken = default);

    Task<MailOptions> PreviewAsync(
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default);
}

/// <summary>アプリケーション設定のスナップショットをメール設定へ変換する。</summary>
public sealed class MailSettingsProvider(AppSettingsProvider appSettings) : IMailSettingsProvider
{
    public async Task<MailOptions> GetAsync(CancellationToken cancellationToken = default) =>
        FromSnapshot(await appSettings.GetAsync(cancellationToken).ConfigureAwait(false));

    public async Task<MailOptions> PreviewAsync(
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default) =>
        FromSnapshot(await appSettings.PreviewAsync(values, cancellationToken).ConfigureAwait(false));

    private static MailOptions FromSnapshot(AppSettingsSnapshot snapshot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(snapshot.Values!)
            .Build();
        return MailOptions.FromConfiguration(configuration);
    }
}

/// <summary>単体テストなどで固定したメール設定を返す。</summary>
public sealed class FixedMailSettingsProvider(MailOptions options) : IMailSettingsProvider
{
    public Task<MailOptions> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(options);

    public Task<MailOptions> PreviewAsync(
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(options);
}

/// <summary>送信の直前に現在の設定を読み、選択された経路で 1 通送る。</summary>
public sealed class DynamicMailTransport(
    IMailSettingsProvider settings,
    ILoggerFactory loggerFactory) : IMailTransport
{
    public async Task SendAsync(
        OutgoingMail mail,
        CancellationToken cancellationToken = default)
    {
        var options = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!options.IsReady)
        {
            throw new MailDeliveryException(
                "メールの設定が有効になっていない（QUESTIONNAIRE_MAIL_*）",
                isTransient: true);
        }

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
