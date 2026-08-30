namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>複数インスタンスで共有する ASP.NET Core Data Protection 鍵束の設定。</summary>
public static class DataProtectionKeys
{
    /// <summary>鍵束を置く共有ディレクトリ。</summary>
    public const string PathSetting = "QUESTIONNAIRE_DATA_PROTECTION_KEYS_PATH";

    /// <summary>
    /// 鍵束を共有するプロセスの識別子。版や Pod ごとに変えると Cookie を共有できない。
    /// </summary>
    public const string ApplicationName = "VehicleVision.PleasanterTools.Questionnaire";
}
