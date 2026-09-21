using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class DatabaseStartupMigrationTests
{
    [Fact]
    public void ロック待機時間は既定で五分にする()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            DatabaseStartupMigration.ReadLockTimeout(null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    public void 不正なロック待機時間では起動を止める(string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => DatabaseStartupMigration.ReadLockTimeout(value));
    }

    [Fact]
    public void 空のロック待機時間は未設定として扱う()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            DatabaseStartupMigration.ReadLockTimeout(string.Empty));
    }

    [Fact]
    public async Task 検査を明示的に外したときだけDBなしで受付可能になる()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseStartupMigration.SkipCheckSetting] = "true",
            })
            .Build();
        var state = new DatabaseStartupState();

        await DatabaseStartupMigration.RunAsync(
            configuration,
            Data.DatabaseProvider.PostgreSql,
            "Host=192.0.2.1;Database=q;Username=q;******",
            state,
            NullLogger.Instance);

        Assert.True(state.IsReady);
    }

    [Fact]
    public void 完了前は受付可能にしない()
    {
        var state = new DatabaseStartupState();

        Assert.False(state.IsReady);

        state.MarkReady();

        Assert.True(state.IsReady);
    }

    [Theory]
    [InlineData("/healthz", true)]
    [InlineData("/ready", true)]
    [InlineData("/HEALTHZ", true)]
    [InlineData("/admin", false)]
    [InlineData("/api/forms/example", false)]
    public void 移行中は生存確認とreadinessだけを通す(string path, bool expected)
    {
        Assert.Equal(
            expected,
            DatabaseStartupMigration.IsAvailableBeforeMigration(new PathString(path)));
    }
}
