using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理画面用 proof-of-work の設定を確かめる。</summary>
public class AdminCaptchaOptionsTests
{
    private static IConfiguration Configuration(string? value = null)
    {
        KeyValuePair<string, string?>[] settings = value is null
            ? []
            :
            [
                new KeyValuePair<string, string?>(
                    AdminCaptchaOptions.EnabledSetting,
                    value),
            ];

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    [Fact]
    public void 未設定では無効である()
    {
        var options = AdminCaptchaOptions.FromConfiguration(Configuration());

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("false", false)]
    public void 真偽値を明示できる(string value, bool expected)
    {
        var options = AdminCaptchaOptions.FromConfiguration(Configuration(value));

        Assert.Equal(expected, options.Enabled);
    }

    [Fact]
    public void 読めない値は起動を止める()
    {
        Assert.Throws<InvalidOperationException>(
            () => AdminCaptchaOptions.FromConfiguration(Configuration("yes")));
    }
}
