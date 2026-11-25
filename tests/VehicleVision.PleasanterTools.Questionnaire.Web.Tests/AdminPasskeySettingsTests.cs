using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>パスキーの外部設定の検証を確かめる（Issue #1082 の設計 4/4）。</summary>
public class AdminPasskeySettingsTests
{
    private const string Enabled = "QUESTIONNAIRE_ADMIN_PASSKEY";
    private const string RpId = "QUESTIONNAIRE_ADMIN_PASSKEY_RPID";
    private const string Origins = "QUESTIONNAIRE_ADMIN_PASSKEY_ORIGINS";

    private static AdminPasskeyOptions Read(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return AdminPasskeySettings.FromConfiguration(configuration, "アンケート");
    }

    [Fact]
    public void 設定名は決めたとおり()
    {
        Assert.Equal(Enabled, AdminPasskeySettings.Keys.EnabledKey);
        Assert.Equal(RpId, AdminPasskeySettings.Keys.RpIdKey);
        Assert.Equal(Origins, AdminPasskeySettings.Keys.OriginsKey);
    }

    [Fact]
    public void 未設定なら無効()
    {
        var options = Read();

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("   ")]
    public void 無効のときはRPIDとOriginsが無くても検証しない(string enabled)
    {
        var options = Read((Enabled, enabled));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 無効のときはRPIDとOriginsが壊れていても止めない()
    {
        // 無効の間は、これらの設定を検証しない
        var options = Read((Enabled, "false"), (RpId, "https://bad/path"), (Origins, "not-an-origin"));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 有効の真偽値の書き間違いは無効のままにせず止める()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read((Enabled, "yes")));

        Assert.Contains(Enabled, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, new[] { RpId, Origins })]
    [InlineData("", "", new[] { RpId, Origins })]
    [InlineData("  ", "https://q.example.com", new[] { RpId })]
    [InlineData("q.example.com", null, new[] { Origins })]
    [InlineData("q.example.com", " , ", new[] { Origins })]
    public void 有効なのにRPIDかOriginsが足りなければ足りない設定名を挙げて止める(
        string? rpId, string? origins, string[] expectedMissing)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Read((Enabled, "true"), (RpId, rpId), (Origins, origins)));

        foreach (var name in new[] { RpId, Origins })
        {
            Assert.Equal(
                expectedMissing.Contains(name),
                exception.Message.Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void 有効でRPIDとOriginsがそろえば読める()
    {
        var options = Read(
            (Enabled, " TRUE "),
            (RpId, "Q.Example.com"),
            (Origins, "https://q.example.com, https://admin.q.example.com"));

        Assert.True(options.Enabled);
        Assert.Equal("q.example.com", options.RpId);
        Assert.Equal(["https://q.example.com", "https://admin.q.example.com"], options.Origins);
        Assert.Equal("アンケート", options.RpName);
    }

    [Theory]
    [InlineData("https://q.example.com/path", "https://q.example.com")]
    [InlineData("q.example.com:8443", "https://q.example.com")]
    [InlineData("q.example.com", "http://q.example.com")]
    [InlineData("q.example.com", "https://other.example.org")]
    public void RPIDとOriginsの書き間違いや食い違いは止める(string rpId, string origins)
    {
        Assert.Throws<InvalidOperationException>(
            () => Read((Enabled, "true"), (RpId, rpId), (Origins, origins)));
    }
}
