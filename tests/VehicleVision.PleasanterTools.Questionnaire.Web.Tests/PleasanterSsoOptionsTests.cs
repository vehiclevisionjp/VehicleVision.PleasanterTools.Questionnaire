using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>Pleasanter のログインで入る設定の読み取り（Issue #464）。</summary>
public class PleasanterSsoOptionsTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1,a")]
    [InlineData("1 2")]
    [InlineData(",,,")]
    [InlineData("2147483648")]
    public void 所属の不正なIDを黙って無制限にしない(string value)
    {
        foreach (var key in new[] { PleasanterSsoOptions.AllowedDeptIdsKey, PleasanterSsoOptions.AllowedGroupIdsKey })
        {
            Assert.Throws<InvalidOperationException>(() => PleasanterSsoOptions.FromValues(k => k == key ? value : null));
        }
    }

    [Fact]
    public void 所属のIDはカンマか改行で読み重複は取り除く()
    {
        var options = PleasanterSsoOptions.FromValues(k => k == PleasanterSsoOptions.AllowedDeptIdsKey ? " 10,20\n10 " : null);
        Assert.Equal([10, 20], options.AllowedDeptIds.ToArray());
        Assert.True(options.HasMembershipRestriction);
        Assert.False(PleasanterSsoOptions.FromValues(_ => null).HasMembershipRestriction);
        Assert.Throws<InvalidOperationException>(() => PleasanterSsoOptions.FromValues(k =>
            k == PleasanterSsoOptions.AllowedGroupIdsKey ? string.Join(',', Enumerable.Range(1, 65)) : null));
    }

    private static PleasanterSsoOptions Read(Dictionary<string, string?> values) =>
        PleasanterSsoOptions.FromValues(key => values.GetValueOrDefault(key));

    private static Dictionary<string, string?> Enabled() => new()
    {
        [PleasanterSsoOptions.EnabledKey] = "true",
        [PleasanterSsoOptions.InternalBaseUrlKey] = "http://pleasanter:8080",
        [PleasanterSsoOptions.LoginUrlKey] = "/users/login",
    };

    [Fact]
    public void 何も書かなければ無効で既定値になる()
    {
        var options = Read([]);

        Assert.False(options.Enabled);
        Assert.Equal([".AspNetCore.Cookies", "Pleasanter_SessionGuid"], options.CookieNamePrefixes.ToArray());
        Assert.Equal(PleasanterSsoUnknownUserPolicy.Reject, options.UnknownUser);
        Assert.Equal(AdminRole.Editor, options.RegisterRole);
        Assert.Equal(TimeSpan.FromMinutes(5), options.RevalidateInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Timeout);
    }

    [Fact]
    public void 有効にすると内部URLの末尾をそろえる()
    {
        var options = Read(Enabled());

        Assert.True(options.Enabled);
        Assert.Equal(new Uri("http://pleasanter:8080/"), options.InternalBaseUrl);
    }

    [Theory]
    [InlineData(PleasanterSsoOptions.InternalBaseUrlKey)]
    [InlineData(PleasanterSsoOptions.LoginUrlKey)]
    public void 有効なのに必須の値が無ければ例外(string missing)
    {
        var values = Enabled();
        values.Remove(missing);

        var exception = Assert.Throws<InvalidOperationException>(() => Read(values));
        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PleasanterSsoOptions.InternalBaseUrlKey, "ftp://pleasanter")]
    [InlineData(PleasanterSsoOptions.InternalBaseUrlKey, "pleasanter:8080")]
    [InlineData(PleasanterSsoOptions.InternalBaseUrlKey, "http://user:pass@pleasanter/")]
    [InlineData(PleasanterSsoOptions.InternalBaseUrlKey, "http://pleasanter/?x=1")]
    [InlineData(PleasanterSsoOptions.LoginUrlKey, "javascript:alert(1)")]
    [InlineData(PleasanterSsoOptions.LoginUrlKey, "//evil.example.com/users/login")]
    [InlineData(PleasanterSsoOptions.LoginUrlKey, "/\\evil.example.com")]
    [InlineData(PleasanterSsoOptions.LogoutUrlKey, "javascript:alert(1)")]
    [InlineData(PleasanterSsoOptions.EnabledKey, "yes")]
    [InlineData(PleasanterSsoOptions.UnknownUserKey, "Allow")]
    [InlineData(PleasanterSsoOptions.UnknownUserKey, "1")]
    [InlineData(PleasanterSsoOptions.RegisterRoleKey, "Root")]
    [InlineData(PleasanterSsoOptions.RevalidateMinutesKey, "0")]
    [InlineData(PleasanterSsoOptions.RevalidateMinutesKey, "61")]
    [InlineData(PleasanterSsoOptions.RevalidateMinutesKey, "five")]
    [InlineData(PleasanterSsoOptions.TimeoutSecondsKey, "31")]
    public void 読めない値は黙って既定へ落とさない(string key, string value)
    {
        var values = Enabled();
        values[key] = value;

        Assert.Throws<InvalidOperationException>(() => Read(values));
    }

    [Theory]
    [InlineData("https://www.example.jp/users/login")]
    [InlineData("/users/login?ReturnUrl=%2F")]
    public void ログイン画面は同じホストのパスか絶対URL(string loginUrl)
    {
        var values = Enabled();
        values[PleasanterSsoOptions.LoginUrlKey] = loginUrl;

        Assert.Equal(loginUrl, Read(values).LoginUrl);
    }

    [Theory]
    [InlineData("q")]
    [InlineData("q.")]
    [InlineData("q.admin")]
    [InlineData("Q.ADMIN")]
    [InlineData(".AspNetCore.Cookies,q.admin")]
    public void 本アプリのcookieと重なる指定は拒否する(string cookieNames)
    {
        var values = Enabled();
        values[PleasanterSsoOptions.CookieNamesKey] = cookieNames;

        Assert.Throws<InvalidOperationException>(() => Read(values));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a=b")]
    public void cookieの名前に使えない文字は拒否する(string cookieNames)
    {
        var values = Enabled();
        values[PleasanterSsoOptions.CookieNamesKey] = cookieNames;

        Assert.Throws<InvalidOperationException>(() => Read(values));
    }

    [Fact]
    public void 転送するcookieは前方一致で本アプリのものは除く()
    {
        var options = Read(Enabled());

        Assert.True(options.ShouldForwardCookie(".AspNetCore.Cookies"));
        Assert.True(options.ShouldForwardCookie(".AspNetCore.CookiesC1"));
        Assert.True(options.ShouldForwardCookie(".AspNetCore.CookiesC12"));
        Assert.True(options.ShouldForwardCookie("Pleasanter_SessionGuid"));
        Assert.False(options.ShouldForwardCookie("q.admin"));
        Assert.False(options.ShouldForwardCookie("q.admin.pending"));
        Assert.False(options.ShouldForwardCookie("q.asset"));
        Assert.False(options.ShouldForwardCookie(".AspNetCore.Antiforgery.x"));
        Assert.False(options.ShouldForwardCookie(string.Empty));
    }

    [Fact]
    public void 数値と列挙を読む()
    {
        var values = Enabled();
        values[PleasanterSsoOptions.UnknownUserKey] = "register";
        values[PleasanterSsoOptions.RegisterRoleKey] = "SurveyAdministrator";
        values[PleasanterSsoOptions.RevalidateMinutesKey] = "15";
        values[PleasanterSsoOptions.TimeoutSecondsKey] = "3";
        values[PleasanterSsoOptions.CookieNamesKey] = ".AspNetCore.Cookies, MyCookie";

        var options = Read(values);

        Assert.Equal(PleasanterSsoUnknownUserPolicy.Register, options.UnknownUser);
        Assert.Equal(AdminRole.SurveyAdministrator, options.RegisterRole);
        Assert.Equal(TimeSpan.FromMinutes(15), options.RevalidateInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Timeout);
        Assert.Equal([".AspNetCore.Cookies", "MyCookie"], options.CookieNamePrefixes.ToArray());
    }
}

