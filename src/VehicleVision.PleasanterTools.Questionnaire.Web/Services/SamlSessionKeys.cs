namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>単一ログアウトに要る、IdP 側の手掛かり（Issue #191）。</summary>
/// <remarks>
/// <para>
/// **ログアウトの要求に載せる値だけを持つ。**
/// ⚠️ **アサーション全体は残さない。** 要らないものを cookie へ入れると、
/// 大きくなるうえに、後から「ここにあるから使おう」が起きる。
/// </para>
/// <para>
/// **<c>SessionIndex</c> は無いことがある。** IdP が出さない設定もあるので、
/// 無ければ載せずに送る（<c>NameID</c> だけでも IdP は落とせる）。
/// </para>
/// </remarks>
/// <param name="NameId">IdP が名乗った利用者の識別子。</param>
/// <param name="SessionIndex">IdP 側のログインを指す値。**無ければ空。**</param>
public sealed record SamlSessionKeys(string NameId, string? SessionIndex);
