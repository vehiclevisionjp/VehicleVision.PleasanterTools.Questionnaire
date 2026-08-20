using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>溜まりすぎたら受付を断ること（Issue #72）。</summary>
/// <remarks>
/// **DB へは繋がない。** 確かめたいのは止める・戻すの判断と数え方であって、
/// SQL の振る舞いではない（そちらは結合試験）。
/// </remarks>
public class ResponseBacklogGuardTests
{
    private static readonly Guid Watched = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>数える口だけを持つ偽物。**何回数えたかを覚える。**</summary>
    private sealed class CountingOutbox : IResponseOutbox
    {
        private PendingBacklog _backlog = PendingBacklog.Empty;

        public int Calls { get; private set; }

        public int? LastAtLeast { get; private set; }

        public Exception? Throws { get; set; }

        public void Set(int total, params (Guid SurveyId, int Count)[] bySurvey) =>
            _backlog = new PendingBacklog(
                total,
                bySurvey.ToImmutableDictionary(row => row.SurveyId, row => row.Count));

        public Task<PendingBacklog> CountBacklogAsync(
            int perSurveyAtLeast,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAtLeast = perSurveyAtLeast;

            return Throws is null
                ? Task.FromResult(_backlog)
                : Task.FromException<PendingBacklog>(Throws);
        }

