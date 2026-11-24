using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 管理 API のトークンの枠（Issue #1027）。認証の失敗の枠（IP ごと・別枠）とトークン単位の枠が、
/// 窓の中だけ効き、恒久の締め出しにならないこと。共有ストアが落ちてもプロセス内へ倒れること。
/// </summary>
public class AdminApiTokenRateGateTests
{
    private sealed class FakeSharedStore : ISharedRateLimitStore
    {
        private readonly Dictionary<string, int> _counts = [];

        public bool Throws { get; set; }

        public List<(string Partition, int Permit, int Limit)> Calls { get; } = [];

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

            Calls.Add((partition, permitCount, permitLimit));
            _counts.TryGetValue(partition, out var count);
            count += permitCount;
            _counts[partition] = count;
            return ValueTask.FromResult(new SharedRateLimitResult(count <= permitLimit, TimeSpan.FromSeconds(42)));
        }
    }

    private static (AdminApiTokenRateGate Gate, FakeTimeProvider Time) Build(ISharedRateLimitStore? store = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-24T00:00:30Z"));
        return (new AdminApiTokenRateGate(store, NullLogger<AdminApiTokenRateGate>.Instance, time), time);
    }

    // ---- 失敗の枠（IP ごと） ------------------------------------------------------------------

    [Fact]
    public async Task 失敗が上限に達した送信元は_見るだけで止まる()
    {
        var (gate, _) = Build();

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await gate.PeekFailuresAsync("192.0.2.1", 3)).Allowed);
            await gate.RecordFailureAsync("192.0.2.1", 3);
        }

        var blocked = await gate.PeekFailuresAsync("192.0.2.1", 3);

        Assert.False(blocked.Allowed);
        Assert.True(blocked.RetryAfter >= TimeSpan.FromSeconds(1));
        // 別の送信元は巻き込まない
        Assert.True((await gate.PeekFailuresAsync("192.0.2.2", 3)).Allowed);
    }

    [Fact]
    public async Task 見るだけでは数えない()
    {
        var (gate, _) = Build();

        for (var i = 0; i < 50; i++)
        {
            Assert.True((await gate.PeekFailuresAsync("192.0.2.1", 3)).Allowed);
        }
    }

    [Fact]
    public async Task 窓が過ぎれば戻る_恒久の締め出しはしない()
    {
        var (gate, time) = Build();
        for (var i = 0; i < 3; i++)
        {
            await gate.RecordFailureAsync("192.0.2.1", 3);
        }

        Assert.False((await gate.PeekFailuresAsync("192.0.2.1", 3)).Allowed);

        time.Advance(AdminApiTokenRateGate.FailureWindow + TimeSpan.FromSeconds(1));

        Assert.True((await gate.PeekFailuresAsync("192.0.2.1", 3)).Allowed);
    }

    [Fact]
    public async Task 共有ストアでは_見るときは0足して_数えるときは1足す()
    {
        var store = new FakeSharedStore();
        var (gate, _) = Build(store);

        await gate.PeekFailuresAsync("192.0.2.1", 3);
        await gate.RecordFailureAsync("192.0.2.1", 3);

        Assert.Equal(("admin-token-fail:192.0.2.1", 0, 2), store.Calls[0]);
        Assert.Equal(("admin-token-fail:192.0.2.1", 1, 3), store.Calls[1]);
    }

    [Fact]
    public async Task 共有ストアで上限に達した送信元は止まる()
    {
        var store = new FakeSharedStore();
        var (gate, _) = Build(store);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await gate.PeekFailuresAsync("192.0.2.1", 3)).Allowed);
            await gate.RecordFailureAsync("192.0.2.1", 3);
        }

        var blocked = await gate.PeekFailuresAsync("192.0.2.1", 3);

        Assert.False(blocked.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(42), blocked.RetryAfter);
    }

    [Fact]
    public async Task 共有ストアが落ちたら_プロセス内の数えへ倒れる()
    {
        var store = new FakeSharedStore { Throws = true };
        var (gate, _) = Build(store);

        for (var i = 0; i < 3; i++)
        {
            await gate.RecordFailureAsync("192.0.2.1", 3);
        }

        Assert.False((await gate.PeekFailuresAsync("192.0.2.1", 3)).Allowed);
        Assert.True((await gate.AcquireTokenAsync(Guid.NewGuid(), write: false, limit: 1)).Allowed);
    }

    // ---- トークン単位の枠 ---------------------------------------------------------------------

    [Fact]
    public async Task トークンの枠は_読み取りと書き込みで別で_トークンごとに数える()
    {
        var (gate, _) = Build();
        var token = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.True((await gate.AcquireTokenAsync(token, write: true, limit: 2)).Allowed);
        Assert.True((await gate.AcquireTokenAsync(token, write: true, limit: 2)).Allowed);
        var limited = await gate.AcquireTokenAsync(token, write: true, limit: 2);

        Assert.False(limited.Allowed);
        Assert.True(limited.RetryAfter >= TimeSpan.FromSeconds(1));
        // 読み取りの枠・別のトークンの枠は影響を受けない
        Assert.True((await gate.AcquireTokenAsync(token, write: false, limit: 2)).Allowed);
        Assert.True((await gate.AcquireTokenAsync(other, write: true, limit: 2)).Allowed);
    }

    [Fact]
    public async Task トークンの枠も_窓が過ぎれば戻る()
    {
        var (gate, time) = Build();
        var token = Guid.NewGuid();
        Assert.True((await gate.AcquireTokenAsync(token, write: true, limit: 1)).Allowed);
        Assert.False((await gate.AcquireTokenAsync(token, write: true, limit: 1)).Allowed);

        time.Advance(AdminApiTokenRateGate.TokenWindow + TimeSpan.FromSeconds(1));

        Assert.True((await gate.AcquireTokenAsync(token, write: true, limit: 1)).Allowed);
    }

    [Fact]
    public async Task 共有ストアのトークンの枠は_書き込みと読み取りの区画が別()
    {
        var store = new FakeSharedStore();
        var (gate, _) = Build(store);
        var token = Guid.NewGuid();

        await gate.AcquireTokenAsync(token, write: true, limit: 5);
        await gate.AcquireTokenAsync(token, write: false, limit: 9);

        Assert.Equal($"admin-token:write:{token:N}", store.Calls[0].Partition);
        Assert.Equal($"admin-token:read:{token:N}", store.Calls[1].Partition);
    }

    // ---- 記録の間引き -------------------------------------------------------------------------

    [Fact]
    public void 枠を超えた記録は_窓ごとに1回だけ()
    {
        var (gate, time) = Build();

        Assert.True(gate.TryMarkReported("t:write", AdminApiTokenRateGate.TokenWindow));
        Assert.False(gate.TryMarkReported("t:write", AdminApiTokenRateGate.TokenWindow));
        Assert.True(gate.TryMarkReported("t:read", AdminApiTokenRateGate.TokenWindow));

        time.Advance(AdminApiTokenRateGate.TokenWindow + TimeSpan.FromSeconds(1));

        Assert.True(gate.TryMarkReported("t:write", AdminApiTokenRateGate.TokenWindow));
    }

    // ---- 設定 ---------------------------------------------------------------------------------

    [Fact]
    public void 枠の設定は_既定があり_範囲外は設定名つきで起動を止める()
    {
        var defaults = AdminApiTokenOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(20, defaults.FailuresPer5Min);
        Assert.Equal(120, defaults.ReadsPerMin);
        Assert.Equal(30, defaults.WritesPerMin);
        Assert.True(defaults.WritesPerMin < defaults.ReadsPerMin);

        var set = AdminApiTokenOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminApiTokenOptions.FailuresPer5MinSetting] = "5",
                [AdminApiTokenOptions.ReadsPerMinSetting] = "7",
                [AdminApiTokenOptions.WritesPerMinSetting] = "3",
            })
            .Build());
        Assert.Equal((5, 7, 3), (set.FailuresPer5Min, set.ReadsPerMin, set.WritesPerMin));

        foreach (var setting in new[]
        {
            AdminApiTokenOptions.FailuresPer5MinSetting,
            AdminApiTokenOptions.ReadsPerMinSetting,
            AdminApiTokenOptions.WritesPerMinSetting,
        })
        {
            foreach (var bad in new[] { "0", "-1", "abc" })
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { [setting] = bad })
                    .Build();
                var exception = Assert.Throws<InvalidOperationException>(
                    () => AdminApiTokenOptions.FromConfiguration(configuration));
                Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
            }
        }
    }
}
