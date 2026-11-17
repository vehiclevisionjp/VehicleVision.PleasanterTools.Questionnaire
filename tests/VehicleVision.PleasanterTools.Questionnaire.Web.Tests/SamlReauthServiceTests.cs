using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// SAML の強制再認証（ForceAuthn）の判定と、使い捨ての許可（Issue #1034。親 #1012）。
/// </summary>
/// <remarks>
/// 署名の検証（<c>Saml2Binding.Unbind</c>）は通った後の事実を渡して、<b>判定の決まり</b>
/// （AuthnInstant・InResponseTo・同一人物・使い捨て・期限・結び付け）を見る。
/// 署名付きの偽の IdP 応答を HTTP で通す所は結合試験（<c>SamlReauthEndToEndTests</c>）の受け持ち。
/// </remarks>
public class SamlReauthServiceTests
{
    private const string Session = "session-handle-a";

    private sealed record Harness(
        FakeAdminUserStore Users,
        FakeTimeProvider Time,
        MemoryAdminReauthStateStore Store,
        SamlReauthService Service,
        AdminUser User);

    private static async Task<Harness> CreateAsync(AdminApiTokenOptions? options = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-18T00:00:00Z"));
        var users = new FakeAdminUserStore(time);
        var user = (await users.CreateAsync(
            "sso-admin@example.test", new PasswordHasher().Hash("unused-password-1"), "Administrator", string.Empty))!;
        var store = new MemoryAdminReauthStateStore(time);
        var service = new SamlReauthService(store, users, options ?? new AdminApiTokenOptions { Enabled = true }, time);
        return new Harness(users, time, store, service, user);
    }

    /// <summary>要求を出した直後に、IdP が「いま」認証した応答の事実。</summary>
    private static SamlAssertionFacts Facts(
        Harness harness,
        string requestId,
        DateTimeOffset? instant,
        string? loginId = null,
        string? classRef = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport") =>
        new(requestId, loginId ?? harness.User.LoginId, instant, classRef);

    [Fact]
    public async Task 要求の後に認証した応答は許可を作り_許可は1回だけ使える()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        harness.Time.Advance(TimeSpan.FromSeconds(20));

        var outcome = await harness.Service.CompleteAsync(
            "_req1", Facts(harness, "_req1", harness.Time.GetUtcNow()));

        Assert.Equal(SamlReauthOutcome.Granted, outcome);
        Assert.True(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));

