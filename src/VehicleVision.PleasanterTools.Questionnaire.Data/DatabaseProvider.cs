using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>対応する RDBMS。</summary>
/// <remarks>
/// **Pleasanter が対応する 3 つに揃える**（<c>_documents/アーキテクチャ方針.md</c> 13 章）。
/// 導入先の Pleasanter が使う RDBMS に合わせられないと、DB サーバを別に立てることになる。
/// </remarks>
public enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
    MySql,
}

/// <summary>接続を作る。</summary>
public interface IDbConnectionFactory
{
    DatabaseProvider Provider { get; }

    DbConnection Create();
}

/// <summary>設定から接続を作る。</summary>
/// <remarks>
/// <para>
/// **接続文字列は設定ファイルへ直書きしない。** 環境変数か Key Vault から読む
/// （<c>App_Data/Parameters/README.md</c>）。
/// </para>
/// <para>
/// **暗号化を既定値任せにしない。** 与えられた接続文字列が黙っているときは
/// こちらで <c>Encrypt=True</c> を立てる。ドライバの既定は版で変わり得るし
/// （Microsoft.Data.SqlClient は 4.0 で <c>true</c> へ変わった）、
/// **書かれていないものは読む側にも伝わらない**。
/// 切られていないかの検査は <see cref="ConnectionSecurity"/> が起動時に行う。
/// </para>
/// </remarks>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(DatabaseProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Provider = provider;
        _connectionString = provider is DatabaseProvider.SqlServer
            ? Encrypted(connectionString)
            : connectionString;
    }

    public DatabaseProvider Provider { get; }

    public DbConnection Create() => Provider switch
    {
        DatabaseProvider.SqlServer => new SqlConnection(_connectionString),
        DatabaseProvider.PostgreSql => new NpgsqlConnection(_connectionString),
        DatabaseProvider.MySql => new MySqlConnection(_connectionString),
        _ => throw new NotSupportedException($"対応していない RDBMS: {Provider}"),
    };

    /// <summary>SQL Server 向けに暗号化を立てる。</summary>
    /// <remarks>
    /// <para>
    /// **<c>Encrypt=False</c> と書かれていても暗号化する。** ここは譲らない。
    /// 「暗号化しない」という選択肢を残すと、逃げ道の設定 1 つで平文になる。
    /// </para>
    /// <para>
    /// **<c>QUESTIONNAIRE_DB_ALLOW_INSECURE</c> が緩めるのは証明書の確認であって、
    /// 暗号化ではない。** 検証環境が自己署名の証明書を使うための逃げ道なので、
    /// <c>TrustServerCertificate</c> はそのまま通す。
    /// </para>
    /// <para>
    /// なお <c>Encrypt=False</c> と書いてあること自体は
    /// <see cref="ConnectionSecurity"/> が起動時に咎める。
    /// **できない指定を黙って読み替えるのではなく、言ってから守る。**
    /// </para>
    /// </remarks>
    private static string Encrypted(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString) { Encrypt = true }.ConnectionString;
}
