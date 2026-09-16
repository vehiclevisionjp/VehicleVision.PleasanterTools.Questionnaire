using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>回答画面と管理画面で Altcha の有効設定を分けられることを確かめる。</summary>
public class AltchaGuardTests
{
    private sealed class MemoryStore : IAltchaChallengeStore
    {
        public Task StoreAsync(
            string challenge,
            DateTime expiresAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(
            string challenge,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static AltchaGuard Guard(bool responseAltchaEnabled) =>
        new(
            Convert.ToBase64String(new byte[32]),
            new AltchaOptions { Enabled = responseAltchaEnabled },
            new MemoryStore());

    [Fact]
    public async Task 回答用が無効なら通常の検証は省く()
    {
        Assert.Null(await Guard(responseAltchaEnabled: false).CheckAsync(solution: null));
    }

    [Fact]
    public async Task 管理用の必須検証は回答用が無効でも省かない()
    {
        Assert.Equal(
            AltchaRejection.Missing,
            await Guard(responseAltchaEnabled: false).CheckRequiredAsync(solution: null));
    }
}
