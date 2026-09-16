using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>複数プロセス用の固定窓と障害時の倒れ方を確かめる。</summary>
public class SharedFixedWindowRateLimiterTests
{
    private sealed class CountingStore : ISharedRateLimitStore
    {
        private int _count;

        public bool Throws { get; init; }

        public ValueTask<SharedRateLimitResult> AcquireAsync(
            string partition,
            int permitCount,
            int permitLimit,
            TimeSpan window,
            CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("Redis が応えない");
            }

            var count = Interlocked.Add(ref _count, permitCount);
            return ValueTask.FromResult(
                new SharedRateLimitResult(count <= permitLimit, window));
        }
    }

    [Fact]
    public async Task 別プロセス相当の枠を合計して上限を守る()
    {
        var store = new CountingStore();
        using var first = Build(store, permitLimit: 10);
        using var second = Build(store, permitLimit: 10);

        var leases = new List<bool>();
        for (var i = 0; i < 6; i++)
        {
            leases.Add((await first.AcquireAsync()).IsAcquired);
            leases.Add((await second.AcquireAsync()).IsAcquired);
        }

        Assert.Equal(10, leases.Count(acquired => acquired));
        Assert.Equal(2, leases.Count(acquired => !acquired));
    }

    [Fact]
    public async Task 同期取得では通さず非同期取得で共有枠を使う()
    {
        var store = new CountingStore();
        using var limiter = Build(store, permitLimit: 1);

        Assert.False(limiter.AttemptAcquire().IsAcquired);
        Assert.True((await limiter.AcquireAsync()).IsAcquired);
        Assert.False((await limiter.AcquireAsync()).IsAcquired);
    }

    [Fact]
    public async Task KVSが落ちてもプロセス内の上限を超えた要求は拒否する()
    {
        using var limiter = Build(new CountingStore { Throws = true }, permitLimit: 10);

        var leases = new List<bool>();
        for (var i = 0; i < 12; i++)
        {
            leases.Add((await limiter.AcquireAsync()).IsAcquired);
        }

        Assert.Equal(10, leases.Count(acquired => acquired));
        Assert.Equal(2, leases.Count(acquired => !acquired));
    }

    [Fact]
    public void 未設定ならプロセス内を選ぶ()
    {
        var options = SharedStateOptions.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.False(options.UseRedis);
    }

    [Fact]
    public void Redis以外の共有先は黙って受け入れない()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SharedStateOptions.StoreKey] = "Memory",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => SharedStateOptions.FromConfiguration(configuration));
    }

    private static SharedFixedWindowRateLimiter Build(
        ISharedRateLimitStore store,
        int permitLimit) =>
        new(
            store,
            "login:192.0.2.1",
            permitLimit,
            TimeSpan.FromMinutes(5),
            NullLogger.Instance);
}
