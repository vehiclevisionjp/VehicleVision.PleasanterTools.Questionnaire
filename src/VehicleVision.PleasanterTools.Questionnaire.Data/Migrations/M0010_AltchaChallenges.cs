using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>使い終えた proof-of-work の課題。</summary>
/// <remarks>
/// <para>
/// **同じ解答を 2 度通さないために持つ**（Issue #55）。
/// 記録しないと、**1 回解くだけで何度でも投稿できる**。
/// </para>
/// <para>
/// **メモリに持たない。** スケールアウトすると別のインスタンスが同じ解答を通してしまうし、
/// 再起動で忘れる。**忘れた分だけ使い回しが通る。**
/// </para>
/// <para>
/// **期限を持たせて消す。** 課題そのものに期限があるので、
/// 過ぎたものを覚えておく意味は無い（放っておくと増え続ける）。
/// </para>
/// </remarks>
[Migration(10, "使い終えた proof-of-work の課題")]
public sealed class M0010_AltchaChallenges : Migration
{
    public override void Up()
    {
        Create.Table("AltchaChallenges")
            // **課題そのものを鍵にする。** 16 進の SHA-256 なので長さは決まっている
            .WithColumn("Challenge").AsString(128).NotNullable().PrimaryKey()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable();

        // **期限切れを消すために引く**
        Create.Index("IX_AltchaChallenges_ExpiresAt")
            .OnTable("AltchaChallenges").OnColumn("ExpiresAt").Ascending();
    }

    public override void Down() => Delete.Table("AltchaChallenges");
}
