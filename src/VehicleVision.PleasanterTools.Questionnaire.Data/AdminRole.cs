namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>管理者の役割。</summary>
/// <remarks>
/// ⚠️ **メンバーの名前は変えないこと。** DB（<c>Core_AdminUsers.Role</c>）へは**名前の文字列**で入っている。
/// 名前を変えると、既存の管理者の役割が読めなくなる。
/// <b>数値は DB へ入らない</b>（旧 <c>AdminUsers.Role</c> の整数は、移行 M100004 が名前へ写した。
/// 移行の対応表はこの値のまま固定してある）。
/// <b>足すときは末尾へ足し、<c>AdminPermissions</c> の対応表と <see cref="AdminRoleMap"/> の移行用の対応にも書く。</b>
/// </remarks>
public enum AdminRole
{
    /// <summary>アンケートの作成と編集ができる。**公開はできない**（Issue #160）。</summary>
    Editor = 0,

    /// <summary>**特権管理者。** すべてができる。</summary>
    Administrator = 1,

    /// <summary>
    /// **アンケート管理者。** アンケートの作成・編集・公開と、送信状況まで。
    /// **人には触れない。**
    /// </summary>
    SurveyAdministrator = 2,

    /// <summary>
    /// **ユーザ管理者。** 管理者の追加・招待・役割変更・停止と、操作の記録まで。
    /// **アンケートには触れない。**
    /// </summary>
    UserAdministrator = 3,

    /// <summary>**監査担当。** 操作の記録・お知らせ・送信状況を**見るだけ**。</summary>
    Auditor = 4,
}

/// <summary>
/// <see cref="AdminRole"/> と、Core.Data が持つ役割の名前（<c>Core_AdminUsers.Role</c>。文字列）の読み替え。
/// </summary>
/// <remarks>
/// 名前は <see cref="AdminRole"/> のメンバー名そのもの。<b>Core が意味を知っているのは
/// <see cref="AdminRoleNames.Administrator"/> だけ</b>（最後の 1 人を守る判定）で、それ以外はアプリが決める。
/// </remarks>
public static class AdminRoleMap
{
    /// <summary>役割を DB に置く名前にする。</summary>
    public static string ToName(AdminRole role) =>
        Enum.IsDefined(role)
            ? role.ToString()
            : throw new ArgumentOutOfRangeException(nameof(role), role, "定義されていない役割");

    /// <summary>
    /// DB の名前から役割を読む。**定義されている名前（大文字小文字まで一致）だけ**。数字や知らない名前は <c>null</c>。
    /// </summary>
    public static AdminRole? TryParse(string? name) =>
        Enum.TryParse<AdminRole>(name, ignoreCase: false, out var role) && Enum.IsDefined(role) && role.ToString() == name
            ? role
            : null;
}
