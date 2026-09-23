using System.Collections.Frozen;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理画面の設定を bot 対策の実行時設定へ変換することを確かめる。</summary>
public sealed class BotMitigationOptionsProviderTests
{
    [Fact]
    public async Task 保存済みの設定を各対策へ反映する()
    {
        var provider = new BotMitigationOptionsProvider(new SnapshotProvider(new Dictionary<string, string>
        {
            [BotMitigationOptionsProvider.MitigationEnabledKey] = "false",
            [BotMitigationOptionsProvider.SubmitMinimumSecondsKey] = "12",
            [BotMitigationOptionsProvider.SubmitTicketHoursKey] = "48",
            [BotMitigationOptionsProvider.AltchaEnabledKey] = "true",
            [BotMitigationOptionsProvider.AltchaMinimumNumberKey] = "75000",
            [BotMitigationOptionsProvider.AltchaMaximumNumberKey] = "175000",
            [BotMitigationOptionsProvider.LoginProofOfWorkKey] = "true",
        }));

        var options = await provider.GetAsync();

        Assert.False(options.SubmissionGuard.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(12), options.SubmissionGuard.MinimumElapsed);
        Assert.Equal(TimeSpan.FromHours(48), options.SubmissionGuard.Lifetime);
        Assert.True(options.Altcha.Enabled);
        Assert.Equal(75_000, options.Altcha.MinimumNumber);
        Assert.Equal(175_000, options.Altcha.MaximumNumber);
        Assert.True(options.AdminCaptcha.Enabled);
    }

    private sealed class SnapshotProvider(IReadOnlyDictionary<string, string> values) : IAppSettingsProvider
    {
        private readonly AppSettingsSnapshot snapshot = new(
            [],
            values.ToFrozenDictionary(StringComparer.Ordinal),
            FrozenSet<string>.Empty);

        public Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<AppSettingsSnapshot> SaveAsync(
            IReadOnlyDictionary<string, string?> values,
            Guid updatedByAdminUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
