using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class OpenApiExposureOptionsTests
{
    [Fact]
    public void 未設定ならOpenAPIを公開しない()
    {
        Assert.False(Read().Enabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" false ")]
    public void 真偽値を読み取る(string value)
    {
        var options = Read((OpenApiExposureOptions.EnabledSetting, value));

        Assert.Equal(bool.Parse(value), options.Enabled);
    }

    [Fact]
    public void 真偽値でない値なら起動を止める()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Read((OpenApiExposureOptions.EnabledSetting, "enabled")));

        Assert.Contains(OpenApiExposureOptions.EnabledSetting, exception.Message, StringComparison.Ordinal);
    }

    private static OpenApiExposureOptions Read(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return OpenApiExposureOptions.FromConfiguration(configuration);
    }
}
