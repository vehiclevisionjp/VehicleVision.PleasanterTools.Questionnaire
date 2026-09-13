using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Mail;

/// <summary>SMTP の暗号化の掛け方。</summary>
public enum SmtpSecurity
{
    /// <summary>平文で繋いでから <c>STARTTLS</c> で上げる。**既定。587 番はこれ。**</summary>
    StartTls,

    /// <summary>最初から TLS で繋ぐ。**465 番はこれ。**</summary>
    ImplicitTls,

    /// <summary>暗号化しない。⚠️ **検証用。本番で使わない。**</summary>
    None,
}

/// <summary>メール送信の設定（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **既定は無効。** 設定しなければメールは 1 通も出ない。
/// インターネットへ出られないイントラでも動かせる状態を既定にする
/// （外部の CAPTCHA と同じ考え方。<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// <para>
/// **送信経路は SMTP だけ**（Issue #189 の決定）。**認証付きリレーの 587 番は
/// Azure でブロックされない**ので、SendGrid・Amazon SES・
/// Azure Communication Services・Microsoft 365 のどれにもこれで届く。
/// マネージド ID で秘密を持たない経路は Issue #198。
/// </para>
/// <para>
/// ⚠️ **パスワードを画面へ返さない。** 設定は環境変数で与え、管理画面から読めるようにしない。
/// </para>
/// </remarks>
public sealed record MailOptions
{
    /// <summary>メールを送るか。**既定は無効。**</summary>
    public bool Enabled { get; init; }

    /// <summary>SMTP サーバ。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>ポート。**既定は 587**（認証付きリレー）。</summary>
    /// <remarks>
    /// ⚠️ **25 番を既定にしない。** Azure は 25 番の外向き接続を塞ぐ
    /// （サブスクリプション種別による）。**587 は塞がらない。**
    /// </remarks>
    public int Port { get; init; } = 587;

    /// <summary>暗号化の掛け方。</summary>
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    /// <summary>認証のユーザ名。**空なら認証しない。**</summary>
    public string? UserName { get; init; }

    /// <summary>認証のパスワード。**ログにも画面にも出さない。**</summary>
    public string? Password { get; init; }

    /// <summary>差出人のアドレス。</summary>
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>差出人の表示名。**空なら付けない。**</summary>
    public string? FromName { get; init; }

    /// <summary>返信先。**空なら差出人と同じ。**</summary>
    /// <remarks>
    /// 自動返信メールは**送りっぱなしにしない。** 回答者が返信できる先を運用側で決められること。
    /// </remarks>
    public string? ReplyToAddress { get; init; }

    /// <summary>1 通あたりの待ち時間の上限。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>送れる形になっているか。</summary>
    /// <remarks>**半端な設定で送り始めない。** 有効なのに欠けていれば、起動時に落とす。</remarks>
    public bool IsReady =>
        Enabled
        && !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(FromAddress)
        && Port is > 0 and <= 65535;

    /// <summary>設定の接頭辞。</summary>
    public const string Prefix = "QUESTIONNAIRE_MAIL_";

    /// <summary>設定から読む。</summary>
    /// <remarks>
    /// **読めない値を黙って既定へ落とさない**（<c>ResponseSenderOptions</c> と同じ）。
    /// 設定したつもりが効いていない状態を作らない。
    /// </remarks>
    public static MailOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ReadBool(configuration, Prefix + "ENABLED");
        if (!enabled)
        {
            // **無効なら他の値を読まない。** 使わない設定の不備で起動を止めない
            return new MailOptions();
        }

        var options = new MailOptions
        {
            Enabled = true,
            Host = configuration[Prefix + "SMTP_HOST"] ?? string.Empty,
            Port = ReadInt(configuration, Prefix + "SMTP_PORT", 587),
            Security = ReadSecurity(configuration, Prefix + "SMTP_SECURITY"),
            UserName = configuration[Prefix + "SMTP_USER"],
            Password = configuration[Prefix + "SMTP_PASSWORD"],
            FromAddress = configuration[Prefix + "FROM_ADDRESS"] ?? string.Empty,
            FromName = configuration[Prefix + "FROM_NAME"],
            ReplyToAddress = configuration[Prefix + "REPLYTO_ADDRESS"],
            Timeout = TimeSpan.FromSeconds(ReadInt(configuration, Prefix + "TIMEOUT_SECONDS", 30)),
        };

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException(
                $"{Prefix}ENABLED が有効なのに {Prefix}SMTP_HOST が設定されていない");
        }

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            throw new InvalidOperationException(
                $"{Prefix}ENABLED が有効なのに {Prefix}FROM_ADDRESS が設定されていない");
        }

        if (options.Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"{Prefix}SMTP_PORT は 1〜65535 で指定する（今の値: {options.Port}）");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{Prefix}TIMEOUT_SECONDS は 1 以上で指定する");
        }

        return options;
    }

    private static bool ReadBool(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は true か false で指定する（今の値: {raw}）");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{key} は整数で指定する（今の値: {raw}）");
    }

    private static SmtpSecurity ReadSecurity(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SmtpSecurity.StartTls;
        }

        return Enum.TryParse<SmtpSecurity>(raw, ignoreCase: true, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は StartTls / ImplicitTls / None のいずれかで指定する（今の値: {raw}）");
    }
}
