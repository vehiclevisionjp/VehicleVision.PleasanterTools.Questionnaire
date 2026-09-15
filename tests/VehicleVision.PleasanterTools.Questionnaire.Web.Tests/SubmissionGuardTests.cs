using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>bot 対策（送信チケット・最短時間・ハニーポット）の試験。</summary>
public sealed class SubmissionGuardTests
{
    private const string PublicId = "AbCdEfGhIjKlMnOp";
    private const string ResponseToken = "0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string Key = SecretProtector.GenerateKey();

    private static readonly DateTimeOffset Origin = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static (SubmissionGuard Guard, FakeTimeProvider Time) Create(
        SubmissionGuardOptions? options = null)
    {
        var time = new FakeTimeProvider(Origin);
        return (new SubmissionGuard(Key, options ?? new SubmissionGuardOptions(), time), time);
    }

    [Fact]
    public void 最短時間を過ぎたチケットは通る()
    {
        var (guard, time) = Create();
        var ticket = guard.Issue(PublicId, ResponseToken);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(guard.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 発行した直後の送信は弾く()
    {
        var (guard, time) = Create();
        var ticket = guard.Issue(PublicId, ResponseToken);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(
            SubmissionGuardRejection.TooFast,
            guard.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void ハニーポットが埋まっていたら弾く()
    {
        var (guard, time) = Create();
        var ticket = guard.Issue(PublicId, ResponseToken);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            SubmissionGuardRejection.Honeypot,
            guard.Check(ticket, trap: "https://example.com/", PublicId, ResponseToken));
    }

    [Fact]
    public void チケットが付いていなければ弾く()
    {
        var (guard, _) = Create();

        Assert.Equal(
            SubmissionGuardRejection.MissingTicket,
            guard.Check(ticket: null, trap: null, PublicId, ResponseToken));
        Assert.Equal(
            SubmissionGuardRejection.MissingTicket,
            guard.Check(ticket: string.Empty, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 別のアンケートで発行したチケットは通らない()
    {
        var (guard, time) = Create();
        var ticket = guard.Issue("QrStUvWxYz012345", ResponseToken);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            SubmissionGuardRejection.InvalidTicket,
            guard.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 別の回答トークンで発行したチケットは通らない()
    {
        // **1 枚のチケットで送信待ちの行をいくつも作らせない。**
        // 回答トークンが違えば別の行になるので、ここを通すと連投の道になる
        var (guard, time) = Create();
        var ticket = guard.Issue(PublicId, ResponseToken);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            SubmissionGuardRejection.InvalidTicket,
            guard.Check(ticket, trap: null, PublicId, new string('f', 48)));
    }

    [Fact]
    public void 発行時刻を書き換えたチケットは通らない()
    {
        var (guard, time) = Create();
        var ticket = guard.Issue(PublicId, ResponseToken);
        var parts = ticket.Split('.');

        // 最短時間を待たずに済むよう、発行時刻だけ過去へずらしてみる
        var forged = string.Join(
            '.', parts[0], long.Parse(parts[1]) - 600, parts[2]);

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(
            SubmissionGuardRejection.InvalidTicket,
            guard.Check(forged, trap: null, PublicId, ResponseToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("t1")]
    [InlineData("t1.abc.xyz")]
    [InlineData("t2.1755600000.xyz")]
    [InlineData("t1.-1.xyz")]
    [InlineData("t1.1755600000.xyz.extra")]
    public void 書式が壊れたチケットは通らない(string ticket)
    {
        var (guard, _) = Create();

        Assert.NotNull(guard.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 有効期間を過ぎたチケットは通らない()
    {
        var (guard, time) = Create();
        var ticket = guard.Issue(PublicId, ResponseToken);

        time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        Assert.Equal(
            SubmissionGuardRejection.ExpiredTicket,
            guard.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 別の鍵で発行したチケットは通らない()
    {
        var time = new FakeTimeProvider(Origin);
        var options = new SubmissionGuardOptions();
        var issuer = new SubmissionGuard(SecretProtector.GenerateKey(), options, time);
        var verifier = new SubmissionGuard(Key, options, time);

        var ticket = issuer.Issue(PublicId, ResponseToken);
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(
            SubmissionGuardRejection.InvalidTicket,
            verifier.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 未来の発行時刻は通らない()
    {
        // **インスタンス間で時計がずれていても通さない。**
        // 通すと、発行時刻を先へ倒して最短時間の判定を素通りできてしまう
        var options = new SubmissionGuardOptions();
        var issuer = new SubmissionGuard(
            Key, options, new FakeTimeProvider(Origin.AddHours(1)));
        var verifier = new SubmissionGuard(Key, options, new FakeTimeProvider(Origin));

        var ticket = issuer.Issue(PublicId, ResponseToken);

        Assert.Equal(
            SubmissionGuardRejection.InvalidTicket,
            verifier.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 無効にすると何も見ない()
    {
        // **検証環境で自動試験を回すための逃げ道。** 本番で切らないこと
        var (guard, _) = Create(new SubmissionGuardOptions { Enabled = false });

        Assert.Null(guard.Check(ticket: null, trap: "bot", PublicId, ResponseToken));
    }

    [Fact]
    public void 最短時間と有効期間は設定で変えられる()
    {
        var (guard, time) = Create(new SubmissionGuardOptions
        {
            MinimumElapsed = TimeSpan.FromSeconds(30),
            Lifetime = TimeSpan.FromMinutes(10),
        });
        var ticket = guard.Issue(PublicId, ResponseToken);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            SubmissionGuardRejection.TooFast,
            guard.Check(ticket, trap: null, PublicId, ResponseToken));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(guard.Check(ticket, trap: null, PublicId, ResponseToken));

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(
            SubmissionGuardRejection.ExpiredTicket,
            guard.Check(ticket, trap: null, PublicId, ResponseToken));
    }

    [Fact]
    public void 鍵が短ければ組み立てで落ちる()
    {
        // **弱い鍵を黙って受け取らない。** 起動時に落ちる方が安全
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        Assert.Throws<ArgumentException>(() =>
            new SubmissionGuard(shortKey, new SubmissionGuardOptions(), TimeProvider.System));
    }
}
