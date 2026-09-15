using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理操作の記録が、期限を過ぎたら消えること。</summary>
public class AuditLogRetentionTests
{
    private sealed class FakeStore : IAuditLogStore
    {
        public List<DateTime> Thresholds { get; } = [];

        public int DeleteResult { get; set; }

        public Exception? ThrowOnDelete { get; set; }

        /// <summary>呼ばれた回数。**投げたときも数える。** 失敗を待つために要る。</summary>
        public int Attempts;

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AuditLogView>> ListAsync(
            AuditLogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogView>>([]);

        public Task<int> DeleteOlderThanAsync(
            DateTime threshold,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);

            if (ThrowOnDelete is { } exception)
            {
                throw exception;
            }

            Thresholds.Add(threshold);
            return Task.FromResult(DeleteResult);
        }
    }

    private static IConfiguration Configuration(string? retentionDays) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(retentionDays is null
                ? []
                : new Dictionary<string, string?>
                {
                    [AuditLogRetentionOptions.RetentionDaysKey] = retentionDays,
                })
            .Build();

    // ---- 設定 ---------------------------------------------------------------

    [Fact]
    public void 既定は一年残す()
    {
        // **既定を「消さない」にしない。** 決めないまま運用が始まると増え続ける
        var options = AuditLogRetentionOptions.FromConfiguration(Configuration(null));

        Assert.Equal(365, options.RetentionDays);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void 知らせは既定で九十日残す()
    {
        // **監査ログより短い**（Issue #80）。知らせは運用のためのもので、
        // **未読は日数に関わらず残る**ので、短くしても気付けなくならない
        var options = AuditLogRetentionOptions.FromConfiguration(Configuration(null));

        Assert.Equal(90, options.NotificationRetentionDays);
        Assert.True(options.NotificationEnabled);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("30", true)]
    public void 知らせの保持日数を設定から読む(string value, bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuditLogRetentionOptions.NotificationRetentionDaysKey] = value,
            })
            .Build();

        var options = AuditLogRetentionOptions.FromConfiguration(configuration);

        Assert.Equal(enabled, options.NotificationEnabled);
    }

    [Fact]
    public void デッドレターは既定で消さない()
    {
        // ⚠️ **中身は回答そのもの**（Issue #85）。回答者には受付完了と伝えているので、
        // **日数を決めた導入先だけが消す**
        var options = AuditLogRetentionOptions.FromConfiguration(Configuration(null));

        Assert.Equal(0, options.DeadLetterRetentionDays);
        Assert.False(options.DeadLetterEnabled);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("180", true)]
    public void デッドレターの保持日数を設定から読む(string value, bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuditLogRetentionOptions.DeadLetterRetentionDaysKey] = value,
            })
            .Build();

        var options = AuditLogRetentionOptions.FromConfiguration(configuration);

        Assert.Equal(enabled, options.DeadLetterEnabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void 零以下にすると消さない(string value)
    {
        var options = AuditLogRetentionOptions.FromConfiguration(Configuration(value));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 読めない値は黙って既定へ落とさない()
    {
        // **設定したつもりが効いていない状態を作らない**
        var exception = Assert.Throws<InvalidOperationException>(
            () => AuditLogRetentionOptions.FromConfiguration(Configuration("いつまでも")));

        Assert.Contains(
            AuditLogRetentionOptions.RetentionDaysKey, exception.Message, StringComparison.Ordinal);
    }

    // ---- 掃除 ---------------------------------------------------------------

    private static (FakeStore Store, AuditLogRetentionService Service, FakeTimeProvider Time)
        Build(AuditLogRetentionOptions options)
    {
        var store = new FakeStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse(
            "2026-08-19T09:00:00+09:00", System.Globalization.CultureInfo.InvariantCulture));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        return (
            store,
            new AuditLogRetentionService(
                store, options, NullLogger<AuditLogRetentionService>.Instance, time),
            time);
    }

    [Fact]
    public async Task 期限より前を消す()
    {
        var options = new AuditLogRetentionOptions
        {
            RetentionDays = 30,
            SweepInterval = TimeSpan.FromHours(6),
        };

        var (store, service, time) = Build(options);
        var start = time.GetLocalNow().DateTime;

        await service.StartAsync(CancellationToken.None);

        // **起動直後には走らせない。** 全インスタンスが一斉に大きな DELETE を投げないように
        Assert.Empty(store.Thresholds);

        await AdvanceUntilAsync(time, options.SweepInterval, () => store.Thresholds.Count > 0);
        var end = time.GetLocalNow().DateTime;

        await service.StopAsync(CancellationToken.None);

        Assert.NotEmpty(store.Thresholds);

        // **時計を進めた回数は数えない**（常駐が待ち始める時機は測れない）。
        // 消しに行った時点がどこであれ、30 日前を指していればよい
        Assert.InRange(store.Thresholds[0], start.AddDays(-30), end.AddDays(-30));
    }

    [Fact]
    public async Task 消さない設定なら触らない()
    {
        var (store, service, time) = Build(new AuditLogRetentionOptions { RetentionDays = 0 });

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromDays(1));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(store.Thresholds);
    }

    [Fact]
    public async Task 消せなくても止まらない()
    {
        // **掃除の失敗でアプリを止めない**
        var options = new AuditLogRetentionOptions
        {
            RetentionDays = 30,
            SweepInterval = TimeSpan.FromHours(1),
        };

        var (store, service, time) = Build(options);
        store.ThrowOnDelete = new InvalidOperationException("消せない");

        await service.StartAsync(CancellationToken.None);

        // **失敗を見届けてから次へ進める**
        await AdvanceUntilAsync(
            time, options.SweepInterval, () => Volatile.Read(ref store.Attempts) >= 1);

        // 失敗しても次の周期へ進む
        store.ThrowOnDelete = null;
        await AdvanceUntilAsync(time, options.SweepInterval, () => store.Thresholds.Count > 0);

        await service.StopAsync(CancellationToken.None);

        Assert.NotEmpty(store.Thresholds);
    }

    /// <summary>条件が満たされるまで、時計を進めながら待つ。</summary>
    /// <remarks>
    /// <para>
    /// **1 回進めるだけでは足りない。** <c>StartAsync</c> が返った時点で、
    /// 常駐側がまだ <c>Task.Delay</c> へ入っていないことがある。
    /// **入る前に進めた分は無かったことになる**ので、そのまま待つと永久に起きない。
    /// </para>
    /// <para>
    /// 手元では速くて通り、CI で落ちた（2026-08-19）。**待ち時間を伸ばしても直らない。**
    /// 進め直すことでしか埋まらない。
    /// </para>
    /// </remarks>
    private static async Task AdvanceUntilAsync(
        FakeTimeProvider time,
        TimeSpan step,
        Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            time.Advance(step);

            // **本物の時間で少し待つ。** 常駐側は別のタスクなので、進めた直後は走っていない
            await Task.Delay(20);
        }
    }
}
