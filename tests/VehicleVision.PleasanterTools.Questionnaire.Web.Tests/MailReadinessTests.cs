using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class MailReadinessTests
{
    private sealed class ThrowingSettings(Exception exception) : IMailSettingsProvider
    {
        public Task<MailOptions> GetAsync(CancellationToken cancellationToken = default) => throw exception;

        public Task<MailOptions> PreviewAsync(
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken = default) => throw exception;
    }

    [Fact]
    public async Task メール設定を組み立てられないときは送れない状態として返す()
    {
        var settings = new ThrowingSettings(new InvalidOperationException("SMTP_HOST が設定されていない"));

        var ready = await MailReadiness.IsReadyAsync(settings, NullLogger.Instance);

        Assert.False(ready);
    }

    [Fact]
    public async Task 想定外の例外は握りつぶさない()
    {
        var settings = new ThrowingSettings(new TimeoutException());

        await Assert.ThrowsAsync<TimeoutException>(
            () => MailReadiness.IsReadyAsync(settings, NullLogger.Instance));
    }

    [Fact]
    public async Task 送れない設定ならfalseを返す()
    {
        var ready = await MailReadiness.IsReadyAsync(
            new FixedMailSettingsProvider(new MailOptions()), NullLogger.Instance);

        Assert.False(ready);
    }
}
