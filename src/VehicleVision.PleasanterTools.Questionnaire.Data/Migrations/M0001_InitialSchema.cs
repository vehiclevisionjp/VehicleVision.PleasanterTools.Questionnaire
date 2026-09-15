using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>最初のスキーマ。</summary>
/// <remarks>
/// <para>
/// **1 つの定義から SQL Server / PostgreSQL / MySQL の 3 つへ適用する。**
/// 方言差は FluentMigrator が吸収する（<c>_documents/データモデル設計.md</c> 5 章）。
/// </para>
/// <para>
/// **マイグレーションは前方のみ。** <c>Down</c> は用意するが、
/// **運用の巻き戻し手段として頼らない。**
/// </para>
/// </remarks>
[Migration(1, "最初のスキーマ")]
public sealed class M0001_InitialSchema : Migration
{
    public override void Up()
    {
        // **JSON はネイティブ JSON 型を使わない。** 3 者で機能差が大きく、検索・更新の
        // 書き方が揃わない（_documents/データモデル設計.md 4 章）。
        // AsString(int.MaxValue) は SQL Server の NVARCHAR(MAX) / PostgreSQL の TEXT /
        // MySQL の LONGTEXT へそれぞれ写る

        // ---- アンケート ----------------------------------------------------
        Create.Table("Surveys")
            .WithColumn("SurveyId").AsGuid().NotNullable().PrimaryKey()
            // **推測不能な値。** 回答用 URL に使う。サイト ID を素で出さない
            .WithColumn("PublicId").AsString(64).NotNullable().Unique()
            .WithColumn("Title").AsString(512).NotNullable()
            .WithColumn("PleasanterSiteId").AsInt64().NotNullable()
            // **割り当ては任意。** NULL なら回答の正本を持たない
            .WithColumn("ResponseJsonColumn").AsString(64).Nullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("SuspendedReason").AsInt32().Nullable()
            .WithColumn("SuspendedAt").AsDateTime2().Nullable()
            .WithColumn("AcceptFrom").AsDateTime2().Nullable()
            .WithColumn("AcceptTo").AsDateTime2().Nullable()
            .WithColumn("ResponseLimit").AsInt32().Nullable()
            .WithColumn("PublishedVersion").AsInt32().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // **公開時に固める不変のスナップショット。** 消さない
        Create.Table("SurveyVersions")
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("Version").AsInt32().NotNullable()
            .WithColumn("DefinitionJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("MappingJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("PublishedAt").AsDateTime2().NotNullable()
            .WithColumn("PublishedBy").AsGuid().Nullable();

        Create.PrimaryKey("PK_SurveyVersions")
            .OnTable("SurveyVersions").Columns("SurveyId", "Version");

        // ---- 回答 ----------------------------------------------------------
        // **送信後も残す。** 回答の編集で ReferenceId を引くために要る
        Create.Table("ResponseTokens")
            .WithColumn("ResponseToken").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("SurveyId").AsGuid().NotNullable()
            // **NULL は「まだ Create していない」を表す**
            .WithColumn("PleasanterReferenceId").AsInt64().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        Create.Index("IX_ResponseTokens_SurveyId")
            .OnTable("ResponseTokens").OnColumn("SurveyId").Ascending();

        // **送信待ち。キューではなく回答ごとに 1 行。** 送信できたら削除する
        Create.Table("Responses")
            .WithColumn("ResponseToken").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("SurveyVersion").AsInt32().NotNullable()
            // **回答本文。個人情報を含み得る。送信できたら確実に消す**
            .WithColumn("PayloadJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("RetryCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("NextAttemptAt").AsDateTime2().NotNullable()
            .WithColumn("LastError").AsString(1024).Nullable()
            .WithColumn("LockedBy").AsString(64).Nullable()
            .WithColumn("LockedUntil").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // 送信ワーカーが「送るべき行」を探す索引
        Create.Index("IX_Responses_Status_NextAttemptAt")
            .OnTable("Responses")
            .OnColumn("Status").Ascending()
            .OnColumn("NextAttemptAt").Ascending();

        Create.Index("IX_Responses_SurveyId")
            .OnTable("Responses").OnColumn("SurveyId").Ascending();

        // ---- 管理者 --------------------------------------------------------
        Create.Table("AdminUsers")
            .WithColumn("AdminUserId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("LoginId").AsString(256).NotNullable().Unique()
            // **ハッシュのみ。平文・可逆暗号にしない**
            .WithColumn("PasswordHash").AsString(512).NotNullable()
            .WithColumn("Role").AsInt32().NotNullable()
            .WithColumn("IsDisabled").AsBoolean().NotNullable().WithDefaultValue(false)
            // **暗号化して保存する。平文で持たない**
            .WithColumn("TotpSecretEncrypted").AsString(512).Nullable()
            .WithColumn("TotpEnabledAt").AsDateTime2().Nullable()
            // **退職者の止め忘れを見つける唯一の手掛かり**
            .WithColumn("LastLoginAt").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // **端末紛失で全員が締め出される事故を防ぐ**
        Create.Table("AdminRecoveryCodes")
            .WithColumn("RecoveryCodeId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("AdminUserId").AsGuid().NotNullable()
            .WithColumn("CodeHash").AsString(512).NotNullable()
            .WithColumn("UsedAt").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable();

        Create.Index("IX_AdminRecoveryCodes_AdminUserId")
            .OnTable("AdminRecoveryCodes").OnColumn("AdminUserId").Ascending();

        // ---- 監査 ----------------------------------------------------------
        // **回答本文を入れない**
        Create.Table("AuditLogs")
            .WithColumn("AuditLogId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("OccurredAt").AsDateTime2().NotNullable()
            .WithColumn("AdminUserId").AsGuid().Nullable()
            .WithColumn("Action").AsString(128).NotNullable()
            .WithColumn("TargetType").AsString(128).Nullable()
            .WithColumn("TargetId").AsString(128).Nullable()
            .WithColumn("DetailJson").AsString(int.MaxValue).Nullable()
            .WithColumn("IpAddress").AsString(64).Nullable();

        Create.Index("IX_AuditLogs_OccurredAt")
            .OnTable("AuditLogs").OnColumn("OccurredAt").Descending();
    }

    public override void Down()
    {
        Delete.Table("AuditLogs");
        Delete.Table("AdminRecoveryCodes");
        Delete.Table("AdminUsers");
        Delete.Table("Responses");
        Delete.Table("ResponseTokens");
        Delete.Table("SurveyVersions");
        Delete.Table("Surveys");
    }

}