        // **使い回せない**
        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
    }

    [Fact]
    public async Task IdPがForceAuthnを無視して古い認証の時刻を返したら断る()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        harness.Time.Advance(TimeSpan.FromMinutes(1));

        // 要求の 1 時間前の認証（IdP のセッションの流用）
        var stale = harness.Time.GetUtcNow().AddHours(-1);
        var outcome = await harness.Service.CompleteAsync("_req1", Facts(harness, "_req1", stale));

        Assert.Equal(SamlReauthOutcome.AuthnInstantStale, outcome);
        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
    }

    [Theory]
    [InlineData(-29, true)]
    [InlineData(-31, false)]
    public async Task 時計のずれの許容は要求の30秒前まで(int secondsBeforeRequest, bool granted)
    {
        var harness = await CreateAsync();
        var requested = harness.Time.GetUtcNow();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        harness.Time.Advance(TimeSpan.FromSeconds(5));

        var outcome = await harness.Service.CompleteAsync(
            "_req1", Facts(harness, "_req1", requested.AddSeconds(secondsBeforeRequest)));

        Assert.Equal(granted ? SamlReauthOutcome.Granted : SamlReauthOutcome.AuthnInstantStale, outcome);
    }

    [Fact]
    public async Task 認証の時刻が無い_未来_前のセッションの流用は断る()
    {
        foreach (var (instantOffset, classRef, expected) in new (int? Seconds, string? Class, SamlReauthOutcome Outcome)[]
        {
            (null, "x", SamlReauthOutcome.AuthnInstantMissing),
            (600, "x", SamlReauthOutcome.AuthnInstantInFuture),
            (0, "urn:oasis:names:tc:SAML:2.0:ac:classes:PreviousSession", SamlReauthOutcome.PreviousSession),
        })
        {
            var harness = await CreateAsync();
            await harness.Service.BeginAsync("_req1", harness.User.Id, Session);

            DateTimeOffset? instant = instantOffset is null ? null : harness.Time.GetUtcNow().AddSeconds(instantOffset.Value);
            var outcome = await harness.Service.CompleteAsync(
                "_req1", Facts(harness, "_req1", instant, classRef: classRef));

            Assert.Equal(expected, outcome);
            Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
        }
    }

    [Fact]
    public async Task 応答が指す要求が違えば断る_出していない要求は受けない()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);

        // cookie の要求と、応答の InResponseTo が違う
        Assert.Equal(
            SamlReauthOutcome.RequestMismatch,
            await harness.Service.CompleteAsync("_req1", Facts(harness, "_other", harness.Time.GetUtcNow())));

        // 出していない要求（cookie も応答も同じ id だが、こちらは預けていない）
        Assert.Equal(
            SamlReauthOutcome.RequestUnknown,
            await harness.Service.CompleteAsync("_never", Facts(harness, "_never", harness.Time.GetUtcNow())));
    }

    [Fact]
    public async Task 同じ応答の再送は断る_失敗した要求も二度と使えない()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        var facts = Facts(harness, "_req1", harness.Time.GetUtcNow());

        Assert.Equal(SamlReauthOutcome.Granted, await harness.Service.CompleteAsync("_req1", facts));
        Assert.True(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));

        // **リプレイ**: 同じ応答をもう一度。許可は作られない
        Assert.Equal(SamlReauthOutcome.RequestUnknown, await harness.Service.CompleteAsync("_req1", facts));
        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));

        // 失敗した要求は、そのあと正しい応答が来ても使えない（総当たりを許さない）
        await harness.Service.BeginAsync("_req2", harness.User.Id, Session);
        Assert.Equal(
            SamlReauthOutcome.AuthnInstantStale,
            await harness.Service.CompleteAsync(
                "_req2", Facts(harness, "_req2", harness.Time.GetUtcNow().AddHours(-1))));
        Assert.Equal(
            SamlReauthOutcome.RequestUnknown,
            await harness.Service.CompleteAsync("_req2", Facts(harness, "_req2", harness.Time.GetUtcNow())));
    }

    [Fact]
    public async Task 別の人で再認証した応答は断る()
    {
        var harness = await CreateAsync();
        var other = (await harness.Users.CreateAsync(
            "other@example.test", new PasswordHasher().Hash("unused-password-1"), "Administrator", string.Empty))!;
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);

        var outcome = await harness.Service.CompleteAsync(
            "_req1", Facts(harness, "_req1", harness.Time.GetUtcNow(), loginId: other.LoginId));

        Assert.Equal(SamlReauthOutcome.UserMismatch, outcome);
        Assert.False(await harness.Service.TryConsumeGrantAsync(other.Id, Session));
        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
    }

    [Fact]
    public async Task ログイン_IDが無い_本アプリに居ない_止められていれば断る()
    {
        var harness = await CreateAsync();

        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        Assert.Equal(
            SamlReauthOutcome.UserMismatch,
            await harness.Service.CompleteAsync(
                "_req1", Facts(harness, "_req1", harness.Time.GetUtcNow(), loginId: " ")));

        await harness.Service.BeginAsync("_req2", harness.User.Id, Session);
        Assert.Equal(
            SamlReauthOutcome.UserMismatch,
            await harness.Service.CompleteAsync(
                "_req2", Facts(harness, "_req2", harness.Time.GetUtcNow(), loginId: "nobody@example.test")));

        await harness.Service.BeginAsync("_req3", harness.User.Id, Session);
        await harness.Users.SetDisabledAsync(harness.User.Id, true);
        Assert.Equal(
            SamlReauthOutcome.UserMismatch,
            await harness.Service.CompleteAsync("_req3", Facts(harness, "_req3", harness.Time.GetUtcNow())));
    }

    [Fact]
    public async Task 許可は利用者とセッションに結び付く()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        await harness.Service.CompleteAsync("_req1", Facts(harness, "_req1", harness.Time.GetUtcNow()));

        // 別のセッション・別の利用者では使えない（取り出しに失敗しても許可は消えない）
        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, "session-handle-b"));
        Assert.False(await harness.Service.TryConsumeGrantAsync(Guid.NewGuid(), Session));
        Assert.True(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
    }

    [Fact]
    public async Task 許可は短い期限で切れる()
    {
        var harness = await CreateAsync(new AdminApiTokenOptions { Enabled = true, ReloginWindow = TimeSpan.FromMinutes(5) });
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        await harness.Service.CompleteAsync("_req1", Facts(harness, "_req1", harness.Time.GetUtcNow()));

        harness.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
    }

    [Fact]
    public async Task IdPでの再認証が長引いて途中の状態の期限が切れたら断る()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);

        harness.Time.Advance(SamlReauthService.PendingLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(
            SamlReauthOutcome.RequestUnknown,
            await harness.Service.CompleteAsync("_req1", Facts(harness, "_req1", harness.Time.GetUtcNow())));
    }

    [Fact]
    public async Task 同時に取り出しても許可1回分で通るのは1つだけ()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_req1", harness.User.Id, Session);
        await harness.Service.CompleteAsync("_req1", Facts(harness, "_req1", harness.Time.GetUtcNow()));

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => harness.Service.TryConsumeGrantAsync(harness.User.Id, Session))));

        Assert.Equal(1, results.Count(granted => granted));
    }

    [Fact]
    public async Task 同時に複数の再認証を始めても_それぞれの要求は別々に使い切る()
    {
        var harness = await CreateAsync();
        await harness.Service.BeginAsync("_reqA", harness.User.Id, Session);
        await harness.Service.BeginAsync("_reqB", harness.User.Id, Session);

        Assert.Equal(
            SamlReauthOutcome.Granted,
            await harness.Service.CompleteAsync("_reqB", Facts(harness, "_reqB", harness.Time.GetUtcNow())));

        // 許可は 1 つの枠（セッションごと）。A の戻りで許可が増えることはなく、使えるのは合わせて 1 回
        Assert.Equal(
            SamlReauthOutcome.Granted,
            await harness.Service.CompleteAsync("_reqA", Facts(harness, "_reqA", harness.Time.GetUtcNow())));
        Assert.True(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
        Assert.False(await harness.Service.TryConsumeGrantAsync(harness.User.Id, Session));
    }

    // ---- 使い捨ての置き場 -------------------------------------------------------------

    [Fact]
    public async Task 置き場は取り出すと消え_期限を過ぎた値は取り出せない()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-18T00:00:00Z"));
        var store = new MemoryAdminReauthStateStore(time);

        await store.PutAsync("k", "v", TimeSpan.FromMinutes(1));
        Assert.Equal("v", await store.TakeAsync("k"));
        Assert.Null(await store.TakeAsync("k"));

        await store.PutAsync("k", "v", TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(1));
        Assert.Null(await store.TakeAsync("k"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PutAsync("k", "v", TimeSpan.Zero));
    }
}
