using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>メールの送信待ちを、Core.Data の共有表（<c>Core_MailOutbox</c>）へ写す。</summary>
/// <remarks>
/// <para>
/// 旧 <c>MailOutbox</c> の行を <c>Core_MailOutbox</c> へ写し、旧表を落とす（<see cref="LegacyStoreMigration"/>）。
/// </para>
/// <para>
/// <b>送信待ちの行は、まだ送っていないメール。失わない。</b>
/// <c>SurveyId</c> は <c>SubjectId</c> へ写す。<b>旧はアンケートに紐づかないメールを <c>Guid.Empty</c>（全部ゼロ）で持っていたので、
/// <c>NULL</c> にする</b>（Core は「紐づかない」を NULL で持つ）。そのほかは同名の列をそのまま写す。
/// </para>
/// </remarks>
[Migration(100001, "メールの送信待ちを Core.Data の共有表へ写す")]
public sealed class M100001_MigrateMailOutboxToCore : Migration
{
    /// <summary>旧が「紐づかない」を表していた値（<see cref="Guid.Empty"/>）。</summary>
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    public override void Up() =>
        LegacyStoreMigration.CopyThenDrop(this, "MailOutbox", provider =>
            "INSERT INTO [Core_MailOutbox] "
            + "([MailId], [Kind], [SubjectId], [PayloadProtected], [Status], [RetryCount], "
            + " [NextAttemptAt], [LastError], [LockedBy], [LockedUntil], [CreatedAt], [UpdatedAt]) "
            + "SELECT m.[MailId], m.[Kind], "
            + $"CASE WHEN m.[SurveyId] = '{EmptyGuid}' THEN NULL ELSE m.[SurveyId] END, "
            + "m.[PayloadProtected], m.[Status], m.[RetryCount], "
            + "m.[NextAttemptAt], m.[LastError], m.[LockedBy], m.[LockedUntil], m.[CreatedAt], m.[UpdatedAt] "
            + "FROM [MailOutbox] m "
            + "WHERE NOT EXISTS (SELECT 1 FROM [Core_MailOutbox] c WHERE c.[MailId] = "
            + LegacyStoreMigration.KeyOf(provider, "m.[MailId]") + ")");

    /// <summary>前方のみ。旧表の形へは戻さない。</summary>
    public override void Down()
    {
    }
}