        // ---- ここから下は使わない ------------------------------------------
        public Task SaveAsync(
            string responseToken,
            Guid surveyId,
            int surveyVersion,
            string payloadJson,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PendingResponse?> ClaimAsync(
            string lockedBy,
            TimeSpan lockDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RescheduleAsync(
            string responseToken,
            DateTime nextAttemptAtUtc,
            string? error,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeadLetterAsync(
            string responseToken, string error, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> FindPayloadAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountPendingAsync(
            Guid? surveyId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
            DeadLetterQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> RequeueDeadLetterAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static (ResponseBacklogGuard Guard, CountingOutbox Outbox, FakeTimeProvider Time)
        Build(BacklogGuardOptions? options = null)
    {
        var outbox = new CountingOutbox();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));

        var guard = new ResponseBacklogGuard(
            outbox,
            options ?? new BacklogGuardOptions { PerSurveyLimit = 100, TotalLimit = 500 },
            NullLogger<ResponseBacklogGuard>.Instance,
            time);

        return (guard, outbox, time);
    }

    // ---- 止める・止めない ---------------------------------------------------

    [Fact]
    public async Task 閾値に届いていなければ止めない()
    {
        var (guard, outbox, _) = Build();
        outbox.Set(total: 10, (Watched, 5));

        Assert.False(await guard.IsBlockedAsync(Watched));
    }

    [Fact]
    public async Task アンケート単位の上限に達したらそのアンケートを止める()
    {
        var (guard, outbox, _) = Build();
        outbox.Set(total: 120, (Watched, 100));

        Assert.True(await guard.IsBlockedAsync(Watched));
    }

    [Fact]
    public async Task 止めるのは超えたアンケートだけで他は生かす()
    {
        // **狙われている 1 本を切り離し、他は受け付け続ける**のが 2 段構えの狙い
        var (guard, outbox, _) = Build();
        outbox.Set(total: 120, (Watched, 100));

        Assert.True(await guard.IsBlockedAsync(Watched));
        Assert.False(await guard.IsBlockedAsync(Other));
    }

    [Fact]
    public async Task 全体の上限に達したら全アンケートを止める()
    {
        // **Pleasanter が止まると全アンケートが等しく溜まる。**
        // 1 本ずつの上限では、どれも超えないまま DB が溢れ得る
        var (guard, outbox, _) = Build();
        outbox.Set(total: 500);

        Assert.True(await guard.IsBlockedAsync(Watched));
        Assert.True(await guard.IsBlockedAsync(Other));
    }

    // ---- ヒステリシス -------------------------------------------------------

    [Fact]
    public async Task 上限を下回っただけでは再開しない()
    {
        // **上限ちょうどで戻すと、1 件捌けては 1 件受け付けるのを境界で繰り返す**
        var (guard, outbox, time) = Build();
        outbox.Set(total: 120, (Watched, 100));
        Assert.True(await guard.IsBlockedAsync(Watched));

        outbox.Set(total: 120, (Watched, 99));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(await guard.IsBlockedAsync(Watched));
    }

    [Fact]
    public async Task 再開の水準まで減れば受け付け直す()
    {
        // **止めっぱなしにしない**（Issue #72）。
        // 半日の障害で人気のアンケートが永久に閉じるのを避ける
        var (guard, outbox, time) = Build();
        outbox.Set(total: 120, (Watched, 100));
        Assert.True(await guard.IsBlockedAsync(Watched));

        // 上限 100・割合 0.8 なので 80 件まで捌ければ戻る
        outbox.Set(total: 120, (Watched, 80));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.False(await guard.IsBlockedAsync(Watched));
    }

    // ---- 数え方 -------------------------------------------------------------

    [Fact]
    public async Task 間隔の内は数え直さない()
    {
        // **受け付けのたびに数えない**（_documents/非機能設計.md 2 章）
        var (guard, outbox, _) = Build();
        outbox.Set(total: 10, (Watched, 5));

        for (var i = 0; i < 20; i++)
        {
            await guard.IsBlockedAsync(Watched);
        }

        Assert.Equal(1, outbox.Calls);
    }

    [Fact]
    public async Task 間隔が過ぎたら数え直す()
    {
        var (guard, outbox, time) = Build();
        outbox.Set(total: 10, (Watched, 5));

        await guard.IsBlockedAsync(Watched);
        time.Advance(TimeSpan.FromSeconds(10));
        await guard.IsBlockedAsync(Watched);

        Assert.Equal(2, outbox.Calls);
    }

    [Fact]
    public async Task 数え直しの間に受け付けた分を足して判断する()
    {
        // **足さないと、間隔の内に押し込まれた分だけ上限を素通りする**
        var (guard, outbox, _) = Build();
        outbox.Set(total: 10, (Watched, 90));

        Assert.False(await guard.IsBlockedAsync(Watched));

        for (var i = 0; i < 10; i++)
        {
            guard.OnAccepted(Watched);
        }

        Assert.True(await guard.IsBlockedAsync(Watched));
        Assert.Equal(1, outbox.Calls);
    }

    [Fact]
    public async Task 数え直したら覚えていた増分を落とす()
    {
        // **二重に数え続けない。** 落とさないと、受け付けるたびに増える一方になる
        var (guard, outbox, time) = Build();
        outbox.Set(total: 10, (Watched, 10));
        await guard.IsBlockedAsync(Watched);

        for (var i = 0; i < 50; i++)
        {
            guard.OnAccepted(Watched);
        }

        // 数え直した結果は 20 件。**覚えていた 50 件を足したままにしない**
        outbox.Set(total: 20, (Watched, 20));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.False(await guard.IsBlockedAsync(Watched));
        Assert.Equal(20, guard.GetStatus().Total);
    }

    [Fact]
    public async Task 数え直しの下限は再開の水準に合わせる()
    {
        // **止まっているアンケートが数え直しで返らないと、永久に戻らない。**
        // 上限で絞ると、上限未満まで減った瞬間に行が消えて 0 件に見える……のは良いが、
        // 再開の水準を跨ぐ間の件数が読めなくなる
        var (guard, outbox, _) = Build();
        outbox.Set(total: 0);

        await guard.IsBlockedAsync(Watched);

        Assert.Equal(80, outbox.LastAtLeast);
    }

    // ---- 壊れたときの倒れ方 -------------------------------------------------

    [Fact]
    public async Task 数えられなくても受付は止めない()
    {
        // **DB が応えないときに受付だけ閉じても、回答を捨てるだけで滞留は減らない**
        var (guard, outbox, _) = Build();
        outbox.Throws = new InvalidOperationException("DB が応えない");

        Assert.False(await guard.IsBlockedAsync(Watched));
    }

    [Fact]
    public async Task 数えられなくなっても直前の判断は残る()
    {
        // **一度止めたものが、数えられなくなった拍子に開かない**
        var (guard, outbox, time) = Build();
        outbox.Set(total: 120, (Watched, 100));
        Assert.True(await guard.IsBlockedAsync(Watched));

        outbox.Throws = new InvalidOperationException("DB が応えない");
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(await guard.IsBlockedAsync(Watched));
    }

    // ---- 無効にできること ---------------------------------------------------

    [Fact]
    public async Task 両方の閾値が0なら数えもしない()
    {
        var (guard, outbox, _) = Build(
            new BacklogGuardOptions { PerSurveyLimit = 0, TotalLimit = 0 });

        Assert.False(await guard.IsBlockedAsync(Watched));
        guard.OnAccepted(Watched);

        Assert.Equal(0, outbox.Calls);
    }

    [Fact]
    public async Task アンケート単位だけ無効にできる()
    {
        var (guard, outbox, _) = Build(
            new BacklogGuardOptions { PerSurveyLimit = 0, TotalLimit = 500 });

        outbox.Set(total: 400, (Watched, 10_000));

        Assert.False(await guard.IsBlockedAsync(Watched));
    }

    [Fact]
    public async Task 全体だけ無効にできる()
    {
        var (guard, outbox, _) = Build(
            new BacklogGuardOptions { PerSurveyLimit = 100, TotalLimit = 0 });

        outbox.Set(total: 1_000_000, (Watched, 10));

        Assert.False(await guard.IsBlockedAsync(Watched));
    }

    // ---- 管理画面へ出す状態 -------------------------------------------------

    [Fact]
    public async Task 止めていることを状態として出す()
    {
        // **件数だけでは「止めている」ことが読み取れない**（Issue #72）
        var (guard, outbox, _) = Build();
        outbox.Set(total: 120, (Watched, 100));

        await guard.IsBlockedAsync(Watched);
        var status = guard.GetStatus();

        Assert.True(status.Enabled);
        Assert.Equal(120, status.Total);
        Assert.False(status.TotalBlocked);
        Assert.Equal(1, status.BlockedSurveyCount);
        Assert.NotNull(status.SampledAt);
    }

    [Fact]
    public void 一度も数えていなければ計測時刻を出さない()
    {
        var (guard, _, _) = Build();

        Assert.Null(guard.GetStatus().SampledAt);
    }

    // ---- 設定の読み方 -------------------------------------------------------

    [Fact]
    public void 設定が無ければ既定値を使う()
    {
        var options = BacklogGuardOptions.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.Equal(10_000, options.PerSurveyLimit);
        Assert.Equal(50_000, options.TotalLimit);
    }

    [Fact]
    public void 読めない値は黙って既定へ落とさない()
    {
        // **設定したつもりが効いていない状態を作らない**
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BacklogGuardOptions.PerSurveyLimitKey] = "たくさん",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => BacklogGuardOptions.FromConfiguration(configuration));

        Assert.Contains(BacklogGuardOptions.PerSurveyLimitKey, exception.Message, StringComparison.Ordinal);
    }
}
