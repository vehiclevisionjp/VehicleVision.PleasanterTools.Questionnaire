using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者ごとの「パスワード設定済み」の印の列を足す（Issue #1086）。</summary>
/// <remarks>
/// <para>
/// SAML・Pleasanter のログインで作る管理者（JIT）は、本人が知らないランダムな値のハッシュを持つ。
/// ハッシュだけでは「本人がパスワードを知っているか」を見分けられないので、印を <c>AdminUserSettings</c> へ持つ。
/// パスキーの「最後のログイン方法を失う削除」の判定が、パスワードを数えるかどうかをこの印で決める。
/// </para>
/// <para>
/// <b>既定は「設定済み」（真）。既存の管理者は全員「設定済み」になる</b>（今の動作と同じで、誰も締め出さない）。
/// 設定の行が無い管理者は「設定済み」と読む（<see cref="IAdminUserSettingsStore.GetPasswordSetAsync"/>）。
/// すでにいる JIT 管理者は、管理画面の切り替えで「未設定」へ直す。
/// </para>
/// <para>
/// <b>番号は M00xx の続き。</b>列を足すだけで、Core の共有表には触れない（<c>AdminUserSettings</c> は M0038 で作った）。
/// すでに M100004 まで適用済みの導入先にも、未適用のこの移行が通る（FluentMigrator は未適用の移行を番号順に流す）。
/// </para>
/// </remarks>
[Migration(44, "管理者の設定へパスワード設定済みの印の列を追加する")]
public sealed class M0044_AdminUserPasswordSet : Migration
{
    public override void Up()
    {
        Alter.Table("AdminUserSettings")
            .AddColumn("PasswordSet").AsBoolean().NotNullable().WithDefaultValue(true);
    }

    public override void Down()
    {
        Delete.Column("PasswordSet").FromTable("AdminUserSettings");
    }
}
