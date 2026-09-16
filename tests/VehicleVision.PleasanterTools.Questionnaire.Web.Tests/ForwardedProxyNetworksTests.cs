namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class ForwardedProxyNetworksTests
{
    [Fact]
    public void カンマ区切りのCIDRを読み取る()
    {
        var networks = ForwardedProxyNetworks.Parse("10.244.0.0/16, 192.0.2.10/32");

        Assert.Equal(2, networks.Count);
        Assert.Equal("10.244.0.0/16", networks[0].ToString());
        Assert.Equal("192.0.2.10/32", networks[1].ToString());
    }

    [Fact]
    public void 空なら追加の信頼範囲を作らない() =>
        Assert.Empty(ForwardedProxyNetworks.Parse(null));

    [Fact]
    public void CIDRでなければ起動を止める()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ForwardedProxyNetworks.Parse("10.244.0.1"));

        Assert.Contains(ForwardedProxyNetworks.Setting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 呼び出し元の設定名をエラーへ出す()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ForwardedProxyNetworks.Parse("not-cidr", "OTHER_SETTING"));

        Assert.Contains("OTHER_SETTING", exception.Message, StringComparison.Ordinal);
    }
}
