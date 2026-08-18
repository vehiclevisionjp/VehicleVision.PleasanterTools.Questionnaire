namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理者の合言葉に求める条件。</summary>
/// <remarks>
/// **1 か所に置く。** 最初の管理者・招待の受け取り・変更のどこかだけ緩いと、
/// そこが一番弱い所になる（<c>_documents/非機能設計.md</c> 1 章）。
/// </remarks>
public static class AdminPasswordPolicy
{
    /// <summary>最低の長さ。</summary>
    /// <remarks>
    /// **文字種の縛りではなく長さで担保する。** 記号を混ぜさせると短く単純な値に寄る。
    /// </remarks>
    public const int MinimumLength = 12;

    /// <summary>受け付けられる合言葉か。</summary>
    public static bool IsAcceptable(string? password) =>
        password is not null && password.Length >= MinimumLength;

    /// <summary>合っていないときに返す文言。</summary>
    public static string Message => $"合言葉は {MinimumLength} 文字以上にしてください。";
}
