using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class AuthenticationOptionsAdapterTests
{
    [Fact]
    public void 設定キーの定数はCoreへ渡す接頭辞と一致する()
    {
        AssertKeys(typeof(PleasanterSsoOptions), new PleasanterSsoSettingKeys("QUESTIONNAIRE_"));
        AssertKeys(typeof(SamlOptions), new SamlSettingKeys("QUESTIONNAIRE_"));
    }

    private static void AssertKeys(Type adapter, object keys)
    {
        var fields = adapter.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name.EndsWith("Key", StringComparison.Ordinal)).ToArray();
        Assert.Equal(keys.GetType().GetProperties().Length, fields.Length);
        foreach (var field in fields)
        {
            var property = keys.GetType().GetProperty(field.Name);
            Assert.NotNull(property);
            Assert.Equal(field.GetRawConstantValue(), property.GetValue(keys));
        }
    }

    public static TheoryData<string, AdminRole> NamedRoles => new()
    {
        { "editor", AdminRole.Editor },
        { "SurveyAdministrator", AdminRole.SurveyAdministrator },
        { "Administrator", AdminRole.Administrator },
        { "UserAdministrator", AdminRole.UserAdministrator },
        { "Auditor", AdminRole.Auditor },
        { "Editor, Administrator", AdminRole.Administrator },
    };

    [Theory]
    [MemberData(nameof(NamedRoles))]
    public void アプリ固有の役割名と複合表記を従来どおり受け付ける(string raw, AdminRole expected)
    {
        Assert.Equal(expected, PleasanterSsoOptions.FromValues(
            key => key == PleasanterSsoOptions.RegisterRoleKey ? raw : null).RegisterRole);
        Assert.Equal(expected, SamlOptions.FromValues(SamlValues(SamlOptions.RegisterRoleKey, raw)).RegisterRole);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("+1")]
    [InlineData("-0")]
    public void 数値の役割はSSOで拒否しSAMLで受け付ける(string raw)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PleasanterSsoOptions.FromValues(
            key => key == PleasanterSsoOptions.RegisterRoleKey ? raw : null));
        Assert.Equal(
            $"{PleasanterSsoOptions.RegisterRoleKey} に知らない値が入っています: {raw}"
            + "（使えるのは Editor / Administrator / SurveyAdministrator / UserAdministrator / Auditor）",
            exception.Message);
        Assert.Equal(Enum.Parse<AdminRole>(raw),
            SamlOptions.FromValues(SamlValues(SamlOptions.RegisterRoleKey, raw)).RegisterRole);
    }

    [Theory]
    [InlineData("Root")]
    [InlineData("5")]
    [InlineData("-1")]
    public void SAMLの不正な役割は既存の例外と文言で拒否する(string raw)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SamlOptions.FromValues(SamlValues(SamlOptions.RegisterRoleKey, raw)));
        Assert.Equal(
            $"{SamlOptions.RegisterRoleKey} に知らない値が入っています: {raw}"
            + "（使えるのは Editor / Administrator / SurveyAdministrator / UserAdministrator / Auditor）",
            exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("yes")]
    public void SAML無効時は役割を含め他の設定を読まない(string? enabled)
    {
        var options = SamlOptions.FromValues(key =>
        {
            Assert.Equal(SamlOptions.EnabledKey, key);
            return enabled;
        });
        Assert.False(options.Enabled);
        Assert.Equal(AdminRole.Editor, options.RegisterRole);
        Assert.Equal(AdminRole.Editor, new SamlOptions().RegisterRole);
        Assert.Equal(SamlLoginIdSource.NameId, options.LoginIdSource);
        Assert.Equal(SamlUnknownUserPolicy.Reject, options.UnknownUser);
    }

    public static TheoryData<string, string, string> SsoErrors => new()
    {
        { "ENABLED", "yes", "QUESTIONNAIRE_PLEASANTERSSO_ENABLED は true か false で指定してください: yes" },
        { "ENABLED", "true",
            "QUESTIONNAIRE_PLEASANTERSSO_ENABLED を有効にしたときは QUESTIONNAIRE_PLEASANTERSSO_INTERNALBASEURL が要ります。" },
        { "REVALIDATEMINUTES", "0", "QUESTIONNAIRE_PLEASANTERSSO_REVALIDATEMINUTES は 1 以上 60 以下の整数で指定してください: 0" },
        { "TIMEOUTSECONDS", "31", "QUESTIONNAIRE_PLEASANTERSSO_TIMEOUTSECONDS は 1 以上 30 以下の整数で指定してください: 31" },
        { "UNKNOWNUSER", "1", "QUESTIONNAIRE_PLEASANTERSSO_UNKNOWNUSER に知らない値が入っています: 1（使えるのは Reject / Register）" },
        { "COOKIENAMES", "a b", "QUESTIONNAIRE_PLEASANTERSSO_COOKIENAMES に cookie の名前として使えない文字があります: a b" },
        { "COOKIENAMES", "q", "QUESTIONNAIRE_PLEASANTERSSO_COOKIENAMES に本アプリの cookie（q. で始まる名前）と重なる指定があります: q" },
        { "COOKIENAMES", ",,,", "QUESTIONNAIRE_PLEASANTERSSO_COOKIENAMES に転送する cookie の名前がありません。" },
        { "INTERNALBASEURL", "/relative",
            "QUESTIONNAIRE_PLEASANTERSSO_INTERNALBASEURL は http(s) の絶対 URL で書いてください（問い合わせ文字列・資格情報は不可）: /relative" },
        { "LOGINURL", "//other",
            "QUESTIONNAIRE_PLEASANTERSSO_LOGINURL は / で始まる同じホストのパスか、http(s) の絶対 URL で書いてください: //other" },
        { "ALLOWEDDEPTIDS", new string('1', 1025),
            "QUESTIONNAIRE_PLEASANTERSSO_ALLOWEDDEPTIDS は正の ID を 64 件以内、1024 文字以内で指定してください。" },
        { "ALLOWEDGROUPIDS", "0", "QUESTIONNAIRE_PLEASANTERSSO_ALLOWEDGROUPIDS は正の ID をカンマか改行で区切って指定してください。" },
    };

    [Theory]
    [MemberData(nameof(SsoErrors))]
    public void SSOの構造化例外を既存の型と日本語文言へ写し替える(string suffix, string raw, string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PleasanterSsoOptions.FromValues(
            key => key == "QUESTIONNAIRE_PLEASANTERSSO_" + suffix ? raw : null));
        Assert.Equal(expected, exception.Message);
        Assert.Null(exception.InnerException);
    }

    public static TheoryData<string, string?, string> SamlErrors => new()
    {
        { "ENTITYID", null, "QUESTIONNAIRE_SAML_ENABLED を有効にしたときは QUESTIONNAIRE_SAML_ENTITYID が要ります。" },
        { "IDPCERTIFICATE", null, "QUESTIONNAIRE_SAML_IDPCERTIFICATE が要ります。IdP の署名証明書（PEM か base64）を入れてください。" },
        { "SINGLESIGNONURL", "/sso", "QUESTIONNAIRE_SAML_SINGLESIGNONURL は http(s) の絶対 URL で書いてください: /sso" },
        { "SINGLELOGOUTURL", "/slo", "QUESTIONNAIRE_SAML_SINGLELOGOUTURL は http(s) の絶対 URL で書いてください: /slo" },
        { "LOGINIDSOURCE", "Claim", "QUESTIONNAIRE_SAML_LOGINIDSOURCE=Claim のときは QUESTIONNAIRE_SAML_LOGINIDCLAIM が要ります。" },
        { "UNKNOWNUSER", "bad", "QUESTIONNAIRE_SAML_UNKNOWNUSER に知らない値が入っています: bad（使えるのは Reject / Register）" },
        { "IDPCERTIFICATE", "-----BEGIN CERTIFICATE-----",
            "QUESTIONNAIRE_SAML_IDPCERTIFICATE の PEM が途中で終わっています。" },
        { "IDPCERTIFICATE", "-----BEGIN CERTIFICATE-----\nbad\n-----END CERTIFICATE-----",
            "QUESTIONNAIRE_SAML_IDPCERTIFICATE の PEM を読めませんでした。" },
        { "IDPCERTIFICATE", "bad!",
            "QUESTIONNAIRE_SAML_IDPCERTIFICATE を証明書として読めませんでした。PEM か、DER を base64 にしたものを入れてください。" },
    };

    [Theory]
    [MemberData(nameof(SamlErrors))]
    public void SAMLの構造化例外を元の証明書例外も含め写し替える(string suffix, string? raw, string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SamlOptions.FromValues(SamlValues("QUESTIONNAIRE_SAML_" + suffix, raw)));
        Assert.Equal(expected, exception.Message);
        if (suffix == "IDPCERTIFICATE" && raw is not null && !raw.EndsWith("BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            Assert.True(exception.InnerException is ArgumentException or CryptographicException or FormatException);
            Assert.IsNotType<AuthenticationSettingException>(exception.InnerException);
        }
        else
        {
            Assert.Null(exception.InnerException);
        }
    }

    private static Func<string, string?> SamlValues(string key, string? raw)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=adapter-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var values = new Dictionary<string, string?>
        {
            [SamlOptions.EnabledKey] = "true",
            [SamlOptions.EntityIdKey] = "https://questionnaire.example.test",
            [SamlOptions.IdpEntityIdKey] = "https://idp.example.test",
            [SamlOptions.SingleSignOnUrlKey] = "https://idp.example.test/sso",
            [SamlOptions.IdpCertificateKey] = certificate.ExportCertificatePem(),
            [key] = raw,
        };
        return name => values.GetValueOrDefault(name);
    }
}
