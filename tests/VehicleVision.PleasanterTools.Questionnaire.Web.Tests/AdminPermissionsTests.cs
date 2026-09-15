using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>役割と権限の対応（Issue #160）。</summary>
/// <remarks>
/// **役割を足したときに、対応表へ書き忘れないためのテスト。**
/// 書き忘れると「何もできない役割」になる（通してしまうより安全な壊れ方だが、気付きたい）。
/// </remarks>
public class AdminPermissionsTests
{
    [Fact]
    public void 定義した役割すべてに権限がある()
    {
        foreach (var role in Enum.GetValues<AdminRole>())
        {
            Assert.NotEmpty(AdminPermissions.Of(role));
        }
    }

    [Fact]
    public void 特権管理者はすべての権限を持つ()
    {
        var permissions = AdminPermissions.Of(AdminRole.Administrator);

        Assert.Equal(AdminPermissions.All.Length, permissions.Count);
        foreach (var permission in AdminPermissions.All)
        {
            Assert.Contains(permission, permissions);
        }
    }

    [Fact]
    public void アンケート管理者は人に触れない()
    {
        var permissions = AdminPermissions.Of(AdminRole.SurveyAdministrator);

        Assert.Contains(AdminPermissions.SurveysPublish, permissions);
        Assert.Contains(AdminPermissions.OutboxRequeue, permissions);

        // **人の出入りには触れない**
        Assert.DoesNotContain(AdminPermissions.UsersRead, permissions);
        Assert.DoesNotContain(AdminPermissions.UsersWrite, permissions);
        Assert.DoesNotContain(AdminPermissions.UsersResetTwoFactor, permissions);

        // **操作の記録も見せない**（誰が何をしたかは、人を管理する側の情報）
        Assert.DoesNotContain(AdminPermissions.AuditRead, permissions);
    }

    [Fact]
    public void ユーザ管理者はアンケートに触れない()
    {
        var permissions = AdminPermissions.Of(AdminRole.UserAdministrator);

        Assert.Contains(AdminPermissions.UsersWrite, permissions);
        Assert.Contains(AdminPermissions.AuditRead, permissions);

        Assert.DoesNotContain(AdminPermissions.SurveysRead, permissions);
        Assert.DoesNotContain(AdminPermissions.SurveysWrite, permissions);
        Assert.DoesNotContain(AdminPermissions.SurveysPublish, permissions);
    }

    /// <summary>**編集者は公開できない。** ここが今回いちばん変わるところ。</summary>
    [Fact]
    public void 編集者は作れるが公開できない()
    {
        var permissions = AdminPermissions.Of(AdminRole.Editor);

        Assert.Contains(AdminPermissions.SurveysRead, permissions);
        Assert.Contains(AdminPermissions.SurveysWrite, permissions);
        Assert.DoesNotContain(AdminPermissions.SurveysPublish, permissions);

        // **送信状況もお知らせも見せない**（今までと同じ）
        Assert.DoesNotContain(AdminPermissions.OutboxRead, permissions);
        Assert.DoesNotContain(AdminPermissions.NotificationsRead, permissions);
    }

    [Fact]
    public void 監査担当は見るだけ()
    {
        var permissions = AdminPermissions.Of(AdminRole.Auditor);

        Assert.Contains(AdminPermissions.AuditRead, permissions);
        Assert.Contains(AdminPermissions.OutboxRead, permissions);
        Assert.Contains(AdminPermissions.NotificationsRead, permissions);

        // **書き込みは 1 つも持たない**
        Assert.DoesNotContain(AdminPermissions.SurveysWrite, permissions);
        Assert.DoesNotContain(AdminPermissions.SurveysPublish, permissions);
        Assert.DoesNotContain(AdminPermissions.OutboxRequeue, permissions);
        Assert.DoesNotContain(AdminPermissions.UsersWrite, permissions);
    }

    [Fact]
    public void 役割の名前で引ける()
    {
        Assert.Equal(
            AdminPermissions.Of(AdminRole.SurveyAdministrator),
            AdminPermissions.Of(nameof(AdminRole.SurveyAdministrator)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SuperAdmin")]
    [InlineData("administrator")]
    public void 知らない役割には何も持たせない(string? roleName)
    {
        // **大文字小文字も区別する。** claim の値は列挙の名前そのものにしてある
        Assert.Empty(AdminPermissions.Of(roleName));
        Assert.False(AdminPermissions.Has(roleName, AdminPermissions.SurveysRead));
    }

    [Fact]
    public void 権限の名前は対象と操作に分かれている()
    {
        foreach (var permission in AdminPermissions.All)
        {
            var parts = permission.Split('.');

            Assert.Equal(2, parts.Length);
            Assert.NotEmpty(parts[0]);
            Assert.NotEmpty(parts[1]);
        }
    }

    [Fact]
    public void ポリシーの名前は権限ごとに違う()
    {
        var names = AdminPermissions.All.Select(AdminPermissions.PolicyOf).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>**値を変えないこと。** DB へ数値で入っている。</summary>
    [Fact]
    public void 役割の値は動かさない()
    {
        Assert.Equal(0, (int)AdminRole.Editor);
        Assert.Equal(1, (int)AdminRole.Administrator);
        Assert.Equal(2, (int)AdminRole.SurveyAdministrator);
        Assert.Equal(3, (int)AdminRole.UserAdministrator);
        Assert.Equal(4, (int)AdminRole.Auditor);
    }
}
