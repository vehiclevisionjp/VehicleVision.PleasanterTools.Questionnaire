using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>共有状態を本物の KVS へ載せて確かめる。</summary>
/// <remarks>
/// <para>
/// **差し替えた口の単体試験では Lua の誤りが分からない。**
/// 綴りを間違えても、本番で初めて落ちる。
/// </para>
/// <para>
/// <c>QUESTIONNAIRE_VALKEY_CONNECTIONSTRING</c> が無い場合は実行しない。
/// 起動方法は <c>_documents/開発環境.md</c> を参照すること。
/// </para>
/// </remarks>
public class SharedStateStoreIntegrationTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_VALKEY_CONNECTIONSTRING");

    // **試験どうしが同じ鍵を触らないよう、走らせるたびに接頭辞を変える。**
    private static SharedStateOptions Options() => new()
    {
        UseRedis = true,
        KeyPrefix = $"questionnaire-test:{Guid.NewGuid():N}:",
    };

    [Fact]
    public async Task 固定窓は上限を超えた要求を拒み再試行までの時間を返す()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        var store = new RedisSharedRateLimitStore(connection, Options());
        var window = TimeSpan.FromSeconds(30);

        for (var i = 1; i <= 3; i++)
        {
            var allowed = await store.AcquireAsync("ip:203.0.113.1", 1, 3, window);
            Assert.True(allowed.Acquired, $"{i} 回目は通るはず");
        }

        var rejected = await store.AcquireAsync("ip:203.0.113.1", 1, 3, window);
        Assert.False(rejected.Acquired);

        // **PEXPIRE が効いていないと TTL が -1 になり、窓が永久に閉じたままになる。**
        Assert.InRange(rejected.RetryAfter, TimeSpan.FromSeconds(1), window);

        // 仕切りが違えば数えも別になる。
        var other = await store.AcquireAsync("ip:203.0.113.2", 1, 3, window);
        Assert.True(other.Acquired);
    }

    [Fact]
    public async Task 滞留は計測値と受付後の増分を足して共有する()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        var options = Options();
        var surveyId = Guid.NewGuid();
        var other = Guid.NewGuid();

        // **別インスタンスから読めることが要点。** 同じ接頭辞を共有させる。
        var writer = new RedisSharedBacklogStateStore(
            connection, options, NullLogger<RedisSharedBacklogStateStore>.Instance);
        var reader = new RedisSharedBacklogStateStore(
            connection, options, NullLogger<RedisSharedBacklogStateStore>.Instance);

        var sampled = await writer.GetOrSampleAsync(
            TimeSpan.FromMinutes(5),
            1,
            _ => Task.FromResult(new PendingBacklog(
                7,
                ImmutableDictionary<Guid, int>.Empty.Add(surveyId, 5))));
        Assert.Equal(7, sampled.Total);
        Assert.Equal(5, sampled.For(surveyId));

        await writer.IncrementAcceptedAsync(surveyId);
        await writer.IncrementAcceptedAsync(other);

        var state = await reader.GetOrSampleAsync(
            TimeSpan.FromMinutes(5),
            1,
            _ => throw new InvalidOperationException("間隔の内側では数え直さないはず"));
        Assert.Equal(9, state.Total);
        Assert.Equal(6, state.For(surveyId));
        Assert.Equal(1, state.For(other));
    }

    [Fact]
    public async Task 受付停止の旗は変わったときだけ真を返す()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        var options = Options();
        var surveyId = Guid.NewGuid();
        var store = new RedisSharedBacklogStateStore(
            connection, options, NullLogger<RedisSharedBacklogStateStore>.Instance);
        var another = new RedisSharedBacklogStateStore(
            connection, options, NullLogger<RedisSharedBacklogStateStore>.Instance);

        // **記録を要求ごとに書かないための戻り値。** 二重に書くと監査ログが埋まる
        Assert.True(await store.SetTotalBlockedAsync(blocked: true));
        Assert.False(await another.SetTotalBlockedAsync(blocked: true));

        Assert.True(await store.SetSurveyBlockedAsync(surveyId, blocked: true));
        Assert.False(await another.SetSurveyBlockedAsync(surveyId, blocked: true));

        var state = await another.GetOrSampleAsync(
            TimeSpan.FromMinutes(5),
            1,
            _ => Task.FromResult(PendingBacklog.Empty));
        Assert.True(state.TotalBlocked);
        Assert.Contains(surveyId, state.BlockedSurveys);

        Assert.True(await store.SetTotalBlockedAsync(blocked: false));
        Assert.True(await store.SetSurveyBlockedAsync(surveyId, blocked: false));
    }
}
