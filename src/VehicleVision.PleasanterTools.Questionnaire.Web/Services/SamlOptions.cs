using System.Collections.Immutable;
using System.IdentityModel.Selectors;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using CoreOptions = VehicleVision.PleasanterTools.Core.Web.SamlOptions;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Core.Web の設定を、既存の設定キー・管理者の役割・ITfoxtec へ接続する。</summary>
public sealed class SamlOptions
{
    public const string EnabledKey = "QUESTIONNAIRE_SAML_ENABLED";
    public const string EntityIdKey = "QUESTIONNAIRE_SAML_ENTITYID";
    public const string IdpEntityIdKey = "QUESTIONNAIRE_SAML_IDPENTITYID";
    public const string SingleSignOnUrlKey = "QUESTIONNAIRE_SAML_SINGLESIGNONURL";
    public const string IdpCertificateKey = "QUESTIONNAIRE_SAML_IDPCERTIFICATE";
    public const string UnknownUserKey = "QUESTIONNAIRE_SAML_UNKNOWNUSER";
    public const string RegisterRoleKey = "QUESTIONNAIRE_SAML_REGISTERROLE";
    public const string LoginIdSourceKey = "QUESTIONNAIRE_SAML_LOGINIDSOURCE";
    public const string LoginIdClaimKey = "QUESTIONNAIRE_SAML_LOGINIDCLAIM";
    public const string ButtonLabelKey = "QUESTIONNAIRE_SAML_BUTTONLABEL";
    public const string SingleLogoutUrlKey = "QUESTIONNAIRE_SAML_SINGLELOGOUTURL";

    private static readonly SamlSettingKeys Keys = new("QUESTIONNAIRE_");

    public bool Enabled { get; init; }
    public string EntityId { get; init; } = string.Empty;
    public string IdpEntityId { get; init; } = string.Empty;
    public Uri? SingleSignOnUrl { get; init; }
    public ImmutableArray<X509Certificate2> IdpCertificates { get; init; } = [];
    public Uri? SingleLogoutUrl { get; init; }
    public bool SingleLogoutEnabled => Enabled && SingleLogoutUrl is not null;
    public SamlUnknownUserPolicy UnknownUser { get; init; }
    public AdminRole RegisterRole { get; init; } = AdminRole.Editor;
    public SamlLoginIdSource LoginIdSource { get; init; }
    public string LoginIdClaim { get; init; } = string.Empty;
    public string ButtonLabel { get; init; } = string.Empty;

    public static SamlOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FromValues(key => configuration[key]);
    }

    public static SamlOptions FromValues(Func<string, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);
        var options = AuthenticationOptionsAdapter.Read(() => CoreOptions.FromValues(
            key => key == RegisterRoleKey
                ? AuthenticationOptionsAdapter.ReadRegisterRole(valueOf(key), key, rejectNumeric: false)
                : valueOf(key),
            Keys, AuthenticationOptionsAdapter.RegisterRoles, AuthenticationOptionsAdapter.DefaultRegisterRole));

        return new()
        {
            Enabled = options.Enabled,
            EntityId = options.EntityId,
            IdpEntityId = options.IdpEntityId,
            SingleSignOnUrl = options.SingleSignOnUrl,
            SingleLogoutUrl = options.SingleLogoutUrl,
            IdpCertificates = options.IdpCertificates,
            UnknownUser = options.UnknownUser,
            RegisterRole = Enum.Parse<AdminRole>(options.RegisterRole),
            LoginIdSource = options.LoginIdSource,
            LoginIdClaim = options.LoginIdClaim,
            ButtonLabel = options.ButtonLabel,
        };
    }

    /// <summary>照合に使う SAML の設定を作る。</summary>
    /// <remarks>
    /// 信頼の起点は設定で与えた証明書そのもの。自己署名の IdP 証明書も許可するが、
    /// 発行者・宛先・署名証明書と署名アルゴリズムは固定する。
    /// </remarks>
    public Saml2Configuration ToSaml2Configuration()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("SAML は無効です。");
        }

        var configuration = new Saml2Configuration
        {
            Issuer = EntityId,
            SingleSignOnDestination = SingleSignOnUrl,
            SingleLogoutDestination = SingleLogoutUrl,
            AllowedIssuer = IdpEntityId,
            AudienceRestricted = true,
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        configuration.AllowedAudienceUris.Add(EntityId);
        configuration.SignatureValidationCertificates.AddRange(IdpCertificates);
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha256Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha384Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha512Signature);

        return configuration;
    }
}
