namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>
/// 本アプリ固有のセッションの段階の名前。Core.Data の <see cref="AdminSessionKinds"/>（<c>Session</c> / <c>Pending</c>）に足す。
/// </summary>
/// <remarks>Core のセッションの段階は文字列（任意の名前を受ける）。列の長さは 16 文字まで。</remarks>
public static class QuestionnaireSessionKinds
{
    /// <summary>
    /// ログイン済みのまま 2 要素を登録し直している途中。
    /// <b>ログインの途中（<see cref="AdminSessionKinds.Pending"/>）としては使えない</b>（別の段階にしてある）。
    /// </summary>
    public const string Reenroll = "Reenroll";
}
