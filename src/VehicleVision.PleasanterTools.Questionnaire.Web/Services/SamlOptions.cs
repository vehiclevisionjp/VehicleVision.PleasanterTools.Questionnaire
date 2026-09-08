using System.Collections.Immutable;
using System.IdentityModel.Selectors;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>本アプリに居ない利用者が IdP から来たときの扱い。</summary>
public enum SamlUnknownUserPolicy
{
    /// <summary>通さない。**既定。** 管理者は本アプリ側で先に作っておく。</summary>
    Reject = 0,

    /// <summary>その場で作って通す（JIT）。**役割は設定で決める。**</summary>
    Register = 1,
}

/// <summary>ログイン ID として使う値の取り出し先。</summary>
public enum SamlLoginIdSource
{
    /// <summary><c>NameID</c> をそのまま使う。**既定。** Google はここにメールアドレスを入れる。</summary>
    NameId = 0,

    /// <summary>属性から取る。<see cref="SamlOptions.LoginIdClaim"/> で名前を指定する。</summary>
    Claim = 1,
}

/// <summary>SAML 2.0 で管理画面へ入れるようにする設定（Issue #166）。</summary>
/// <remarks>
/// <para>
/// **既定は無効。** 何も設定しなければ、今までどおりログイン ID とパスワードだけになる。
/// </para>
/// <para>
/// ⚠️ **IdP の署名証明書は設定から与える。** メタデータの URL を渡して取りに行かせると、
/// その URL を差し替えられた時点で誰でも管理者になれる。**信頼の起点は設定に置く。**
/// </para>
/// <para>
/// ⚠️ **利用者の突き合わせはログイン ID で行う。** IdP から来た値
/// （既定は <c>NameID</c>）を本アプリのログイン ID として扱う。
/// **DB の列は増やしていない**（Issue #166 の決めごと）。
/// </para>
/// </remarks>
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

    /// <summary>SAML でのログインを使うか。**既定は使わない。**</summary>
    public bool Enabled { get; init; }

    /// <summary>SP（本アプリ）の EntityID。**IdP へ登録する値と同じにする。**</summary>
    public string EntityId { get; init; } = string.Empty;

    /// <summary>IdP の EntityID。**これ以外が発行した応答は受け取らない。**</summary>
    public string IdpEntityId { get; init; } = string.Empty;

    /// <summary>IdP のログインの窓口（<c>AuthnRequest</c> の宛先）。</summary>
    public Uri? SingleSignOnUrl { get; init; }

    /// <summary>IdP の署名証明書。**入れ替えの最中は 2 枚とも並べられる。**</summary>
    public ImmutableArray<X509Certificate2> IdpCertificates { get; init; } = [];

    /// <summary>本アプリに居ない利用者の扱い。**既定は通さない。**</summary>
    public SamlUnknownUserPolicy UnknownUser { get; init; } = SamlUnknownUserPolicy.Reject;

    /// <summary>
    /// <see cref="SamlUnknownUserPolicy.Register"/> で作る利用者の役割。
    /// **既定は <see cref="AdminRole.Editor"/>。**
    /// </summary>
    /// <remarks>
    /// ⚠️ **ここを <see cref="AdminRole.Administrator"/> にすると、
    /// IdP に居る全員が全権を持つ。** 組織全体へ配っている IdP では選ばないこと。
    /// </remarks>
    public AdminRole RegisterRole { get; init; } = AdminRole.Editor;

    /// <summary>ログイン ID をどこから取るか。</summary>
    public SamlLoginIdSource LoginIdSource { get; init; } = SamlLoginIdSource.NameId;

    /// <summary>
    /// <see cref="SamlLoginIdSource.Claim"/> のときに読む属性の名前。
    /// </summary>
    public string LoginIdClaim { get; init; } = string.Empty;

    /// <summary>ログイン画面の釦に出す文字。**組織の IdP の名前を入れると分かりやすい。**</summary>
    public string ButtonLabel { get; init; } = string.Empty;

    /// <summary>設定を読む。**足りないものがあれば、その場で分かるように例外にする。**</summary>
    /// <remarks>
    /// **黙って無効へ落とさない。** 「有効にしたつもりが効いていない」を起動時に気付かせる。
    /// </remarks>
    /// <exception cref="InvalidOperationException">有効なのに設定が足りない・読めない。</exception>
    public static SamlOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsTrue(configuration[EnabledKey]))
        {
            return new SamlOptions();
        }

        var entityId = Trim(configuration[EntityIdKey]);
        var idpEntityId = Trim(configuration[IdpEntityIdKey]);
        var singleSignOnUrl = Trim(configuration[SingleSignOnUrlKey]);
        var certificates = ReadCertificates(configuration[IdpCertificateKey]);

        Require(entityId, EntityIdKey);
        Require(idpEntityId, IdpEntityIdKey);
        Require(singleSignOnUrl, SingleSignOnUrlKey);

        if (certificates.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{IdpCertificateKey} が要ります。IdP の署名証明書（PEM か base64）を入れてください。");
        }

        if (!Uri.TryCreate(singleSignOnUrl, UriKind.Absolute, out var ssoUri)
            || (ssoUri.Scheme != Uri.UriSchemeHttps && ssoUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{SingleSignOnUrlKey} は http(s) の絶対 URL で書いてください: {singleSignOnUrl}");
        }

        var loginIdSource = ParseEnum<SamlLoginIdSource>(configuration[LoginIdSourceKey], LoginIdSourceKey)
            ?? SamlLoginIdSource.NameId;
        var loginIdClaim = Trim(configuration[LoginIdClaimKey]);

        if (loginIdSource == SamlLoginIdSource.Claim && loginIdClaim.Length == 0)
        {
            throw new InvalidOperationException(
                $"{LoginIdSourceKey}=Claim のときは {LoginIdClaimKey} が要ります。");
        }

        return new SamlOptions
        {
            Enabled = true,
            EntityId = entityId,
            IdpEntityId = idpEntityId,
            SingleSignOnUrl = ssoUri,
            IdpCertificates = certificates,
            UnknownUser = ParseEnum<SamlUnknownUserPolicy>(configuration[UnknownUserKey], UnknownUserKey)
                ?? SamlUnknownUserPolicy.Reject,
            RegisterRole = ParseEnum<AdminRole>(configuration[RegisterRoleKey], RegisterRoleKey)
                ?? AdminRole.Editor,
            LoginIdSource = loginIdSource,
            LoginIdClaim = loginIdClaim,
            ButtonLabel = Trim(configuration[ButtonLabelKey]),
        };
    }

    /// <summary>照合に使う SAML の設定を作る。</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Saml2Configuration.CertificateValidationMode"/> を
    /// <see cref="X509CertificateValidationMode.None"/> にしているのは、
    /// **信頼の起点が設定で与えた証明書そのもの**だから。
    /// IdP の証明書は自己署名が普通で、連鎖を辿らせると必ず落ちる。
    /// </para>
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

            // **発行者を固定する。** 別の IdP が署名した応答を受け取らない
            AllowedIssuer = IdpEntityId,

            // **宛先も固定する。** 他所の SP 向けの応答を持ち込まれても受け取らない
            AudienceRestricted = true,

            // **署名の無い応答は受け取らない**（既定のまま）
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        configuration.AllowedAudienceUris.Add(EntityId);
        configuration.SignatureValidationCertificates.AddRange(IdpCertificates);

        // **署名の種類は IdP 任せにしない。** SHA-1 は受け取らない
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha256Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha384Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha512Signature);

        return configuration;
    }

    private static void Require(string value, string key)
    {
        if (value.Length == 0)
        {
            throw new InvalidOperationException($"{EnabledKey} を有効にしたときは {key} が要ります。");
        }
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsTrue(string? value) =>
        bool.TryParse(value?.Trim(), out var parsed) && parsed;

    /// <summary>知らない値は例外にする。**書き間違いを黙って既定へ落とさない。**</summary>
    private static TEnum? ParseEnum<TEnum>(string? value, string key)
        where TEnum : struct, Enum
    {
        var trimmed = Trim(value);
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!Enum.TryParse<TEnum>(trimmed, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new InvalidOperationException(
                $"{key} に知らない値が入っています: {trimmed}"
                + $"（使えるのは {string.Join(" / ", Enum.GetNames<TEnum>())}）");
        }

        return parsed;
    }

    /// <summary>PEM でも base64 でも読む。**複数枚はカンマか改行で並べる。**</summary>
    private static ImmutableArray<X509Certificate2> ReadCertificates(string? raw)
    {
        var trimmed = Trim(raw);
        if (trimmed.Length == 0)
        {
            return [];
        }

        // PEM は「-----BEGIN CERTIFICATE-----」の塊ごとに読む。
        // **カンマで割ると PEM の中身まで割れてしまう**ので、先に PEM を見る
        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return ReadPem(trimmed);
        }

        var builder = ImmutableArray.CreateBuilder<X509Certificate2>();
        foreach (var part in trimmed.Split(
            [',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            builder.Add(LoadBase64(part));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<X509Certificate2> ReadPem(string pem)
    {
        var builder = ImmutableArray.CreateBuilder<X509Certificate2>();
        var index = 0;

        while (true)
        {
            var begin = pem.IndexOf("-----BEGIN", index, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            var endMarker = pem.IndexOf("-----END", begin, StringComparison.Ordinal);
            if (endMarker < 0)
            {
                throw new InvalidOperationException(
                    $"{IdpCertificateKey} の PEM が途中で終わっています。");
            }

            var end = pem.IndexOf("-----", endMarker + "-----END".Length, StringComparison.Ordinal);
            var length = (end < 0 ? pem.Length : end + "-----".Length) - begin;

            try
            {
                builder.Add(X509Certificate2.CreateFromPem(pem.AsSpan(begin, length)));
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                throw new InvalidOperationException(
                    $"{IdpCertificateKey} の PEM を読めませんでした。", exception);
            }

            index = begin + length;
        }

        if (builder.Count == 0)
        {
            throw new InvalidOperationException(
                $"{IdpCertificateKey} に証明書が見つかりませんでした。");
        }

        return builder.ToImmutable();
    }

    private static X509Certificate2 LoadBase64(string value)
    {
        try
        {
            // **.NET 9 以降は X509CertificateLoader を使う**（従来の構築子は非推奨）
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"{IdpCertificateKey} を証明書として読めませんでした。"
                + "PEM か、DER を base64 にしたものを入れてください。",
                exception);
        }
    }
}
