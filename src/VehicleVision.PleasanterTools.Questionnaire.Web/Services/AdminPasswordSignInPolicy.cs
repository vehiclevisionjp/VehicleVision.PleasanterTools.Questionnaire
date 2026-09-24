using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>SAML・Pleasanter のログインの現在値を含め、合言葉ログインを許すかを決める。</summary>
public sealed class AdminPasswordSignInPolicy(
    AdminPasswordSignInOptions options,
    IDataProtectionProvider protectionProvider,
    TimeProvider timeProvider,
    ILogger<AdminPasswordSignInPolicy> logger)
{
    private const string RescueCookie = "q.admin.rescue";
    private static readonly TimeSpan RescueLifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector protector =
        protectionProvider.CreateProtector("Questionnaire.AdminRescueGrant.v1");
    private int warnedWhileSamlDisabled;

    /// <param name="ssoEnabled">SAML か Pleasanter のログイン（Issue #464）のどちらかが有効か。</param>
    public bool IsAllowed(HttpContext context, bool ssoEnabled)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (options.Enabled)
        {
            return true;
        }

        if (!ssoEnabled)
        {
            // **SAML も Pleasanter のログインも画面から再起動なしで無効にできる。**
            // 起動時だけ検証しても後から締め出せるため、この組み合わせでは停止指定を無視する。
            // 警告は SAML が再び有効になるまで 1 回に抑え、ログを要求数で埋めない。
            if (Interlocked.Exchange(ref warnedWhileSamlDisabled, 1) == 0)
            {
                logger.LogWarning(
                    "{Setting}=false は SAML と Pleasanter のログインがどちらも無効な間は適用せず、合言葉ログインを許可する",
                    AdminPasswordSignInOptions.EnabledSetting);
            }

            return true;
        }

        Interlocked.Exchange(ref warnedWhileSamlDisabled, 0);
        return HasRescueGrant(context);
    }

    /// <summary>救済トークンを照合し、短時間だけ合言葉の入口を開く。</summary>
    public bool TryGrantRescue(HttpContext context, string? suppliedToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (options.RescueToken is null || suppliedToken is null)
        {
            return false;
        }

        // **長さの違いも比較時間へ出さない。** 固定長のハッシュ同士を比較する。
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.RescueToken));
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            return false;
        }

        var expiresAt = timeProvider.GetUtcNow().Add(RescueLifetime);
        var value = protector.Protect(
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        context.Response.Cookies.Append(RescueCookie, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/admin",
            MaxAge = RescueLifetime,
        });
        return true;
    }

    private bool HasRescueGrant(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(RescueCookie, out var value))
        {
            return false;
        }

        try
        {
            var rawExpiry = protector.Unprotect(value);
            return long.TryParse(
                    rawExpiry,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expiry)
                && DateTimeOffset.FromUnixTimeSeconds(expiry) > timeProvider.GetUtcNow();
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
