using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
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

        Assert.True(policy.IsAllowed(new DefaultHttpContext(), samlEnabled: true));
    }

    [Fact]
    public void 停止指定とSAML有効が揃ったときだけ合言葉を拒否する()
    {
        var policy = Policy(enabled: false);

        Assert.False(policy.IsAllowed(new DefaultHttpContext(), samlEnabled: true));
    }

    [Fact]
    public void Samlが無効なら停止指定を無視して締め出しを防ぐ()
    {
        var policy = Policy(enabled: false);

        Assert.True(policy.IsAllowed(new DefaultHttpContext(), samlEnabled: false));
    }

    [Fact]
    public void 正しい救済トークンだけが合言葉の入口を開く()
    {
        var policy = Policy(enabled: false, rescueToken: new string('x', 32));
        var rescue = new DefaultHttpContext();

        Assert.True(policy.TryGrantRescue(rescue, new string('x', 32)));

        var setCookie = rescue.Response.Headers.SetCookie.ToString();
        Assert.NotEmpty(setCookie);
        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = setCookie[..setCookie.IndexOf(';')];
        Assert.True(policy.IsAllowed(request, samlEnabled: true));
    }

    [Fact]
    public void 違う救済トークンでは合言葉の入口を開かない()
    {
        var policy = Policy(enabled: false, rescueToken: new string('x', 32));

        Assert.False(policy.TryGrantRescue(new DefaultHttpContext(), new string('y', 32)));
    }

    [Fact]
    public void 救済トークンが未設定なら逃げ道は存在しない()
    {
        var policy = Policy(enabled: false);

        Assert.False(policy.TryGrantRescue(new DefaultHttpContext(), new string('x', 32)));
    }

    private static AdminPasswordSignInPolicy Policy(
        bool enabled,
        string? rescueToken = null) =>
        new(
            new AdminPasswordSignInOptions(enabled, rescueToken),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<AdminPasswordSignInPolicy>.Instance);
}
