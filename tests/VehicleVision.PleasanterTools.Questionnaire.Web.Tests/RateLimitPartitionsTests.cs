using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>アンケート 1 本あたりの枠が、回答画面以外を巻き込まないことを見る。</summary>
/// <remarks>
/// ⚠️ **レート制限には試験が 1 つも無かった。** Issue #311 の取り違えも、
/// 写しの一式が 429 で全滅して初めて分かった。
/// </remarks>
public class RateLimitPartitionsTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static RateLimiter LimiterFor(string? publicId, int permitLimit)
    {
        var partition = RateLimitPartitions.Survey(
            publicId, permitLimit, Window, sharedStore: null, NullLogger.Instance);
        return partition.Factory(partition.PartitionKey);
    }

    [Theory]
    // ビルド成果物。**書体は文字の範囲ごとに分かれるので本数が多い**
    [InlineData("/assets/admin-DPWwLWYw.js")]
    [InlineData("/assets/theme-DTjGXjp6.css")]
    [InlineData("/assets/noto-sans-jp-42-wght-normal.woff2")]
    // 回答画面の本文画像
    [InlineData("/api/forms/pub-a/assets/0f8f7b2c-0000-0000-0000-000000000000")]
    public void 一画面で何本も出るものは緩い枠で数える(string path)
    {
        Assert.True(RateLimitPartitions.IsBulkAsset(new PathString(path)));
    }

    [Theory]
    [InlineData("/api/forms/pub-a")]
    [InlineData("/api/admin/surveys")]
    [InlineData("/healthz")]
    [InlineData("/admin")]
    // ⚠️ **接頭辞の一致だけで通さない。** /assetsomething は別物
    [InlineData("/assetsomething/x.js")]
    public void 通常の要求は通常の枠で数える(string path)
    {
        Assert.False(RateLimitPartitions.IsBulkAsset(new PathString(path)));
    }

    [Fact]
    public void 回答画面は本ごとに数えられ上限を超えると断られる()
    {
        using var limiter = LimiterFor("pub-a", 2);

        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.False(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void 別のアンケートは別の枠で数えられる()
    {
        var a = RateLimitPartitions.Survey(
            "pub-a", 2, Window, sharedStore: null, NullLogger.Instance);
        var b = RateLimitPartitions.Survey(
            "pub-b", 2, Window, sharedStore: null, NullLogger.Instance);

        Assert.NotEqual(a.PartitionKey, b.PartitionKey);
    }

    [Fact]
    public void 回答画面以外はこの段で数えない()
    {
        // **管理画面・監視 API・資産の配信は publicId を持たない。**
        // ⚠️ これらを 1 つの枠へまとめると、送信元に関係なく全体の合計が
        // 頭打ちになる（Issue #311）。1 段目の送信元ごとの枠で守る
        using var limiter = LimiterFor(publicId: null, permitLimit: 2);

        for (var i = 0; i < 50; i++)
        {
            Assert.True(limiter.AttemptAcquire().IsAcquired, $"{i + 1} 回目で断られた");
        }
    }
}
