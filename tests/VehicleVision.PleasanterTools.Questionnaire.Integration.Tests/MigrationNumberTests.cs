using System.Reflection;
using FluentMigrator;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>マイグレーションの採番を守る。</summary>
/// <remarks>
/// <para>
/// **番号が重複しても FluentMigrator はエラーにしない。**
/// 先に適用された方の番号が <c>VersionInfo</c> に入るため、
/// **同じ番号のもう 1 本は「適用済み」と見なされて静かに飛ばされる。**
/// 表が作られないまま、その表を使う機能だけが動く形になる。
/// </para>
/// <para>
/// **これは枝分かれした作業を合流させたときに起きる。**
/// 別々のブランチで同じ番号を取り、どちらも単体では正しく動く。
/// 合流して初めて壊れ、しかも例外は出ない。
/// </para>
/// <para>
/// 実際に踏んだ（2026-08-19）。設問エディタと管理者の招待が両方 3 番を取り、
/// 検証環境では先に流した方だけが適用されていた。
/// </para>
/// </remarks>
public class MigrationNumberTests
{
    private static IReadOnlyList<(Type Type, long Version, string Description)> Migrations()
    {
        return typeof(DatabaseMigrator).Assembly
            .GetTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<MigrationAttribute>()))
            .Where(pair => pair.Attribute is not null)
            .Select(pair => (pair.Type, pair.Attribute!.Version, pair.Attribute.Description))
            .OrderBy(item => item.Version)
            .ToList();
    }

    [Fact]
    public void 番号は重複しない()
    {
        var duplicated = Migrations()
            .GroupBy(migration => migration.Version)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(" / ", group.Select(m => m.Type.Name))}")
            .ToArray();

        Assert.True(
            duplicated.Length == 0,
            "同じ番号のマイグレーションがある。**片方は静かに飛ばされる。**"
            + $"合流したときは採番し直すこと: {string.Join("、", duplicated)}");
    }

    [Fact]
    public void クラス名の番号と属性の番号が一致する()
    {
        foreach (var (type, version, _) in Migrations())
        {
            // M0004_AdminInvitations → 4
            var prefix = type.Name.Split('_')[0];
            Assert.StartsWith("M", prefix, StringComparison.Ordinal);

            Assert.True(
                int.TryParse(prefix[1..], out var fromName),
                $"クラス名から番号を読み取れない: {type.Name}");

            Assert.True(
                fromName == version,
                $"クラス名と属性の番号が食い違う: {type.Name} は {version} 番。"
                + "**採番し直すときは両方直すこと**");
        }
    }

    // **飛び番は見ない。** 枝分かれして作業している間は、
    // 他のブランチが取った番号がこちらに無いのが普通で、飛んでいること自体は害が無い。
    // FluentMigrator は番号順に流すだけで、連番であることを求めない。
    // **害があるのは重複だけ**（片方が静かに飛ばされる）。
}
