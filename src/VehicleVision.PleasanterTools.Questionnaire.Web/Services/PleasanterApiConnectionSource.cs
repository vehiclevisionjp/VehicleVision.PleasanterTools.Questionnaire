using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 本アプリの Pleasanter 接続設定（URL と API キー）を、Core.Web の本人確認へ渡す（Issue #706）。
/// </summary>
/// <remarks>
/// <para>
/// **回答の送信と同じ接続設定**（<see cref="IPleasanterOptionsProvider"/>。管理画面で変えられ、要求ごとに読む）を返す。
/// 本人確認は、利用者の API 利用が禁止されているときと、グループの所属を確かめるときにだけ使う。
/// </para>
/// <para>
/// ⚠️ **読み出しの例外は包まずにそのまま伝える。** 検証側（Core.Web）が
/// <see cref="FormatException"/>・<see cref="OverflowException"/>・<see cref="InvalidOperationException"/> を
/// 警告に残して「API キーが無い」として扱う。置き換える前の本アプリの検証器と同じ扱い。
/// </para>
/// </remarks>
public sealed class PleasanterApiConnectionSource(IPleasanterOptionsProvider pleasanterOptionsProvider)
    : IPleasanterApiConnectionSource
{
    public async Task<PleasanterApiConnectionSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var pleasanter = await pleasanterOptionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return new PleasanterApiConnectionSettings(pleasanter.BaseUrl, pleasanter.ApiKey);
    }
}
