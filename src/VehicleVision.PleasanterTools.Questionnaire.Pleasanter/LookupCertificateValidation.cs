using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>
/// ルックアップの接続先（HTTPS）の証明書の検証に失敗したことを、接続先のホスト名ごとに記録する（Issue #898）。
/// </summary>
/// <remarks>
/// <para>
/// 証明書の検証失敗は、.NET では <see cref="HttpRequestException"/> の内側の
/// <see cref="System.Security.Authentication.AuthenticationException"/> として現れるが、
/// 「TLS を話さない相手」など別の原因も同じ形になり、例外だけでは区別できない。
/// そこで検証の呼び出し（<see cref="SocketsHttpHandler"/> では、送信者が <see cref="SslStream"/>）で、
/// 検証に失敗した時刻をホスト名ごとに残し、検索クライアントが「この要求の開始以降に失敗したか」を調べる。
/// </para>
/// <para>
/// **検証は緩めない。** 既定と同じく、エラーが 1 つでもあれば拒否する。
/// 記録するのはホスト名・時刻・<see cref="SslPolicyErrors"/> の値だけで、証明書の内容は持たない。
/// 記録は結果の分類（502 か 503）にだけ使い、回答者への応答・ログには出さない。
/// </para>
/// </remarks>
public static class LookupCertificateValidation
{
    /// <summary>記録するホスト名の上限。接続先は管理済みの 1 つだが、無制限に溜めない。</summary>
    private const int MaxHosts = 64;

    private static readonly ConcurrentDictionary<string, (long Timestamp, SslPolicyErrors Errors)> Failures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>検証の結果を記録するハンドラ。リダイレクトは追わない。</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        SslOptions = { RemoteCertificateValidationCallback = Validate },
    };

    /// <summary>検証の呼び出し。エラーがなければ受け入れ、あれば記録して拒否する。</summary>
    public static bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        var host = sender switch
        {
            SslStream stream => stream.TargetHostName,
            HttpRequestMessage request => request.RequestUri?.Host,
            _ => null,
        };
        if (!string.IsNullOrEmpty(host))
        {
            Record(host, errors);
        }

        return false;
    }

    /// <summary>ホスト名の検証失敗を、いまの時刻で記録する。</summary>
    public static void Record(string host, SslPolicyErrors errors)
    {
        if (Failures.Count >= MaxHosts)
        {
            Failures.Clear();
        }

        Failures[host] = (Stopwatch.GetTimestamp(), errors);
    }

    /// <summary>現在の時刻（<see cref="TryGetFailure"/> の <c>since</c> に渡す）。</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary><paramref name="since"/> 以降に、そのホストで証明書の検証に失敗したか。</summary>
    public static bool TryGetFailure(string host, long since, out SslPolicyErrors errors)
    {
        if (Failures.TryGetValue(host, out var failure) && failure.Timestamp >= since)
        {
            errors = failure.Errors;
            return true;
        }

        errors = SslPolicyErrors.None;
        return false;
    }
}
