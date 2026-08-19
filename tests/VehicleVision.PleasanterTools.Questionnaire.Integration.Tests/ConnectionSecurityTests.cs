using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>DB への通信が平文で流れないこと。</summary>
/// <remarks>
/// **接続文字列は運用者が与える。** コードからは中身が見えないので、
/// 暗号化を切った設定のまま本番へ出ても気付けない。起動時に見て止める。
///
/// **DB へは繋がない。** 文字列を読むだけなので、環境変数の有無に関わらず走る。
/// </remarks>
public class ConnectionSecurityTests
{
    // ---- SQL Server ---------------------------------------------------------

    [Fact]
    public void SqlServer_暗号化を切っていたら咎める()
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.SqlServer,
            "Server=db;Database=Q;UID=sa;PWD=x;Encrypt=False");

        Assert.Contains(problems, problem => problem.Reason.Contains("平文", StringComparison.Ordinal));
    }

    [Fact]
    public void SqlServer_証明書を確かめない設定を咎める()
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.SqlServer,
            "Server=db;Database=Q;UID=sa;PWD=x;TrustServerCertificate=True");

        // **暗号化はされるが、相手が本物かを確かめない**
        Assert.Contains(
            problems,
            problem => problem.Reason.Contains("中間者", StringComparison.Ordinal));
    }

    [Fact]
    public void SqlServer_既定は暗号化されるので咎めない()
    {
        // Microsoft.Data.SqlClient 4.0 以降の既定は Encrypt=true
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.SqlServer,
            "Server=db;Database=Q;UID=sa;PWD=x");

        Assert.Empty(problems);
    }

    // ---- PostgreSQL ---------------------------------------------------------

    [Theory]
    [InlineData("Disable")]
    [InlineData("Prefer")]
    [InlineData("Allow")]
    [InlineData("Require")]
    public void PostgreSql_確かめない設定を咎める(string sslMode)
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.PostgreSql,
            $"Host=db;Database=q;Username=postgres;Password=x;SslMode={sslMode}");

        Assert.NotEmpty(problems);
    }

    [Theory]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void PostgreSql_証明書まで確かめる設定は通す(string sslMode)
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.PostgreSql,
            $"Host=db;Database=q;Username=postgres;Password=x;SslMode={sslMode}");

        Assert.Empty(problems);
    }

    // ---- MySQL --------------------------------------------------------------

    [Theory]
    [InlineData("None")]
    [InlineData("Preferred")]
    [InlineData("Required")]
    public void MySql_確かめない設定を咎める(string sslMode)
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.MySql,
            $"Server=db;Database=q;Uid=root;Pwd=x;SslMode={sslMode}");

        Assert.NotEmpty(problems);
    }

    [Theory]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void MySql_証明書まで確かめる設定は通す(string sslMode)
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.MySql,
            $"Server=db;Database=q;Uid=root;Pwd=x;SslMode={sslMode}");

        Assert.Empty(problems);
    }

    // ---- 起動時の扱い -------------------------------------------------------

    [Fact]
    public void 不備があれば起動を止める()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectionSecurity.EnsureSecure(
                DatabaseProvider.SqlServer,
                "Server=db;Database=Q;UID=sa;PWD=x;Encrypt=False",
                allowInsecure: false));

        // **どう直せばよいかを書く**
        Assert.Contains("QUESTIONNAIRE_DB_ALLOW_INSECURE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 明示して緩めたときだけ通す()
    {
        // 検証環境は自己署名の証明書を使う。**明示したときだけ**
        ConnectionSecurity.EnsureSecure(
            DatabaseProvider.SqlServer,
            "Server=db;Database=Q;UID=sa;PWD=x;TrustServerCertificate=True",
            allowInsecure: true);
    }

    [Fact]
    public void 咎める理由に接続文字列そのものを入れない()
    {
        var problems = ConnectionSecurity.Inspect(
            DatabaseProvider.SqlServer,
            "Server=db;Database=Q;UID=sa;PWD=秘密の合言葉;Encrypt=False");

        // **資格情報をログや例外へ流さない**
        Assert.All(
            problems,
            problem => Assert.DoesNotContain("秘密の合言葉", problem.Reason, StringComparison.Ordinal));
    }
}
