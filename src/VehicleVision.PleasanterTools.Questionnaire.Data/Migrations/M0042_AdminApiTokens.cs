using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理 API の個人トークン（API キー）の表を作る（Issue #1025。親 #1012）。</summary>
/// <remarks>
/// <para>
/// <b>保存するのは SHA-256 のハッシュ（小文字の 16 進 64 文字）だけ。</b>平文は発行時に 1 回だけ返し、どこにも残さない。
/// 256 ビットの乱数なので、パスワードと違って PBKDF2 は使わず、ハッシュで直に引く（招待トークンと同じ判断）。
/// </para>
/// <para>
/// <b>持ち主（<c>OwnerAdminUserId</c>）は Core の表（<c>Core_AdminUsers</c>）の管理者。</b>
/// 外部キーは張らない（Core は他の表からの参照を前提にしない。<c>AdminUserSettings</c>（M0038）と同じ）。
/// 管理者を消す操作は無い（止めるだけ）ので、孤児は出ない。結合もしないので、MySQL の照合順序の調整も要らない。
/// </para>
/// <para>
/// <b>期限は必須</b>（<c>ExpiresAt</c> は NOT NULL。無期限は作れない）。
/// 失効は行を消さず <c>RevokedAt</c> を入れる（一覧と監査で「いつ・誰が・なぜ」を追えるように）。
/// </para>
/// </remarks>
[Migration(42, "管理 API の個人トークンの表を追加する")]
public sealed class M0042_AdminApiTokens : Migration
{
    public override void Up()
    {
        Create.Table("AdminApiTokens")
            .WithColumn("TokenId").AsGuid().NotNullable().PrimaryKey("PK_AdminApiTokens")
            .WithColumn("OwnerAdminUserId").AsGuid().NotNullable()
            .WithColumn("Name").AsString(100).NotNullable()
            // 識別用の接頭辞（qat_ ＋ 8 文字）。一覧・監査へ出してよい。**ここから有効なトークンは作れない**
            .WithColumn("Prefix").AsString(16).NotNullable()
            .WithColumn("TokenHash").AsString(64).NotNullable()
            // 空白区切りのスコープ（例: "surveys.read surveys.write"）
            .WithColumn("Scopes").AsString(256).NotNullable()
            // カンマ区切りの CIDR。空なら制限しない
            .WithColumn("IpAllowList").AsString(1024).Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("RevokedAt").AsDateTime2().Nullable()
            .WithColumn("RevokedBy").AsGuid().Nullable()
            // 失効の理由の符号（owner / administrator / ownerDisabled / passwordChanged / twoFactorReset ほか）
            .WithColumn("RevokeReason").AsString(32).Nullable()
            .WithColumn("LastUsedAt").AsDateTime2().Nullable()
            .WithColumn("LastUsedIp").AsString(64).Nullable();

        // ハッシュで引く（認証の入口）。一意にして、同じ値を 2 本作れないようにする
        Create.Index("UX_AdminApiTokens_TokenHash")
            .OnTable("AdminApiTokens")
            .OnColumn("TokenHash").Ascending()
            .WithOptions().Unique();

        Create.Index("IX_AdminApiTokens_OwnerAdminUserId")
            .OnTable("AdminApiTokens")
            .OnColumn("OwnerAdminUserId").Ascending();
    }

    public override void Down() => Delete.Table("AdminApiTokens");
}
