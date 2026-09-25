using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class ClamAvHealthMonitorTests
{
    private sealed class Scanner : IVirusScanner
    {
        public ScanVerdict Verdict { get; set; } = ScanVerdict.Clean;
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public List<byte[]> Contents { get; } = [];

        public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Contents.Add(content.ToArray());
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(Verdict);
        }
    }

    private sealed class Fixture
    {
        public Scanner Scanner { get; } = new();
        public FakeAdminNotificationStore Notifications { get; } = new();
        public ListLogger<ClamAvHealthMonitor> Logger { get; } = new();
        public ClamAvHealthMonitor Monitor { get; }

        public Fixture()
        {
            Monitor = new(Scanner, Notifications, new FakeTimeProvider(), Logger);
        }

        public Task Check() => Monitor.CheckAsync(default);
    }

    [Fact]
    public async Task 正常のままなら通知も状態変化のログも増やさない()
    {
        var f = new Fixture();
        await f.Check();
        await f.Check();
        Assert.Empty(f.Notifications.Raised);
        Assert.Empty(f.Logger.Messages);
        Assert.Equal(2, f.Scanner.Calls);
        Assert.All(f.Scanner.Contents, content =>
            Assert.Equal("Questionnaire ClamAV health check"u8.ToArray(), content));
    }

    [Fact]
    public async Task 初回の異常と復旧と再発だけを全体のお知らせとログへ出す()
    {
        var f = new Fixture();
        f.Scanner.Failure = new VirusScannerUnavailableException("接続できない");
        await f.Check();
        await f.Check();
        f.Scanner.Failure = null;
        await f.Check();
        await f.Check();
        f.Scanner.Failure = new VirusScannerUnavailableException("時間切れ");
        await f.Check();

        Assert.Equal(
            [((int)AdminNotificationKind.ClamAvUnavailable, Guid.Empty),
             ((int)AdminNotificationKind.ClamAvRecovered, Guid.Empty),
             ((int)AdminNotificationKind.ClamAvUnavailable, Guid.Empty)],
            f.Notifications.Raised);
        Assert.Equal(3, f.Logger.Messages.Count);
        Assert.StartsWith("Error:", f.Logger.Messages[0]);
        Assert.StartsWith("Information:", f.Logger.Messages[1]);
    }

    [Fact]
    public async Task 正常で起動しても後から起きた異常を通知する()
    {
        var f = new Fixture();
        await f.Check();
        f.Scanner.Failure = new VirusScannerUnavailableException("接続できない");
        await f.Check();
        Assert.Equal((int)AdminNotificationKind.ClamAvUnavailable,
            Assert.Single(f.Notifications.Raised).Kind);
    }

    [Fact]
    public async Task 固定テキストを検出なしと判定できなければ異常にする()
    {
        var f = new Fixture();
        f.Scanner.Verdict = ScanVerdict.Infected;
        await f.Check();
        Assert.Equal((int)AdminNotificationKind.ClamAvUnavailable,
            Assert.Single(f.Notifications.Raised).Kind);
    }

    [Fact]
    public async Task 保存に失敗した通知は次回に再試行する()
    {
        var f = new Fixture();
        f.Scanner.Failure = new VirusScannerUnavailableException("接続できない");
        f.Notifications.Throws = true;
        await f.Check();
        Assert.Empty(f.Notifications.Raised);
        f.Notifications.Throws = false;
        await f.Check();
        await f.Check();
        Assert.Single(f.Notifications.Raised);
        Assert.Single(f.Logger.Messages, message => message.Contains("状態を検知した"));
        Assert.Contains(f.Logger.Messages, message => message.Contains("保存できなかった"));
    }

    [Fact]
    public async Task 保存待ちの間に復旧したら古い異常より復旧を優先する()
    {
        var f = new Fixture();
        f.Scanner.Failure = new VirusScannerUnavailableException("接続できない");
        f.Notifications.Throws = true;
        await f.Check();
        f.Scanner.Failure = null;
        f.Notifications.Throws = false;
        await f.Check();
        Assert.Equal((int)AdminNotificationKind.ClamAvRecovered,
            Assert.Single(f.Notifications.Raised).Kind);
    }

    [Fact]
    public async Task 停止は異常として通知しない()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.Monitor.CheckAsync(cancellation.Token));
        Assert.Empty(f.Notifications.Raised);
        Assert.Empty(f.Logger.Messages);
    }

    /// <summary>待機タイマーの登録を待ってから仮想時計を進める。</summary>
    private sealed class Clock : TimeProvider
    {
        public FakeTimeProvider Fake { get; } = new();
        public Channel<bool> Scheduled { get; } = Channel.CreateUnbounded<bool>();
        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();
        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(callback, state, dueTime, period);
            Scheduled.Writer.TryWrite(true);
            return timer;
        }

        public async Task WaitForDelay() =>
            await Scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DB起動後に確認し一分ごとに続け停止できる()
    {
        var f = new Fixture();
        var startup = new DatabaseStartupState();
        var clock = new Clock();
        using var service = new ClamAvHealthMonitorHostedService(
            f.Monitor, startup, clock, NullLogger<ClamAvHealthMonitorHostedService>.Instance);
        await service.StartAsync(default);
        Assert.Equal(0, f.Scanner.Calls);
        startup.MarkReady();
        await clock.WaitForDelay();
        Assert.Equal(1, f.Scanner.Calls);
        clock.Fake.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, f.Scanner.Calls);
        clock.Fake.Advance(TimeSpan.FromSeconds(1));
        await clock.WaitForDelay();
        Assert.Equal(2, f.Scanner.Calls);
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fake.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, f.Scanner.Calls);
    }

    [Fact]
    public async Task DB起動待ちの間にも停止できる()
    {
        var f = new Fixture();
        using var service = new ClamAvHealthMonitorHostedService(
            f.Monitor, new DatabaseStartupState(), new FakeTimeProvider(),
            NullLogger<ClamAvHealthMonitorHostedService>.Instance);
        await service.StartAsync(default);
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Scanner.Calls);
    }
}
