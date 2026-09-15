using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>SAML の設定の読み込み（Issue #166）。</summary>
/// <remarks>
/// **既定で無効なこと**と、**足りない設定を黙って無効へ落とさないこと**が肝。
/// 後者を許すと「有効にしたつもりが効いていない」に気付けない。
/// </remarks>
public class SamlOptionsTests
{
    /// <summary>検証用の自己署名証明書。**IdP の証明書の代わり。**</summary>
    private static string Pem()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test-idp", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }

    private static string Base64Der()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test-idp-2", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.RawData);
    }

    private static SamlOptions FromPairs(params (string Key, string? Value)[] pairs) =>
        SamlOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
                .Build());

    private static (string Key, string? Value)[] Minimum(params (string Key, string? Value)[] extra) =>
    [
        (SamlOptions.EnabledKey, "true"),
        (SamlOptions.EntityIdKey, "https://questionnaire.example.jp"),
        (SamlOptions.IdpEntityIdKey, "https://idp.example.com/entity"),
        (SamlOptions.SingleSignOnUrlKey, "https://idp.example.com/sso"),
        (SamlOptions.IdpCertificateKey, Pem()),
        .. extra,
    ];

    [Fact]
    public void 既定は無効()
    {
        var options = FromPairs();

        Assert.False(options.Enabled);
        Assert.Empty(options.IdpCertificates);
        Assert.Equal(SamlUnknownUserPolicy.Reject, options.UnknownUser);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData("1")]
    public void 有効にする値以外は無効のまま(string? enabled)
    {
        // **曖昧な値で有効にしない。** `true` だけを有効と見る
        var options = FromPairs((SamlOptions.EnabledKey, enabled));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 揃っていれば読める()
    {
        var options = FromPairs(Minimum());

        Assert.True(options.Enabled);
        Assert.Equal("https://questionnaire.example.jp", options.EntityId);
        Assert.Equal("https://idp.example.com/entity", options.IdpEntityId);
        Assert.Equal(new Uri("https://idp.example.com/sso"), options.SingleSignOnUrl);
        Assert.Equal("CN=test-idp", Assert.Single(options.IdpCertificates).Subject);
        Assert.Equal(SamlLoginIdSource.NameId, options.LoginIdSource);
        Assert.Equal(AdminRole.Editor, options.RegisterRole);
    }

    [Theory]
    [InlineData(SamlOptions.EntityIdKey)]
    [InlineData(SamlOptions.IdpEntityIdKey)]
    [InlineData(SamlOptions.SingleSignOnUrlKey)]
    [InlineData(SamlOptions.IdpCertificateKey)]
    public void 足りない設定は起動時に分かる(string missing)
    {
        // **黙って無効へ落とさない。** 効いていないことに後から気付くより、起動しない方が良い
        var pairs = Minimum().Where(pair => pair.Key != missing).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() => FromPairs(pairs));
        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://idp.example.com/sso")]
    [InlineData("/sso")]
    [InlineData("idp.example.com")]
    public void 窓口はhttpの絶対URLだけ(string url)
    {
        var pairs = Minimum().Select(pair =>
            pair.Key == SamlOptions.SingleSignOnUrlKey ? (pair.Key, (string?)url) : pair).ToArray();

        Assert.Throws<InvalidOperationException>(() => FromPairs(pairs));
    }

    [Fact]
    public void 証明書はbase64でも読める()
    {
        var pairs = Minimum().Select(pair =>
            pair.Key == SamlOptions.IdpCertificateKey
                ? (pair.Key, (string?)Base64Der())
                : pair).ToArray();

        var options = FromPairs(pairs);

        Assert.Equal("CN=test-idp-2", Assert.Single(options.IdpCertificates).Subject);
    }

    [Fact]
    public void 証明書は入れ替えのために二枚並べられる()
    {
        // **1 枚ずつ入れ替えると、切り替えの瞬間に誰も入れなくなる**
        var pairs = Minimum().Select(pair =>
            pair.Key == SamlOptions.IdpCertificateKey
                ? (pair.Key, (string?)(Pem() + "\n" + Pem()))
                : pair).ToArray();

        var options = FromPairs(pairs);

        Assert.Equal(2, options.IdpCertificates.Length);
    }

    [Fact]
    public void 読めない証明書は起動時に分かる()
    {
        var pairs = Minimum().Select(pair =>
            pair.Key == SamlOptions.IdpCertificateKey
                ? (pair.Key, (string?)"これは証明書ではない")
                : pair).ToArray();

        Assert.Throws<InvalidOperationException>(() => FromPairs(pairs));
    }

    [Theory]
    [InlineData("Register", SamlUnknownUserPolicy.Register)]
    [InlineData("reject", SamlUnknownUserPolicy.Reject)]
    [InlineData("REGISTER", SamlUnknownUserPolicy.Register)]
    public void 未登録の扱いは大文字小文字を問わない(string raw, SamlUnknownUserPolicy expected)
    {
        var options = FromPairs(Minimum((SamlOptions.UnknownUserKey, raw)));

        Assert.Equal(expected, options.UnknownUser);
    }

    [Fact]
    public void 知らない値は起動時に分かる()
    {
        // ⚠️ **既定へ落とさない。** 「Register のつもりが Reject だった」を黙って起こさない
        var exception = Assert.Throws<InvalidOperationException>(
            () => FromPairs(Minimum((SamlOptions.UnknownUserKey, "JitRegister"))));

        Assert.Contains(SamlOptions.UnknownUserKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 属性から取るなら属性名が要る()
    {
        Assert.Throws<InvalidOperationException>(
            () => FromPairs(Minimum((SamlOptions.LoginIdSourceKey, "Claim"))));

        var options = FromPairs(Minimum(
            (SamlOptions.LoginIdSourceKey, "Claim"),
            (SamlOptions.LoginIdClaimKey, "login_id")));

        Assert.Equal(SamlLoginIdSource.Claim, options.LoginIdSource);
        Assert.Equal("login_id", options.LoginIdClaim);
    }

    [Fact]
    public void 照合の設定は発行者と宛先を固定する()
    {
        var options = FromPairs(Minimum());

        var configuration = options.ToSaml2Configuration();

        Assert.Equal("https://questionnaire.example.jp", configuration.Issuer);
        Assert.Equal("https://idp.example.com/entity", configuration.AllowedIssuer);
        Assert.True(configuration.AudienceRestricted);
        Assert.Equal("https://questionnaire.example.jp", Assert.Single(configuration.AllowedAudienceUris));
        Assert.Single(configuration.SignatureValidationCertificates);

        // **SHA-1 は受け取らない**
        Assert.DoesNotContain(
            configuration.SignatureValidationAlgorithms,
            algorithm => algorithm.Contains("sha1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 無効なら照合の設定は作らせない()
    {
        var options = new SamlOptions();

        Assert.Throws<InvalidOperationException>(() => options.ToSaml2Configuration());
    }
}
