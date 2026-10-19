namespace VehicleVision.PleasanterTools.Questionnaire.Mail;

/// <summary>
/// このアプリのメール設定の名前。送信の部品は Core.Mail にあり、<b>環境変数の接頭辞はアプリごとに決める</b>
/// ので、ここで持つ。
/// </summary>
public static class QuestionnaireMailSettings
{
    /// <summary>メールの環境変数の接頭辞（例: <c>QUESTIONNAIRE_MAIL_ENABLED</c>）。</summary>
    public const string Prefix = "QUESTIONNAIRE_MAIL_";
}
