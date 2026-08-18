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
/// **接続文字列は設定ファイルへ直書きしない。** 環境変数か Key Vault から読む
/// （<c>App_Data/Parameters/README.md</c>）。
/// </remarks>
public sealed class DbConnectionFactory(DatabaseProvider provider, string connectionString)
    : IDbConnectionFactory
{
    public DatabaseProvider Provider { get; } = provider;

    public DbConnection Create() => Provider switch
    {
        DatabaseProvider.SqlServer => new SqlConnection(connectionString),
        DatabaseProvider.PostgreSql => new NpgsqlConnection(connectionString),
        DatabaseProvider.MySql => new MySqlConnection(connectionString),
        _ => throw new NotSupportedException($"対応していない RDBMS: {Provider}"),
    };
}
