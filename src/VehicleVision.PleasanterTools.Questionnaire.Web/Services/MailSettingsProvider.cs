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
/// <remarks>
/// **メール本文の起点 URL にはサブパスを足して渡す**（Issue #465）。
/// 招待・自動返信・再編集リンク・配布資産のリンクは、すべてこの起点から作られる。
/// 起点に既にサブパスまで書かれていれば足さない（<see cref="PathBaseOptions.ComposePublicUrl"/>）。
/// </remarks>
public sealed class MailSettingsProvider(
    AppSettingsProvider appSettings,
    PathBaseOptions? pathBase = null) : IMailSettingsProvider
{
    public async Task<MailOptions> GetAsync(CancellationToken cancellationToken = default) =>
        FromSnapshot(await appSettings.GetAsync(cancellationToken).ConfigureAwait(false));

    public async Task<MailOptions> PreviewAsync(
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default) =>
        FromSnapshot(await appSettings.PreviewAsync(values, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// スナップショットをメール設定へ直す。**空の値は積まない**（Issue #397）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **スナップショットは「未設定」を空文字で持つ**（定義の既定値が <c>string.Empty</c>）。
    /// そのまま積むと、**以前は <c>null</c> だった任意の項目が空文字になる。**
    /// <c>ReplyToAddress</c> が空文字になった結果、
    /// <c>MailMessageFactory.ValidateHeaders</c> の <c>is not null</c> を通過し、
    /// **自動返信が 1 通も送れなくなった**（再試行もされずデッドレターへ回る）。
    /// **同じ形の項目はほかにもある**（<c>FROM_NAME</c> / <c>BASEURL</c> /
    /// <c>SMTP_USER</c> / <c>SMTP_PASSWORD</c>）ので、1 つずつ直さずここでまとめて落とす。
    /// </remarks>
    private MailOptions FromSnapshot(AppSettingsSnapshot snapshot)
    {
        var values = snapshot.Values
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = MailOptions.FromConfiguration(configuration);
        return pathBase is { IsConfigured: true }
            ? options with { BaseUrl = pathBase.ComposePublicUrl(options.BaseUrl) }
            : options;
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