/// <summary>Pleasanter のログインで入る設定の優先順位と保存（Issue #464）。</summary>
public class PleasanterSsoOptionsProviderTests
{
    private sealed class FakeStore(PleasanterSsoSettingValues values) : IPleasanterSsoSettingStore
    {
        public PleasanterSsoSettingValues Values { get; private set; } = values;

        public int Reads { get; private set; }

        public Task<PleasanterSsoSettingValues> GetAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Values);
        }

        public Task SaveAsync(PleasanterSsoSettingValues values, CancellationToken cancellationToken = default)
        {
            Values = values;
            return Task.CompletedTask;
        }
    }

    private static PleasanterSsoOptionsProvider Create(
        Dictionary<string, string?> external,
        FakeStore store,
        FakeTimeProvider? time = null) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(external).Build(),
            store,
            time ?? new FakeTimeProvider(),
            NullLogger<PleasanterSsoOptionsProvider>.Instance);

    [Fact]
    public async Task 外部設定がDBより優先され固定項目になる()
    {
        var store = new FakeStore(new PleasanterSsoSettingValues
        {
            Enabled = "true",
            InternalBaseUrl = "http://db-pleasanter/",
            LoginUrl = "/users/login",
            ButtonLabel = "DB の表示",
        });
        var provider = Create(
            new() { [PleasanterSsoOptions.InternalBaseUrlKey] = "http://env-pleasanter:8080/" },
            store);

        var snapshot = await provider.GetAsync();

        Assert.True(snapshot.Options.Enabled);
        Assert.Equal(new Uri("http://env-pleasanter:8080/"), snapshot.Options.InternalBaseUrl);
        Assert.Equal("DB の表示", snapshot.Options.ButtonLabel);
        Assert.Contains(PleasanterSsoOptions.InternalBaseUrlKey, snapshot.FixedKeys);
        Assert.DoesNotContain(PleasanterSsoOptions.ButtonLabelKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 固定項目は画面から保存してもDBの退避値を変えない()
    {
        var store = new FakeStore(new PleasanterSsoSettingValues { InternalBaseUrl = "http://kept/" });
        var provider = Create(
            new() { [PleasanterSsoOptions.InternalBaseUrlKey] = "http://env/" },
            store);

        await provider.SaveAsync(new PleasanterSsoSettingValues
        {
            Enabled = "true",
            InternalBaseUrl = "http://from-screen/",
            LoginUrl = "/users/login",
        });

        Assert.Equal("http://kept/", store.Values.InternalBaseUrl);
        Assert.Equal("true", store.Values.Enabled);
    }

    [Fact]
    public async Task 所属の設定も外部優先で保存とプレビューに反映する()
    {
        var store = new FakeStore(new PleasanterSsoSettingValues { AllowedDeptIds = "10", AllowedGroupIds = "20" });
        var provider = Create(new() { [PleasanterSsoOptions.AllowedDeptIdsKey] = "30" }, store);
        var request = new PleasanterSsoSettingValues { AllowedDeptIds = "40", AllowedGroupIds = "50" };
        var preview = await provider.PreviewAsync(request, forceEnabled: false);
        Assert.Equal([30], preview.Options.AllowedDeptIds.ToArray());
        Assert.Equal([50], preview.Options.AllowedGroupIds.ToArray());
        Assert.Equal("20", store.Values.AllowedGroupIds);
        var saved = await provider.SaveAsync(request);
        Assert.Contains(PleasanterSsoOptions.AllowedDeptIdsKey, saved.FixedKeys);
        Assert.Equal("10", store.Values.AllowedDeptIds);
        Assert.Equal("50", store.Values.AllowedGroupIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SaveAsync(request with { AllowedGroupIds = "broken" }));
        Assert.Equal("50", store.Values.AllowedGroupIds);
    }

    [Fact]
    public async Task 壊れた設定は保存しない()
    {
        var store = new FakeStore(new PleasanterSsoSettingValues());
        var provider = Create([], store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SaveAsync(
            new PleasanterSsoSettingValues { Enabled = "true", LoginUrl = "/users/login" }));

        Assert.Null(store.Values.Enabled);
    }

    [Fact]
    public async Task 読めないDBの値は無効として扱い画面ごと落とさない()
    {
        var store = new FakeStore(new PleasanterSsoSettingValues { Enabled = "true", RevalidateMinutes = "x" });
        var provider = Create([], store);

        var snapshot = await provider.GetAsync();

        Assert.False(snapshot.Options.Enabled);
    }

    [Fact]
    public async Task 読んだ値は30秒だけ覚え保存すると入れ替える()
    {
        var time = new FakeTimeProvider();
        var store = new FakeStore(new PleasanterSsoSettingValues());
        var provider = Create([], store, time);

        await provider.GetAsync();
        await provider.GetAsync();
        Assert.Equal(1, store.Reads);

        await provider.SaveAsync(new PleasanterSsoSettingValues
        {
            Enabled = "true",
            InternalBaseUrl = "http://pleasanter/",
            LoginUrl = "/users/login",
        });
        Assert.True((await provider.GetAsync()).Options.Enabled);

        time.Advance(TimeSpan.FromSeconds(31));
        await provider.GetAsync();
        Assert.True(store.Reads >= 3);
    }

    [Fact]
    public async Task 試験では無効のままでも有効として組み立てる()
    {
        var store = new FakeStore(new PleasanterSsoSettingValues());
        var provider = Create([], store);

        var snapshot = await provider.PreviewAsync(
            new PleasanterSsoSettingValues
            {
                Enabled = "false",
                InternalBaseUrl = "http://pleasanter/",
                LoginUrl = "/users/login",
            },
            forceEnabled: true);

        Assert.True(snapshot.Options.Enabled);
        Assert.Null(store.Values.InternalBaseUrl);
    }
}
