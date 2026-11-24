using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理画面のログイン方式の設定の優先順位と保存時の検査（Issue #1056）。</summary>
public class AdminSignInSettingsProviderTests
{
    private static readonly string Token = new('x', 32);

    private static AdminSignInSettingsProvider Create(
        FakeAdminSignInSettingStore store,
        bool? external = null,
        string? rescueToken = null) =>
        new(new AdminPasswordSignInOptions(external, rescueToken), store);

    [Fact]
    public async Task 何も指定が無ければ隠さない()
    {
        var snapshot = await Create(new FakeAdminSignInSettingStore()).GetAsync();

        Assert.True(snapshot.PasswordSignInEnabled);
        Assert.False(snapshot.Fixed);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData(" FALSE ", false)]
    [InlineData("true", true)]
    [InlineData("読めない値", true)]
    public async Task DBの値を使い読めない値は隠さない側へ倒す(string raw, bool expected)
    {
        var snapshot = await Create(new FakeAdminSignInSettingStore(raw)).GetAsync();

        Assert.Equal(expected, snapshot.PasswordSignInEnabled);
        Assert.False(snapshot.Fixed);
    }

    [Fact]
    public async Task 外部設定がDBより優先され固定になりDBは読まない()
    {
        var store = new FakeAdminSignInSettingStore("true");

        var snapshot = await Create(store, external: false).GetAsync();

        Assert.False(snapshot.PasswordSignInEnabled);
        Assert.True(snapshot.Fixed);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task 救済トークンの設定状況を返す()
    {
        Assert.False((await Create(new FakeAdminSignInSettingStore()).GetAsync()).RescueTokenConfigured);
        Assert.True(
            (await Create(new FakeAdminSignInSettingStore(), rescueToken: Token).GetAsync()).RescueTokenConfigured);
    }

    [Fact]
    public async Task SSOが有効で救済トークンがあれば隠せる()
    {
        var store = new FakeAdminSignInSettingStore();
        var provider = Create(store, rescueToken: Token);

        var outcome = await provider.SaveAsync(passwordSignInEnabled: false, ssoEnabled: true);

        Assert.Equal(AdminSignInSaveOutcome.Saved, outcome);
        Assert.Equal("false", store.Values.PasswordSignIn);
        Assert.False((await provider.GetAsync()).PasswordSignInEnabled);
    }

    [Fact]
    public async Task SSOが無効なら隠せない()
    {
        var store = new FakeAdminSignInSettingStore();

        var outcome = await Create(store, rescueToken: Token).SaveAsync(false, ssoEnabled: false);

        Assert.Equal(AdminSignInSaveOutcome.SsoDisabled, outcome);
        Assert.Null(store.Values.PasswordSignIn);
    }

    [Fact]
    public async Task 救済トークンが無ければ隠せない()
    {
        var store = new FakeAdminSignInSettingStore();

        var outcome = await Create(store).SaveAsync(false, ssoEnabled: true);

        Assert.Equal(AdminSignInSaveOutcome.RescueTokenMissing, outcome);
        Assert.Null(store.Values.PasswordSignIn);
    }

    [Fact]
    public async Task 表示へ戻すのはSSOや救済トークンが無くてもできる()
    {
        var store = new FakeAdminSignInSettingStore("false");

        var outcome = await Create(store).SaveAsync(true, ssoEnabled: false);

        Assert.Equal(AdminSignInSaveOutcome.Saved, outcome);
        Assert.Equal("true", store.Values.PasswordSignIn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 外部設定で固定されていれば保存を断りDBを変えない(bool requested)
    {
        var store = new FakeAdminSignInSettingStore("true");

        var outcome = await Create(store, external: true, rescueToken: Token).SaveAsync(requested, ssoEnabled: true);

        Assert.Equal(AdminSignInSaveOutcome.Fixed, outcome);
        Assert.Equal("true", store.Values.PasswordSignIn);
    }

    [Fact]
    public async Task 画面で隠した設定は方針に反映されSSOが無効になれば無視される()
    {
        // **画面の保存値 → 方針まで通しで確かめる。** 保存値だけ見ても、入口が実際に閉じるかは分からない
        var store = new FakeAdminSignInSettingStore("false");
        var policy = new AdminPasswordSignInPolicy(
            new AdminPasswordSignInOptions(null, Token),
            Create(store, rescueToken: Token),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<AdminPasswordSignInPolicy>.Instance);

        Assert.False(await policy.IsAllowedAsync(new DefaultHttpContext(), ssoEnabled: true));
        Assert.True(await policy.IsAllowedAsync(new DefaultHttpContext(), ssoEnabled: false));
    }
}
