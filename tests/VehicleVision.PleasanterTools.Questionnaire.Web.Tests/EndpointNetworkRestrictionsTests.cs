using System.Net;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class EndpointNetworkRestrictionsTests
{
    [Fact]
    public void 未設定なら対象の口を絞らない()
    {
        var restrictions = Read();

        Assert.True(restrictions.IsAllowed("/healthz", IPAddress.Parse("203.0.113.1")));
        Assert.True(restrictions.IsAllowed("/ready", IPAddress.Parse("203.0.113.1")));
        Assert.True(restrictions.IsAllowed(
            "/api/monitoring/status",
            IPAddress.Parse("203.0.113.1")));
        Assert.True(restrictions.IsAllowed(
            "/openapi/v1.json",
            IPAddress.Parse("203.0.113.1")));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/ready")]
    public void 生存確認は指定したCIDRだけを許す(string path)
    {
        var restrictions = Read(
            (EndpointNetworkRestrictions.HealthSetting, "10.0.0.0/8,192.0.2.10/32"));

        Assert.True(restrictions.IsAllowed(path, IPAddress.Parse("10.1.2.3")));
        Assert.True(restrictions.IsAllowed(path, IPAddress.Parse("192.0.2.10")));
        Assert.False(restrictions.IsAllowed(path, IPAddress.Parse("192.0.2.11")));
    }

    [Fact]
    public void 監視はinheritで生存確認のCIDRを引き継ぐ()
    {
        var restrictions = Read(
            (EndpointNetworkRestrictions.HealthSetting, "10.0.0.0/8"),
            (EndpointNetworkRestrictions.MonitoringSetting, "inherit"));

        Assert.True(restrictions.IsAllowed(
            "/api/monitoring/status",
            IPAddress.Parse("10.1.2.3")));
        Assert.False(restrictions.IsAllowed(
            "/api/monitoring/status",
            IPAddress.Parse("192.0.2.10")));
    }

    [Fact]
    public void OpenAPIはinheritで生存確認のCIDRを引き継ぐ()
    {
        var restrictions = Read(
            (EndpointNetworkRestrictions.HealthSetting, "10.0.0.0/8"),
            (EndpointNetworkRestrictions.OpenApiSetting, "inherit"));

        Assert.True(restrictions.IsAllowed(
            "/openapi/v1.json",
            IPAddress.Parse("10.1.2.3")));
        Assert.False(restrictions.IsAllowed(
            "/openapi/v1.json",
            IPAddress.Parse("192.0.2.10")));
    }

    [Fact]
    public void IPv4射影のIPv6をIPv4のCIDRとして照合する()
    {
        var restrictions = Read(
            (EndpointNetworkRestrictions.HealthSetting, "10.0.0.0/8"));

        Assert.True(restrictions.IsAllowed(
            "/healthz",
            IPAddress.Parse("::ffff:10.1.2.3")));
    }

    [Fact]
    public void 対象外の経路は送信元を絞らない()
    {
        var restrictions = Read(
            (EndpointNetworkRestrictions.HealthSetting, "10.0.0.0/8"),
            (EndpointNetworkRestrictions.MonitoringSetting, "192.0.2.0/24"));

        Assert.True(restrictions.IsAllowed("/admin", IPAddress.Parse("203.0.113.1")));
    }

    [Fact]
    public void inheritの綴りを間違えたら起動を止める()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read(
            (EndpointNetworkRestrictions.MonitoringSetting, "inherits")));

        Assert.Contains(
            EndpointNetworkRestrictions.MonitoringSetting,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CIDRでない値なら起動を止める()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read(
            (EndpointNetworkRestrictions.HealthSetting, "10.0.0.1")));

        Assert.Contains(
            EndpointNetworkRestrictions.HealthSetting,
            exception.Message,
            StringComparison.Ordinal);
    }

    private static EndpointNetworkRestrictions Read(
        params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return EndpointNetworkRestrictions.FromConfiguration(configuration);
    }
}
