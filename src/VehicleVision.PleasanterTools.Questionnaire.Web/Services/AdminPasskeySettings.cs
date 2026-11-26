namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面のパスキーの外部設定を読み、起動前に検証する（Issue #1082）。</summary>
/// <remarks>
/// <para>
/// 設定名は <c>QUESTIONNAIRE_ADMIN_PASSKEY</c>（有効にするか。<b>既定は無効</b>）・<c>_RPID</c>（RP ID。ホスト名だけ）・
/// <c>_ORIGINS</c>（許可する Origin。カンマ区切り）。名前の組み立ては Core.Web の <see cref="AdminPasskeySettingKeys"/> に任せる。
/// </para>
/// <para>
/// <b>Core.Web は、RP ID・Origin が未設定ならリクエストのホストから導ける。本アプリはその道を使わない</b>
/// （Issue #1082 の設計 4/4）。<c>Host</c> ヘッダーは偽装でき、偽装されると攻撃者の値が RP ID・Origin になるため。
/// 有効なのにどちらかが未設定・空なら、<b>足りない設定名を挙げて起動を止める</b>。
/// </para>
/// <para>
/// <b>無効（既定）のときは、<c>_RPID</c>・<c>_ORIGINS</c> を検証しない。</b>有効かどうかの真偽値の書き間違いだけは止める
/// （黙って無効のまま動くと、有効にしたつもりで使えない）。
/// </para>
/// </remarks>
public static class AdminPasskeySettings
{
    /// <summary>設定名の接頭辞。</summary>
    public const string Prefix = "QUESTIONNAIRE_";

    /// <summary>設定名（<c>QUESTIONNAIRE_ADMIN_PASSKEY</c> ほか）。</summary>
    public static AdminPasskeySettingKeys Keys { get; } = new(Prefix);

    /// <summary>外部設定から読む。</summary>
    /// <param name="configuration">外部設定。</param>
    /// <param name="rpName">認証器の画面に出す RP の名前（設定ではない）。</param>
    /// <exception cref="InvalidOperationException">設定の誤り、または有効なのに RP ID・Origin が足りない。</exception>
    public static AdminPasskeyOptions FromConfiguration(IConfiguration configuration, string rpName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FromValues(key => configuration[key], rpName);
    }

    /// <summary>生値から読む。</summary>
    /// <exception cref="InvalidOperationException">設定の誤り、または有効なのに RP ID・Origin が足りない。</exception>
    public static AdminPasskeyOptions FromValues(Func<string, string?> valueOf, string rpName)
    {
        ArgumentNullException.ThrowIfNull(valueOf);

        // まず有効かどうかだけを読む。無効なら RP ID・Origin は見ない（無効時は検証しない）
        var enabledOnly = AuthenticationOptionsAdapter.Read(() => AdminPasskeyOptions.FromValues(
            key => string.Equals(key, Keys.EnabledKey, StringComparison.Ordinal) ? valueOf(key) : null,
            Keys,
            rpName));
        if (!enabledOnly.Enabled)
        {
            return enabledOnly;
        }

        // 書き間違い（RP ID の形・Origin の形・両者の食い違い）は Core.Web の検査に任せる
        var options = AuthenticationOptionsAdapter.Read(() => AdminPasskeyOptions.FromValues(valueOf, Keys, rpName));

        // **導出に頼らない。** 足りない設定名を全部挙げる
        var missing = new List<string>();
        if (options.RpId is null)
        {
            missing.Add(Keys.RpIdKey);
        }

        if (options.Origins.Count == 0)
        {
            missing.Add(Keys.OriginsKey);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Keys.EnabledKey}=true のときは、{string.Join(" と ", missing)} の設定が必要です"
                + "（リクエストの Host ヘッダーから補うことはしません）。");
        }

        return options;
    }
}
