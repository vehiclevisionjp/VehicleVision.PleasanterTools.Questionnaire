using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class AssetTicketTests
{
    [Fact]
    public void URLは断片で引換券を渡す()
    {
        var url = AssetTicket.UrlOf("https://survey.example.jp/", "pub 1", "tok+1/2");

        Assert.Equal(
            "https://survey.example.jp/f/pub%201#d=tok%2B1%2F2",
            url);
        Assert.DoesNotContain("?d=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 作るたびに違いURLで壊れない()
    {
        var first = AssetTicket.Create();
        var second = AssetTicket.Create();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void 保存用ハッシュへ平文を残さない()
    {
        Assert.DoesNotContain(
            "ticket-1",
            AssetTicket.HashOf("ticket-1"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 完了時限定の署名付きCookieで配布資産を取得できる()
    {
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var guard = new SubmissionGuard(
            SecretProtector.GenerateKey(),
            new SubmissionGuardOptions(),
            time);
        var store = new RejectingAssetTicketStore();
        var cookie = guard.IssueAssetAccess("pub-1", "response-token");

        var allowed = await AssetTicket.CanAccessAsync(
            cookie,
            "pub-1",
            Guid.NewGuid(),
            guard,
            store,
            time.GetUtcNow().UtcDateTime);

        Assert.True(allowed);
        Assert.Equal(0, store.RedeemCount);
    }

    [Fact]
    public async Task 完了時限定の署名付きCookieは期限切れ後に取得できない()
    {
        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var guard = new SubmissionGuard(
            SecretProtector.GenerateKey(),
            new SubmissionGuardOptions(),
            time);
        var store = new RejectingAssetTicketStore();
        var cookie = guard.IssueAssetAccess("pub-1", "response-token");
        time.Advance(SubmissionGuard.AssetAccessLifetime + TimeSpan.FromSeconds(1));

        var allowed = await AssetTicket.CanAccessAsync(
            cookie,
            "pub-1",
            Guid.NewGuid(),
            guard,
            store,
            time.GetUtcNow().UtcDateTime);

        Assert.False(allowed);
        Assert.Equal(0, store.RedeemCount);
    }

    private sealed class RejectingAssetTicketStore : IAssetTicketStore
    {
        public int RedeemCount { get; private set; }

        public Task SaveAsync(
            string ticketHash,
            string responseToken,
            Guid surveyId,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssetTicketGrant?> RedeemAsync(
            string ticketHash,
            Guid surveyId,
            DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            RedeemCount++;
            return Task.FromResult<AssetTicketGrant?>(null);
        }

        public Task<int> RevokeBySurveyAsync(
            Guid surveyId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredAsync(
            DateTime threshold,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
