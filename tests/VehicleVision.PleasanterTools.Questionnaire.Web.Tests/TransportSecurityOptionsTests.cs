using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>HTTP 運用の宣言を厳密に読み取れることを確かめる。</summary>
public class TransportSecurityOptionsTests
{
    private static IConfiguration Configuration(string? value = null)
    {
        KeyValuePair<string, string?>[] settings = value is null
            ? []
            :
            [
                new KeyValuePair<string, string?>(
                    TransportSecurityOptions.AllowInsecureSetting,
                    value),
            ];

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    [Fact]
    public void 未設定では従来どおりHTTPSを強制する()
    {
        var options = TransportSecurityOptions.FromConfiguration(Configuration());

        Assert.False(options.AllowInsecure);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("false", false)]
    public void 真偽値を明示できる(string value, bool expected)
    {
        var options = TransportSecurityOptions.FromConfiguration(Configuration(value));

        Assert.Equal(expected, options.AllowInsecure);
    }

    [Fact]
    public void 読めない値は安全側へ見せかけず起動を止める()
    {
        Assert.Throws<InvalidOperationException>(
            () => TransportSecurityOptions.FromConfiguration(Configuration("yes")));
    }
}
