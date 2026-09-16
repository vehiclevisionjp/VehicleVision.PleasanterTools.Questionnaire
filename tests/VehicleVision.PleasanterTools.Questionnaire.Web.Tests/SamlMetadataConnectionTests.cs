using System.Net;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>メタデータ取得を内部ネットワークへの入口にしない。</summary>
public class SamlMetadataConnectionTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    public void 内部アドレスは接続先にしない(string raw)
    {
        Assert.False(SamlMetadataConnection.IsPublic(IPAddress.Parse(raw)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public void 公開アドレスは接続できる(string raw)
    {
        Assert.True(SamlMetadataConnection.IsPublic(IPAddress.Parse(raw)));
    }
}
