namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>
/// ルックアップ項目を、サーバー全体で有効にするかを表す（Issue #883・仕様書 10 章）。
/// </summary>
/// <remarks>
/// <b>既定は無効。</b>実機（本アプリ経由）の総合判定がそろうまで有効にしない。
/// 無効のとき、回答者向けの公開 API はルックアップを「使えない」として扱い、回答画面の取得応答へ <c>lookups</c> を載せない。
/// 管理側（定義の編集・保存・資格情報・接続試験・プレビュー）は、回答者へ何も出さないので無効でも使える。
/// </remarks>
public sealed record LookupFeatureOptions(bool Enabled)
{
    /// <summary>ルックアップを有効にする設定。</summary>
    public const string EnabledSetting = "QUESTIONNAIRE_LOOKUP_ENABLED";

    /// <summary>設定を読む。未設定・空は無効。真偽値以外は、設定名つきで起動を止める。</summary>
    public static LookupFeatureOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[EnabledSetting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LookupFeatureOptions(false);
        }

        return bool.TryParse(raw.Trim(), out var enabled)
            ? new LookupFeatureOptions(enabled)
            : throw new InvalidOperationException(
                $"{EnabledSetting} には true / false のいずれかを指定する（未設定なら無効）");
    }
}
