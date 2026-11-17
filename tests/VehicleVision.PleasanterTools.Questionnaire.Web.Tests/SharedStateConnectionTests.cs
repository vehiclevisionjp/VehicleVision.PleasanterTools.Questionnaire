using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>共有ストアが止まっているときに、長く待たせず拒否すること（Issue #899）。</summary>
public class SharedStateConnectionTests
{
    [Fact]
    public void 接続設定は待ち時間を揃え_切断中の命令をすぐ失敗させる()
    {
        // 接続文字列に別の待ち時間があっても、設定の値で上書きする
        var options = SharedStateConnection.CreateOptions(
            "127.0.0.1:6379,password=secret,syncTimeout=9000,connectTimeout=9000", 1500);

        Assert.Equal(1500, options.ConnectTimeout);
        Assert.Equal(1500, options.SyncTimeout);
        Assert.Equal(1500, options.AsyncTimeout);
        Assert.False(options.AbortOnConnectFail);
        Assert.False(options.BacklogPolicy.QueueWhileDisconnected);

        // 接続文字列のほかの指定（資格情報）は残す
        Assert.Equal("secret", options.Password);
    }

    /// <summary>
    /// 接続できないアドレス（誰も待ち受けていないポート）へ向けた共有ストアで、
    /// ルックアップの枠の判定が <see cref="LookupRateOutcome.Unavailable"/> を短時間で返すこと。
    /// 既定の待ち時間（5 秒）のままだと 1 回ごとに約 5 秒かかり、3 回で 15 秒を超える。
    /// 余裕を持たせ、3 回で 4.5 秒未満を上限にする。
    /// </summary>
    [Fact]
    public async Task 共有ストアに接続できないとき_枠の判定は短時間で拒否を返す()
    {
        var options = SharedStateConnection.CreateOptions(
            $"127.0.0.1:{UnusedPort()}", StartupSettingValidation.DefaultSharedStateTimeoutMilliseconds);
        await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        var store = new RedisSharedRateLimitStore(
            connection, new SharedStateOptions { UseRedis = true });
        using var gate = new LookupRateGate(store, NullLogger<LookupRateGate>.Instance);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            var decision = await gate.AcquireAsync("ip", "203.0.113.1", 100, TimeSpan.FromMinutes(1));

            // 上限が 100 でも、障害のあいだは毎回拒否する（プロセス内の枠で通さない）
            Assert.Equal(LookupRateOutcome.Unavailable, decision.Outcome);
            Assert.Equal(TimeSpan.FromSeconds(5), decision.RetryAfter);
        }

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(4.5),
            $"拒否までに {stopwatch.Elapsed.TotalSeconds:F1} 秒かかった");
    }

    private static int UnusedPort()
    {
        // 一時的に待ち受けて番号をもらい、すぐ閉じる（誰も待ち受けていないポートにする）
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
