using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>接続文字列が暗号化を切っていないかを見る。</summary>
/// <remarks>
/// <para>
/// **接続文字列は運用者が与える。** コードの側からは中身が見えないので、
/// 暗号化を切った設定のまま本番へ出ても気付けない。
/// **起動時に見て、切っていたら止める。**
/// </para>
/// <para>
/// 検証環境では自己署名の証明書を使うため、**開発中だけは緩めてよい**。
/// ただし「緩めてよい」と決めるのはアプリではなく設定であり、
/// **既定は厳しい側**にしてある。
/// </para>
/// <para>
/// CodeQL の <c>cs/insecure-sql-connection</c> がこの場所を指摘した（2026-08-19）。
/// **指摘そのものは「切られているかもしれない」であって、切れている証拠ではない。**
/// だからこそ、切られていないことを実際に確かめる仕組みを置く。
/// </para>
/// </remarks>
public static class ConnectionSecurity
{
    /// <summary>接続文字列の不備。</summary>
    /// <param name="Reason">何が問題か。**接続文字列そのものは入れない**（資格情報を含む）。</param>
    public sealed record Problem(string Reason);

    /// <summary>暗号化が切られていないかを確かめる。</summary>
    /// <returns>不備。無ければ空。</returns>
    public static IReadOnlyList<Problem> Inspect(DatabaseProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var problems = new List<Problem>();

        switch (provider)
        {
            case DatabaseProvider.SqlServer:
            {
                var builder = new SqlConnectionStringBuilder(connectionString);

                // Microsoft.Data.SqlClient 4.0 以降の既定は Encrypt=true。
                // **明示して切っている場合だけを咎める**
                if (!builder.Encrypt)
                {
                    problems.Add(new Problem("Encrypt=False になっている。通信が平文で流れる"));
                }

                if (builder.TrustServerCertificate)
                {
                    // **暗号化はされるが、相手が本物かを確かめない。**
                    // 中間者に差し替えられても気付けない
                    problems.Add(new Problem(
                        "TrustServerCertificate=True になっている。証明書を確かめないため中間者を見抜けない"));
                }

                break;
            }

            case DatabaseProvider.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString);

                if (builder.SslMode is SslMode.Disable)
                {
                    problems.Add(new Problem("SslMode=Disable になっている。通信が平文で流れる"));
                }

                if (builder.SslMode is SslMode.Prefer or SslMode.Allow)
                {
                    // **相手が拒めば平文へ落ちる。** 落ちたことは利用者に見えない
                    problems.Add(new Problem(
                        $"SslMode={builder.SslMode} は、相手が拒むと平文へ落ちる。Require 以上にする"));
                }

                if (builder.SslMode is SslMode.Require)
                {
                    // Npgsql の Require は証明書を確かめない（VerifyCA / VerifyFull が確かめる）
                    problems.Add(new Problem(
                        "SslMode=Require は証明書を確かめない。VerifyCA か VerifyFull にする"));
                }

                break;
            }

            case DatabaseProvider.MySql:
            {
                var builder = new MySqlConnectionStringBuilder(connectionString);

                if (builder.SslMode is MySqlSslMode.None or MySqlSslMode.Disabled)
                {
                    problems.Add(new Problem("SslMode=None になっている。通信が平文で流れる"));
                }

                if (builder.SslMode is MySqlSslMode.Preferred)
                {
                    problems.Add(new Problem(
                        "SslMode=Preferred は、相手が拒むと平文へ落ちる。Required 以上にする"));
                }

                if (builder.SslMode is MySqlSslMode.Required)
                {
                    problems.Add(new Problem(
                        "SslMode=Required は証明書を確かめない。VerifyCA か VerifyFull にする"));
                }

                break;
            }

            default:
                problems.Add(new Problem($"対応していない RDBMS: {provider}"));
                break;
        }

        return problems;
    }

    /// <summary>不備があれば例外にする。**起動時に呼ぶ。**</summary>
    /// <param name="allowInsecure">
    /// 検証環境のために緩める。**本番で真にしないこと。**
    /// </param>
    public static void EnsureSecure(
        DatabaseProvider provider,
        string connectionString,
        bool allowInsecure)
    {
        var problems = Inspect(provider, connectionString);
        if (problems.Count == 0 || allowInsecure)
        {
            return;
        }

        throw new InvalidOperationException(
            "DB への接続が暗号化されていない、または相手を確かめていない: "
            + string.Join(" / ", problems.Select(problem => problem.Reason))
            + "。検証環境で意図して緩めるなら QUESTIONNAIRE_DB_ALLOW_INSECURE=true を設定する");
    }
}
