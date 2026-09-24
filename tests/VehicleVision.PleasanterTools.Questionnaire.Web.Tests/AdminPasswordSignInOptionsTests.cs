using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>合言葉ログインの外部設定を確かめる。</summary>
public class AdminPasswordSignInOptionsTests
{
    private static IConfiguration Configuration(
        string? enabled = null,
        string? rescueToken = null)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            settings[AdminPasswordSignInOptions.EnabledSetting] = enabled;
        }

        if (rescueToken is not null)
        {
            settings[AdminPasswordSignInOptions.RescueTokenSetting] = rescueToken;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    [Fact]
    public void 未設定なら合言葉ログインを塞がず逃げ道も作らない()
    {
        var options = AdminPasswordSignInOptions.FromConfiguration(Configuration());

        Assert.True(options.Enabled);
        Assert.Null(options.RescueToken);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("false", false)]
    public void 合言葉ログインの可否を明示できる(string value, bool expected)
    {
        var options = AdminPasswordSignInOptions.FromConfiguration(Configuration(enabled: value));

        Assert.Equal(expected, options.Enabled);
    }

    [Fact]
    public void 読めない真偽値は起動を止める()
    {
        Assert.Throws<InvalidOperationException>(
            () => AdminPasswordSignInOptions.FromConfiguration(Configuration(enabled: "yes")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の救済トークンは未設定として扱う(string value)
    {
        var options = AdminPasswordSignInOptions.FromConfiguration(
            Configuration(rescueToken: value));

        Assert.Null(options.RescueToken);
    }

    [Fact]
    public void 短い救済トークンは起動を止める()
    {
        Assert.Throws<InvalidOperationException>(
            () => AdminPasswordSignInOptions.FromConfiguration(
                Configuration(rescueToken: new string('x', 31))));
    }

    [Fact]
    public void 三十二文字以上の救済トークンを受け入れる()
    {
        var token = new string('x', 32);

        var options = AdminPasswordSignInOptions.FromConfiguration(
            Configuration(rescueToken: token));

        Assert.Equal(token, options.RescueToken);
    }
}
