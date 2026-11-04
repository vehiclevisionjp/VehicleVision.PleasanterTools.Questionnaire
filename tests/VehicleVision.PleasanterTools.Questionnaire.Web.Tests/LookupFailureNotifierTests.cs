using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>ルックアップの参照元の不備を管理者へ知らせる（Issue #833・仕様書 4.3 節）。偽の通知先で確かめる。</summary>
public sealed class LookupFailureNotifierTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public FakeAdminNotificationStore Store { get; } = new();

        public FakeTimeProvider Time { get; } = new(Origin);

        public ListLogger<LookupFailureNotifier> Logger { get; } = new();

        public LookupFailureNotifier Notifier { get; }

        public Fixture(LookupPublicOptions? options = null)
        {
            Notifier = new LookupFailureNotifier(
                Store, options ?? new LookupPublicOptions(), Time, Logger);
        }
    }

    [Theory]
    [InlineData(LookupSearchFailure.CredentialMissing, AdminNotificationKind.LookupCredentialUnavailable)]
    [InlineData(LookupSearchFailure.CredentialDisabled, AdminNotificationKind.LookupCredentialUnavailable)]
    [InlineData(LookupSearchFailure.CredentialUndecryptable, AdminNotificationKind.LookupCredentialUnavailable)]
    [InlineData(LookupSearchFailure.Unauthorized, AdminNotificationKind.LookupSourceAccessDenied)]
    [InlineData(LookupSearchFailure.Forbidden, AdminNotificationKind.LookupSourceAccessDenied)]
    [InlineData(LookupSearchFailure.NotFound, AdminNotificationKind.LookupSourceAccessDenied)]
    [InlineData(LookupSearchFailure.ClientError, AdminNotificationKind.LookupSourceRejected)]
    [InlineData(LookupSearchFailure.Redirected, AdminNotificationKind.LookupSourceRejected)]
    [InlineData(LookupSearchFailure.InvalidDefinition, AdminNotificationKind.LookupSourceRejected)]
    [InlineData(LookupSearchFailure.InsecureTransport, AdminNotificationKind.LookupSourceRejected)]
    [InlineData(LookupSearchFailure.InvalidResponse, AdminNotificationKind.LookupSourceBadResponse)]
    [InlineData(LookupSearchFailure.ResponseTooLarge, AdminNotificationKind.LookupSourceBadResponse)]
    [InlineData(LookupSearchFailure.Inconsistent, AdminNotificationKind.LookupSourceBadResponse)]
    [InlineData(LookupSearchFailure.None, AdminNotificationKind.LookupSourceBadResponse)]
    public async Task 失敗の分類を知らせの種類へ写し_アンケートへ紐づけて立てる(
        LookupSearchFailure failure, AdminNotificationKind expected)
    {
        var f = new Fixture();
        var surveyId = Guid.NewGuid();

        await f.Notifier.NotifyAsync(surveyId, "l1", failure, "trace-1");

        var raised = Assert.Single(f.Store.Raised);
        Assert.Equal((int)expected, raised.Kind);
        Assert.Equal(surveyId, raised.SurveyId);
    }

    [Fact]
    public async Task 同じアンケート_検索_種類の連続は窓の間は一度だけ立て_窓が明けたら再び立てる()
    {
        var f = new Fixture();
        var surveyId = Guid.NewGuid();

        for (var i = 0; i < 50; i++)
        {
            await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Unauthorized, "t" + i);
            // 同じ種類へ写る別の分類も同じ窓
            await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Forbidden, "t" + i);
        }

        Assert.Single(f.Store.Raised);

        f.Time.Advance(TimeSpan.FromMinutes(9));
        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Unauthorized, "t");
        Assert.Single(f.Store.Raised);

        f.Time.Advance(TimeSpan.FromMinutes(1));
        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Unauthorized, "t");
        Assert.Equal(2, f.Store.Raised.Count);
    }

    [Fact]
    public async Task 検索IDと相関IDを通知の行へ添えて立てる_それ以外は渡さない()
    {
        // Issue #836。識別子だけを渡す。キー値・取得値・資格情報を渡す経路は型に無い
        var f = new Fixture();
        var surveyId = Guid.NewGuid();

        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Unauthorized, "trace-1");

        var detail = Assert.Single(f.Store.Details);
        Assert.Equal(new AdminNotificationDetail("l1", "trace-1"), detail);
        Assert.Equal(
            ["LookupId", "CorrelationId"],
            typeof(AdminNotificationDetail)
                .GetProperties()
                .Where(property => property.Name != "EqualityContract")
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task アンケート_検索_種類が違えば別々に立てる()
    {
        var f = new Fixture();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await f.Notifier.NotifyAsync(a, "l1", LookupSearchFailure.Unauthorized, "t");
        await f.Notifier.NotifyAsync(b, "l1", LookupSearchFailure.Unauthorized, "t");
        await f.Notifier.NotifyAsync(a, "l2", LookupSearchFailure.Unauthorized, "t");
        await f.Notifier.NotifyAsync(a, "l1", LookupSearchFailure.CredentialDisabled, "t");

        Assert.Equal(4, f.Store.Raised.Count);
    }

    [Fact]
    public async Task 窓の長さは設定に従う()
    {
        var f = new Fixture(new LookupPublicOptions { AdminNotificationWindow = TimeSpan.FromSeconds(30) });
        var surveyId = Guid.NewGuid();

        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.NotFound, "t");
        f.Time.Advance(TimeSpan.FromSeconds(29));
        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.NotFound, "t");
        Assert.Single(f.Store.Raised);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.NotFound, "t");
        Assert.Equal(2, f.Store.Raised.Count);
    }

    [Fact]
    public async Task 知らせを書けなくても例外を投げず_窓を閉じて次の失敗でやり直す_ログに中身を出さない()
    {
        var f = new Fixture();
        var surveyId = Guid.NewGuid();
        f.Store.Throws = true;

        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Forbidden, "trace-x");

        Assert.Empty(f.Store.Raised);
        Assert.Contains(f.Logger.Messages, message => message.Contains("知らせられなかった", StringComparison.Ordinal));
        Assert.Contains(f.Logger.Messages, message => message.Contains("trace-x", StringComparison.Ordinal));

        f.Store.Throws = false;
        await f.Notifier.NotifyAsync(surveyId, "l1", LookupSearchFailure.Forbidden, "trace-y");
        Assert.Single(f.Store.Raised);
    }

    /// <summary>書き込みで必ず例外を投げる通知先。例外の本文に秘密に見立てた値を入れられる。</summary>
    private sealed class ThrowingStore(Exception exception) : IAdminNotificationStore
    {
        public Task RaiseAsync(
            int kind, Guid surveyId, DateTime occurredAt, AdminNotificationDetail detail,
            CancellationToken cancellationToken = default) =>
            throw exception;

        public Task RaiseAsync(
            int kind, Guid surveyId, DateTime occurredAt, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<IReadOnlyList<AdminNotificationView>> ListAsync(
            AdminNotificationQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdminNotificationView>>([]);

        public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> MarkAllReadAsync(DateTime readAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> DeleteOlderThanAsync(DateTime threshold, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task 通知先の例外の中身はログへ出さず_型名だけを残す()
    {
        const string secret = "SECRET-KEY-VALUE-5c1e";
        var store = new ThrowingStore(new InvalidOperationException(secret));
        var logger = new ListLogger<LookupFailureNotifier>();
        var notifier = new LookupFailureNotifier(store, new LookupPublicOptions(), new FakeTimeProvider(Origin), logger);

        await notifier.NotifyAsync(Guid.NewGuid(), "l1", LookupSearchFailure.Unauthorized, "trace-z");

        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task 通知先が応答しなくても知らせを待ち続けず_例外を投げない()
    {
        var store = new ThrowingStore(new OperationCanceledException());
        var logger = new ListLogger<LookupFailureNotifier>();
        var notifier = new LookupFailureNotifier(store, new LookupPublicOptions(), new FakeTimeProvider(Origin), logger);

        await notifier.NotifyAsync(Guid.NewGuid(), "l1", LookupSearchFailure.Unauthorized, "t");

        Assert.Contains(logger.Messages, message => message.Contains("知らせられなかった", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ログには検索ID_分類_相関IDだけを出す()
    {
        var f = new Fixture();

        await f.Notifier.NotifyAsync(Guid.NewGuid(), "lookup-77", LookupSearchFailure.Inconsistent, "trace-77");

        var line = Assert.Single(f.Logger.Messages);
        Assert.Contains("lookup-77", line, StringComparison.Ordinal);
        Assert.Contains("trace-77", line, StringComparison.Ordinal);
        Assert.Contains(nameof(LookupSearchFailure.Inconsistent), line, StringComparison.Ordinal);
    }
}
