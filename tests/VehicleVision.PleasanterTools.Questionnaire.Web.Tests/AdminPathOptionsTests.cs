using VehicleVision.PleasanterTools.Questionnaire.Web;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class AdminPathOptionsTests
{
    [Theory]
    [InlineData(null, "/admin")]
    [InlineData("/admin", "/admin")]
    [InlineData("/back-office-2", "/back-office-2")]
    public void 正常なパスを受け入れる(string? value, string expected)
    {
        Assert.Equal(expected, AdminPathOptions.Parse(value).Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("/Admin")]
    [InlineData("/admin/other")]
    [InlineData("/admin_2")]
    [InlineData("/管理")]
    public void 書式が不正なパスを拒否する(string value)
    {
        Assert.Throws<InvalidOperationException>(() => AdminPathOptions.Parse(value));
    }

    [Fact]
    public void 長すぎるパスを拒否する()
    {
        var value = "/" + new string('a', AdminPathOptions.MaximumLength);

        Assert.Throws<InvalidOperationException>(() => AdminPathOptions.Parse(value));
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/assets")]
    [InlineData("/f")]
    [InlineData("/fonts")]
    [InlineData("/healthz")]
    [InlineData("/openapi")]
    [InlineData("/ready")]
    [InlineData("/scalar")]
    public void 既存の入口と衝突するパスを拒否する(string value)
    {
        Assert.Throws<InvalidOperationException>(() => AdminPathOptions.Parse(value));
    }
}
