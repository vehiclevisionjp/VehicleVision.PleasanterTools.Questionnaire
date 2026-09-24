namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>SAML の現在値を含め、合言葉ログインを許すかを決める。</summary>
public sealed class AdminPasswordSignInPolicy(
    AdminPasswordSignInOptions options,
    ILogger<AdminPasswordSignInPolicy> logger)
{
    private int warnedWhileSamlDisabled;

    public bool IsAllowed(bool samlEnabled)
    {
        if (options.Enabled)
        {
            return true;
        }

        if (samlEnabled)
        {
            Interlocked.Exchange(ref warnedWhileSamlDisabled, 0);
            return false;
        }

        // **SAML は画面から再起動なしで無効にできる。**
        // 起動時だけ検証しても後から締め出せるため、この組み合わせでは停止指定を無視する。
        // 警告は SAML が再び有効になるまで 1 回に抑え、ログを要求数で埋めない。
        if (Interlocked.Exchange(ref warnedWhileSamlDisabled, 1) == 0)
        {
            logger.LogWarning(
                "{Setting}=false は SAML が無効な間は適用せず、合言葉ログインを許可する",
                AdminPasswordSignInOptions.EnabledSetting);
        }

        return true;
    }
}
