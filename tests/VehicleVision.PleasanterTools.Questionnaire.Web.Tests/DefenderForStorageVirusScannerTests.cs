using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>Defender for Storage 経路の試験。**Azure へはつながない。**</summary>
public class DefenderForStorageVirusScannerTests
{
    private static readonly byte[] Content = [0x89, 0x50, 0x4E, 0x47];

    /// <summary>DMZ のストレージの代わり。置かれた名前を覚える。</summary>
    private sealed class FakeBlobStore(Exception? uploadFailure = null) : IDmzBlobStore
    {
        public List<string> Uploaded { get; } = [];

        public List<string> Deleted { get; } = [];

        public Task UploadAsync(
            string blobName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (uploadFailure is not null)
            {
                throw uploadFailure;
            }

            Uploaded.Add(blobName);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken)
        {
            Deleted.Add(blobName);
            return Task.CompletedTask;
        }
    }

    private static VirusScanOptions Options(int timeoutSeconds = 5) => new()
    {
        Enabled = true,
        Provider = VirusScanProvider.DefenderForStorage,
        DefenderResultTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        EventGridKey = "合言葉",
    };

    /// <summary>判定が届く様子を模す。**置かれた blob 名へ後から結果を配る。**</summary>
    private static void PublishWhenUploaded(
        MalwareScanVerdicts verdicts,
        FakeBlobStore blobs,
        string scanResultType) =>
        _ = Task.Run(async () =>
        {
            while (blobs.Uploaded.Count == 0)
            {
                await Task.Delay(5);
            }

            verdicts.Publish(
                $"https://dmz.blob.core.windows.net/untrusted/{blobs.Uploaded[0]}",
                scanResultType);
        });

    [Fact]
    public async Task 検出なしの判定が届いたら通す()
    {
        var verdicts = new MalwareScanVerdicts();
        var blobs = new FakeBlobStore();
        var scanner = new DefenderForStorageVirusScanner(
            Options(), blobs, verdicts, NullLogger<DefenderForStorageVirusScanner>.Instance);
        PublishWhenUploaded(verdicts, blobs, "No threats found");

        Assert.Equal(ScanVerdict.Clean, await scanner.ScanAsync(Content, default));
        // **中身を残さない**
        Assert.Equal(blobs.Uploaded, blobs.Deleted);
    }

    [Fact]
    public async Task 検出の判定が届いたら検出ありを返す()
    {
        var verdicts = new MalwareScanVerdicts();
        var blobs = new FakeBlobStore();
        var scanner = new DefenderForStorageVirusScanner(
            Options(), blobs, verdicts, NullLogger<DefenderForStorageVirusScanner>.Instance);
        PublishWhenUploaded(verdicts, blobs, "Malicious");

        Assert.Equal(ScanVerdict.Infected, await scanner.ScanAsync(Content, default));
        Assert.Single(blobs.Deleted);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Not scanned")]
    public async Task 検査できなかった判定は通さない(string scanResultType)
    {
        // **「まだ検査されていない」を「通してよい」と解釈しない**
        var verdicts = new MalwareScanVerdicts();
        var blobs = new FakeBlobStore();
        var scanner = new DefenderForStorageVirusScanner(
            Options(), blobs, verdicts, NullLogger<DefenderForStorageVirusScanner>.Instance);
        PublishWhenUploaded(verdicts, blobs, scanResultType);

        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => scanner.ScanAsync(Content, default));
    }

    [Fact]
    public async Task 判定が時間内に届かなければ通さない()
    {
        var blobs = new FakeBlobStore();
        var scanner = new DefenderForStorageVirusScanner(
            Options(timeoutSeconds: 0),
            blobs,
            new MalwareScanVerdicts(),
            NullLogger<DefenderForStorageVirusScanner>.Instance);

        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => scanner.ScanAsync(Content, default));
        // 判定が来なくても置きっぱなしにしない
        Assert.Single(blobs.Deleted);
    }

    [Fact]
    public async Task DMZへ置けなければ通さない()
    {
        var scanner = new DefenderForStorageVirusScanner(
            Options(),
            new FakeBlobStore(new InvalidOperationException("置けない")),
            new MalwareScanVerdicts(),
            NullLogger<DefenderForStorageVirusScanner>.Instance);

        await Assert.ThrowsAsync<VirusScannerUnavailableException>(
            () => scanner.ScanAsync(Content, default));
    }

    [Fact]
    public async Task 判定はblobのURIから待ち合わせ先を引く()
    {
        var verdicts = new MalwareScanVerdicts();
        using var registration = verdicts.Register("abc123");

        Assert.True(verdicts.Publish(
            "https://dmz.blob.core.windows.net/untrusted/abc123?se=2026-08-18", "Malicious"));
        Assert.Equal("Malicious", await verdicts.WaitAsync("abc123", TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public void 待っていない添付の判定は捨てる()
    {
        // 時間切れの後や、別インスタンス宛ての通知
        Assert.False(new MalwareScanVerdicts().Publish("abc123", "No threats found"));
    }

    [Fact]
    public void 待ち合わせは後始末で消える()
    {
        // **残したままにすると溜まり続ける**
        var verdicts = new MalwareScanVerdicts();
        verdicts.Register("abc123").Dispose();

        Assert.False(verdicts.Publish("abc123", "No threats found"));
    }
}
