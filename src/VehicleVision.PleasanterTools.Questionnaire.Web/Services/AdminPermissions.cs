using System.Collections.Frozen;
using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面でできることの単位（Issue #160）。</summary>
/// <remarks>
/// <para>
/// **役割へ直接ぶら下げない。** 「この操作は Administrator だけ」と各所へ書くと、
/// 役割を 1 つ増やすたびに全部を見直すことになる。
/// **権限を挟めば、機能が増えたときに権限を 1 つ足すだけで済む。**
/// </para>
/// <para>
/// **名前は `対象.操作` にそろえる。** 増えたときに並べて読めるようにするため。
/// </para>
/// </remarks>
public static class AdminPermissions
{
    /// <summary>アンケートの閲覧。</summary>
    public const string SurveysRead = "surveys.read";

    /// <summary>アンケートの作成・編集（下書き）。</summary>
    public const string SurveysWrite = "surveys.write";

    /// <summary>アンケートの公開・停止・複製。</summary>
    /// <remarks>
    /// **作るのと公開するのを分ける。** 「作るのはこの人、出す判断は別の人」を作れるようにする。
    /// </remarks>
    public const string SurveysPublish = "surveys.publish";

    /// <summary>テンプレートの利用。</summary>
    public const string TemplatesRead = "templates.read";

    /// <summary>テンプレートの作成・削除。</summary>
    public const string TemplatesWrite = "templates.write";

    /// <summary>送信状況の閲覧。</summary>
    public const string OutboxRead = "outbox.read";

    /// <summary>送信できなかった回答を送信待ちへ戻す。</summary>
    public const string OutboxRequeue = "outbox.requeue";

    /// <summary>お知らせの閲覧・既読。</summary>
    public const string NotificationsRead = "notifications.read";

    /// <summary>操作の記録の閲覧。</summary>
    public const string AuditRead = "audit.read";

    /// <summary>管理者の閲覧。</summary>
    public const string UsersRead = "users.read";

    /// <summary>管理者の追加・招待・役割変更・停止。</summary>
    public const string UsersWrite = "users.write";

    /// <summary>他人の 2 要素認証の解除（Issue #154）。</summary>
    /// <remarks>
    /// **保護を外す操作なので、書き込みとは分けて持つ。**
    /// 人の出入りだけ任せたい相手に、保護を外す力まで渡さずに済む。
    /// </remarks>
    public const string UsersResetTwoFactor = "users.resetTwoFactor";

    /// <summary>すべての権限。**役割の対応表を作るときに使う。**</summary>
    public static readonly ImmutableArray<string> All =
    [
        SurveysRead,
        SurveysWrite,
        SurveysPublish,
        TemplatesRead,
        TemplatesWrite,
        OutboxRead,
        OutboxRequeue,
        NotificationsRead,
        AuditRead,
        UsersRead,
        UsersWrite,
        UsersResetTwoFactor,
    ];

    /// <summary>役割ごとに持つ権限。</summary>
    /// <remarks>
    /// ⚠️ **役割を足したらここへ書く。** 書き忘れると「何もできない役割」になる
    /// （通してしまうより安全な壊れ方を選んでいる）。
    /// </remarks>
    private static readonly FrozenDictionary<AdminRole, FrozenSet<string>> ByRole =
        new Dictionary<AdminRole, FrozenSet<string>>
        {
            // **特権管理者。** 今までの Administrator と同じ
            [AdminRole.Administrator] = All.ToFrozenSet(StringComparer.Ordinal),

            // **アンケート管理者。** アンケートの運用を任せる相手。人には触れない
            [AdminRole.SurveyAdministrator] = new[]
            {
                SurveysRead,
                SurveysWrite,
                SurveysPublish,
                TemplatesRead,
                TemplatesWrite,
                OutboxRead,
                OutboxRequeue,
                NotificationsRead,
            }.ToFrozenSet(StringComparer.Ordinal),

            // **ユーザ管理者。** 人の出入りと、その記録
            [AdminRole.UserAdministrator] = new[]
            {
                UsersRead,
                UsersWrite,
                UsersResetTwoFactor,
                AuditRead,
            }.ToFrozenSet(StringComparer.Ordinal),

            // **アンケート編集者。** 作るところまで。**公開はできない**
            [AdminRole.Editor] = new[]
            {
                SurveysRead,
                SurveysWrite,
                TemplatesRead,
            }.ToFrozenSet(StringComparer.Ordinal),

            // **監査担当。** 見るだけ
            [AdminRole.Auditor] = new[]
            {
                AuditRead,
                NotificationsRead,
                OutboxRead,
            }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary();

    /// <summary>その役割が持つ権限。**知らない役割には何も持たせない。**</summary>
    public static IReadOnlyCollection<string> Of(AdminRole role) =>
        ByRole.TryGetValue(role, out var permissions) ? permissions : FrozenSet<string>.Empty;

    /// <summary>その役割が持つ権限。**役割の名前で引く**（claim から引くときに使う）。</summary>
    public static IReadOnlyCollection<string> Of(string? roleName) =>
        Enum.TryParse<AdminRole>(roleName, out var role) && Enum.IsDefined(role)
            ? Of(role)
            : FrozenSet<string>.Empty;

    /// <summary>その役割がその権限を持つか。</summary>
    public static bool Has(string? roleName, string permission) =>
        Of(roleName).Contains(permission);

    /// <summary>権限を要求するポリシーの名前。</summary>
    /// <remarks>**役割ではなく権限で要求する**ので、名前も権限から作る。</remarks>
    public static string PolicyOf(string permission) => $"Admin.Session.Permission:{permission}";
}
