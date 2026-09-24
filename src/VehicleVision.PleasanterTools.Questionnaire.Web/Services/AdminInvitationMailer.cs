using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理者の招待をメールで送る（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **これまでは URL を画面に出して、人が手で渡していた。**
/// 渡す途中（チャット・口頭・付箋）で漏れる経路を減らすために、本人へ直接送る。
/// </para>
/// <para>
/// ⚠️ **画面へ URL を出すのをやめない。** メールが送れない構成（設定していない・
/// ログイン ID がメールアドレスでない）でも、招待は今までどおり出せること。
/// **送れたかどうかを画面へ返す**ので、送れていなければ手で渡せる。
/// </para>
/// <para>
/// ⚠️ **招待の URL は、それだけで管理者になれる値。**
/// 送信待ちの行では暗号化され、送れたら行ごと消える（<c>MailOutbox</c>）。
/// </para>
/// </remarks>
public sealed class AdminInvitationMailer(
    IMailOutbox outbox,
    IMailPayloadProtector protector,
    IMailSettingsProvider mailSettings,
    AdminPathOptions adminPath,
    ILogger<AdminInvitationMailer> logger)
{
    public AdminInvitationMailer(
        IMailOutbox outbox,
        IMailPayloadProtector protector,
        MailOptions options,
        ILogger<AdminInvitationMailer> logger,
        AdminPathOptions? adminPath = null)
        : this(
            outbox,
            protector,
            new FixedMailSettingsProvider(options),
            adminPath ?? new AdminPathOptions(AdminPathOptions.DefaultPath),
            logger)
    {
    }

    /// <summary>招待のメールを積む。**積んだら <c>true</c>。**</summary>
    /// <param name="loginId">招いた相手のログイン ID。**メールアドレスのときだけ送る。**</param>
    /// <param name="token">招待のトークン。**この 1 回しか受け取れない値。**</param>
    /// <param name="expiresAtUtc">期限（UTC）。</param>
    /// <param name="language">招いた側が使っている言語。</param>
    /// <param name="cancellationToken">中断。</param>
    public async Task<bool> TryEnqueueAsync(
        string loginId,
        string token,
        DateTime expiresAtUtc,
        string? language,
        CancellationToken cancellationToken = default)
    {
        var options = await mailSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!options.IsReady)
        {
            return false;
        }

        if (options.Link(
                $"{adminPath.Path}/invitations/accept?token={Uri.EscapeDataString(token)}")
            is not { } url)
        {
            // ⚠️ **要求の Host から組み立てない**（host header injection）。
            // 設定が無ければ送らず、画面から手で渡してもらう
            logger.LogWarning(
                "招待メールを送れない（{Key}BASEURL が未設定）。画面の URL を手で渡すこと",
                MailOptions.Prefix);
            return false;
        }

        if (!MailAddress.TryCreate(loginId, out _))
        {
            // **ログイン ID がメールアドレスとは限らない。** 宛先が無いだけで、異常ではない
            logger.LogInformation("ログイン ID がメールアドレスではないので、招待メールは送らない");
            return false;
        }

        try
        {
            var mail = new OutgoingMail(
                loginId,
                ServerMessages.Get(ServerMessageKeys.InvitationMailSubject, language),
                ServerMessages.Get(
                    ServerMessageKeys.InvitationMailBody,
                    language,
                    url,
                    expiresAtUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));

            // ⚠️ **ここで初めて暗号化する。** 招待の URL が平文で DB に載る経路を作らない
            return await outbox.EnqueueAsync(
                MailIdOf(token),
                (int)MailKind.AdminInvitation,
                Guid.Empty,
                protector.Protect(mail),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // ⚠️ **招待そのものは既に出ている。** メールを積めなかったことで失敗にしない
            logger.LogError(exception, "招待メールを積めなかった。画面の URL を手で渡すこと");
            return false;
        }
    }

    /// <summary>招待のトークンから、送信待ちの識別子を決める。</summary>
    /// <remarks>
    /// ⚠️ **トークンそのものを識別子にしない。** 送信待ちの行は管理画面から見える所にあり、
    /// **招待の URL はそれだけで管理者になれる値**（<c>AutoReplyDispatcher</c> と同じ理由）。
    /// **出し直すたびにトークンが変わる**ので、識別子も変わって新しい 1 通が積まれる。
    /// </remarks>
    public static Guid MailIdOf(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("invitation:" + token));
        return new Guid(hash.AsSpan(0, 16));
    }
}
