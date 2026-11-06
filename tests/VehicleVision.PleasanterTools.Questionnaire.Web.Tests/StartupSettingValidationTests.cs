using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class StartupSettingValidationTests
{
    [Fact]
    public void DBの種類が未設定ならSqlServerになる()
    {
        Assert.Equal(DatabaseProvider.SqlServer, StartupSettingValidation.ParseDatabaseProvider(null));
    }

    [Theory]
    [InlineData("SqlServer", DatabaseProvider.SqlServer)]
    [InlineData("PostgreSql", DatabaseProvider.PostgreSql)]
    [InlineData("MySql", DatabaseProvider.MySql)]
    [InlineData("Sqlite", DatabaseProvider.Sqlite)]
    public void DBの種類は名前の完全一致で読む(string value, DatabaseProvider expected)
    {
        Assert.Equal(expected, StartupSettingValidation.ParseDatabaseProvider(value));
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("")]
    [InlineData("sqlserver")]
    [InlineData("0")]
    public void DBの種類が知らない値なら設定名と使える値を示して止める(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StartupSettingValidation.ParseDatabaseProvider(value));

        Assert.Contains("QUESTIONNAIRE_DB_PROVIDER", exception.Message);
        Assert.Contains("PostgreSql", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 秘密の鍵が未設定でも空でも設定名を示して止める(string? value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StartupSettingValidation.RequireSecretKey(value));

        Assert.Contains("QUESTIONNAIRE_SECRET_KEY", exception.Message);
    }

    [Fact]
    public void 秘密の鍵の値は検証せずそのまま返す()
    {
        Assert.Equal("abc", StartupSettingValidation.RequireSecretKey("abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redisの接続文字列が無ければ設定名を示して止める(string? value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StartupSettingValidation.RequireRedisConnectionString(value, "QUESTIONNAIRE_X"));

        Assert.Contains("QUESTIONNAIRE_X", exception.Message);
    }
}
