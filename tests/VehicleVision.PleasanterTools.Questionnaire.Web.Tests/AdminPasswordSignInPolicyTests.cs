using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>SAML の状態と合言葉ログインの組み合わせを確かめる。</summary>
public class AdminPasswordSignInPolicyTests
{
    [Fact]
    public void 既定ではSAMLが有効でも合言葉を許可する()
    {
        var policy = Policy(enabled: true);

        Assert.True(policy.IsAllowed(samlEnabled: true));
    }

    [Fact]
    public void 停止指定とSAML有効が揃ったときだけ合言葉を拒否する()
    {
        var policy = Policy(enabled: false);

        Assert.False(policy.IsAllowed(samlEnabled: true));
    }

    [Fact]
    public void Samlが無効なら停止指定を無視して締め出しを防ぐ()
    {
        var policy = Policy(enabled: false);

        Assert.True(policy.IsAllowed(samlEnabled: false));
    }

    private static AdminPasswordSignInPolicy Policy(bool enabled) =>
        new(
            new AdminPasswordSignInOptions(enabled, RescueToken: null),
            NullLogger<AdminPasswordSignInPolicy>.Instance);
}
