using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using nClam;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>ClamAV へ問い合わせるスキャナの試験。**clamd は動かさない。**</summary>
public class ClamAvVirusScannerTests
{
    private static readonly byte[] Content = [0x89, 0x50, 0x4E, 0x47];

    /// <summary><c>clamd</c> の代わり。応答をそのまま決め打ちする。</summary>
    private sealed class FakeClamClient(Func<CancellationToken, Task<ClamScanResult>> respond)
        : IClamClient
    {
        public int MaxChunkSize { get; set; }

        public long MaxStreamSize { get; set; }

        public string? Server { get; set; }

        public IPAddress? ServerIP { get; set; }

        public int Port { get; set; }

        public Task<ClamScanResult> SendAndScanFileAsync(
            Stream sourceStream, CancellationToken cancellationToken) => respond(cancellationToken);

        public Task<ClamScanResult> SendAndScanFileAsync(Stream sourceStream) =>
            respond(CancellationToken.None);

        public Task<ClamScanResult> SendAndScanFileAsync(byte[] fileData) =>
            respond(CancellationToken.None);

        public Task<ClamScanResult> SendAndScanFileAsync(
            byte[] fileData, CancellationToken cancellationToken) => respond(cancellationToken);

        public Task<ClamScanResult> SendAndScanFileAsync(string filePath) =>
            respond(CancellationToken.None);

        public Task<ClamScanResult> SendAndScanFileAsync(
            string filePath, CancellationToken cancellationToken) => respond(cancellationToken);

        // 以下はこのスキャナが使わない口。**使ったら気づけるように落とす**
        public Task<string> GetVersionAsync() => throw new NotSupportedException();

        public Task<string> GetVersionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> PingAsync() => throw new NotSupportedException();

        public Task<bool> PingAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryPingAsync() => throw new NotSupportedException();

        public Task<bool> TryPingAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClamScanResult> ScanFileOnServerAsync(string filePath) =>
            throw new NotSupportedException();

        public Task<ClamScanResult> ScanFileOnServerAsync(
            string filePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClamScanResult> ScanFileOnServerMultithreadedAsync(string filePath) =>
            throw new NotSupportedException();

        public Task<ClamScanResult> ScanFileOnServerMultithreadedAsync(
            string filePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task Shutdown(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static ClamAvVirusScanner Scanner(
        Func<CancellationToken, Task<ClamScanResult>> respond,
        TimeSpan? timeout = null) =>
        new(
            new VirusScanOptions
            {
                Enabled = true,
                ClamAvHost = "clamav",
                ClamAvPort = 3310,
                ClamAvTimeout = timeout ?? TimeSpan.FromSeconds(30),
            },
            NullLogger<ClamAvVirusScanner>.Instance,
            () => new FakeClamClient(respond));

    private static Func<CancellationToken, Task<ClamScanResult>> Responds(string rawResult) =>
        _ => Task.FromResult(new ClamScanResult(rawResult));

    [Fact]
    public async Task 検出が無ければ通す()
    {
        var verdict = await Scanner(Responds("stream: OK")).ScanAsync(Content, default);

        Assert.Equal(ScanVerdict.Clean, verdict);
    }

    [Fact]
    public async Task 検出したら検出ありを返す()
    {
        var verdict = await Scanner(Responds("stream: Eicar-Signature FOUND"))
            .ScanAsync(Content, default);

        Assert.Equal(ScanVerdict.Infected, verdict);
    }

    [Fact]
    public async Task clamdがエラーを返したら通さない()
    {
        // **判定が付かなかったものを「検出なし」にしない**
        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => Scanner(Responds("stream: something ERROR")).ScanAsync(Content, default));
    }

    [Fact]
    public async Task 解釈できない応答は通さない()
    {
        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => Scanner(Responds("なにこれ")).ScanAsync(Content, default));
    }

    [Fact]
    public async Task 到達できなければ通さない()
    {
        // 起動直後（定義データベースの取得中）もここに来る
        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => Scanner(_ => throw new SocketException(10061)).ScanAsync(Content, default));
    }

    [Fact]
    public async Task 時間内に応答しなければ通さない()
    {
        var scanner = Scanner(
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return new ClamScanResult("stream: OK");
            },
            timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => scanner.ScanAsync(Content, default));
    }

    [Fact]
    public async Task 呼び出し側の中断はそのまま伝える()
    {
        // **中断はスキャナの不調ではない。** 取り違えると原因が追えなくなる
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Scanner(
                    async cancellationToken =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                        return new ClamScanResult("stream: OK");
                    })
                .ScanAsync(Content, cancellation.Token));
    }
}
