using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>マッピングの入力に行を持たせる。</summary>
/// <remarks>
/// <para>
/// **グリッドとランキングは 1 設問が複数の入力を出す**（Issue #54）。
/// どの行を指しているかを持たないと、行ごとに列へ繋げない。
/// </para>
/// <para>
/// **NULL 可。** 行を持たない設問では指定しない（<c>MappingValidator</c> が咎める）。
/// </para>
/// <para>
/// **公開済みの版には移行が要らない。** あちらは定義まるごとの JSON なので、
/// 読むときに既定値（<c>null</c>）が入るだけ。
/// </para>
/// </remarks>
[Migration(12, "マッピングの入力に行を持たせる")]
public sealed class M0012_MappingSourceRow : Migration
{
    public override void Up() =>
        Alter.Table("AssignmentSources")
            // 行の識別子。**設問の中で一意であればよい**
            .AddColumn("RowId").AsString(64).Nullable();

    public override void Down() => Delete.Column("RowId").FromTable("AssignmentSources");
}
