using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>SQL の中の識別子が、その RDBMS の引用符へ書き換わること。</summary>
/// <remarks>
/// **DB へは繋がない。** 文字列を変換するだけなので、環境変数の有無に関わらず走る。
/// </remarks>
public class SqlFormatTests
{
    [Fact]
    public void SqlServerでは書き換えない()
    {
        const string sql = "SELECT [PublicId] FROM [Surveys] WHERE [SurveyId] = @SurveyId";

        // **角括弧がそのまま引用符。** 触る必要がない
        Assert.Equal(sql, SqlDialect.Format(DatabaseProvider.SqlServer, sql));
    }

    [Fact]
    public void PostgreSqlでは二重引用符になる()
    {
        Assert.Equal(
            "SELECT \"PublicId\" FROM \"Surveys\" WHERE \"SurveyId\" = @SurveyId",
            SqlDialect.Format(
                DatabaseProvider.PostgreSql,
                "SELECT [PublicId] FROM [Surveys] WHERE [SurveyId] = @SurveyId"));
    }

    [Fact]
    public void MySqlでは逆引用符になる()
    {
        Assert.Equal(
            "SELECT `PublicId` FROM `Surveys` WHERE `SurveyId` = @SurveyId",
            SqlDialect.Format(
                DatabaseProvider.MySql,
                "SELECT [PublicId] FROM [Surveys] WHERE [SurveyId] = @SurveyId"));
    }

    [Fact]
    public void パラメータは触らない()
    {
        // **@ で始まる名前は値の入り口。** 引用符で包んではいけない
        var formatted = SqlDialect.Format(
            DatabaseProvider.PostgreSql,
            "WHERE [Status] = @Status AND [NextAttemptAt] <= @Now");

        Assert.Contains("@Status", formatted, StringComparison.Ordinal);
        Assert.Contains("@Now", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void 別名を付けた列も書き換わる()
    {
        Assert.Equal(
            "SELECT v.\"DefinitionJson\" FROM \"SurveyVersions\" v",
            SqlDialect.Format(
                DatabaseProvider.PostgreSql,
                "SELECT v.[DefinitionJson] FROM [SurveyVersions] v"));
    }

    [Fact]
    public void 囲んでいない識別子はそのまま残る()
    {
        // **囲み忘れが目で見て分かるようにしておく。**
        // 黙って直すと、書き忘れに気付けなくなる
        var formatted = SqlDialect.Format(DatabaseProvider.PostgreSql, "SELECT PublicId FROM [Surveys]");

        Assert.Contains("SELECT PublicId", formatted, StringComparison.Ordinal);
        Assert.Contains("\"Surveys\"", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("COUNT(*)")]
    [InlineData("IS NULL")]
    [InlineData("ORDER BY")]
    public void 予約語や関数は触らない(string fragment)
    {
        var formatted = SqlDialect.Format(DatabaseProvider.MySql, $"SELECT {fragment} FROM [Surveys]");

        Assert.Contains(fragment, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void 数字で始まる名前は識別子として扱わない()
    {
        // 添字などを巻き込まないための決まり
        var formatted = SqlDialect.Format(DatabaseProvider.PostgreSql, "SELECT [1abc] FROM [Surveys]");

        Assert.Contains("[1abc]", formatted, StringComparison.Ordinal);
    }
}
