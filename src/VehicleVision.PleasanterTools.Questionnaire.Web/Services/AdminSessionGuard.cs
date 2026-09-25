using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StackExchange.Redis;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>セッション ID をストアと DB に突き合わせ、principal を復元する。</summary>
/// <remarks>
/// <para>
/// **止めた管理者を、その場で追い出すため。** cookie は 8 時間有効なので、
/// これが無いと**止めたのに最大 8 時間は操作できてしまう。**
/// 「止め忘れを見つけて止める」ことに意味を持たせるには、止めた瞬間に効く必要がある。
/// </para>
/// <para>
/// **役割の食い違いでも追い出す。** 降格した相手が、
/// 古い cookie に載った <see cref="AdminRole.Administrator"/> のまま操作を続けられては困る。
/// </para>
/// <para>
/// 要求ごとに 1 回だけ DB を引く（認証の結果は要求の中で使い回される）。
/// 管理画面の頻度なら釣り合う。
/// </para>
/// </remarks>
public static class AdminSessionGuard
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
        => await ValidateAsync(context, AdminSessionKind.Session).ConfigureAwait(false);

    public static async Task ValidatePendingAsync(CookieValidatePrincipalContext context)
        => await ValidateAsync(context, AdminSessionKind.Pending).ConfigureAwait(false);

    public static async Task ValidateReenrollAsync(CookieValidatePrincipalContext context)
        => await ValidateAsync(context, AdminSessionKind.Reenroll).ConfigureAwait(false);

    private static async Task ValidateAsync(
        CookieValidatePrincipalContext context,
        AdminSessionKind kind)
    {
        var manager = context.HttpContext.RequestServices.GetRequiredService<AdminSessionManager>();
        (AdminSessionEntry Entry, ClaimsPrincipal Principal)? restored;
        try
        {
            restored = await manager
                .FindAsync(context.Principal, kind, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // **要求が打ち切られただけ**（再読み込み・画面遷移・タブを閉じる。Issue #426）。
            //
            // ⚠️ **RejectAsync を呼ばないこと。** 中断を「セッション無効」に倒すと、
            // **打ち切られた要求で cookie を捨てる**副作用が出る。
            // **応答はどこへも返らない**ので、何もせず戻るのが正しい。
            //
            // ⚠️ **ここで拾わないと最外周まで飛ぶ。** デバッガが毎回止まり、
            // **本物の異常と見分けがつかなくなる**（実際にそうなっていた）。
            // **中断でないもの（タイムアウトなど）は when で除いて投げ直す。**
            return;
        }
        catch (RedisException exception)
        {
            // KVS 停止時も cookie だけで通さない。障害はログへ出し、認証は失敗させる。
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILogger<AdminSessionManager>>();
            logger.LogError(exception, "管理者セッションストアを読み取れなかった");
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        if (restored is null)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        var principal = restored.Value.Principal;
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var adminUserId)
            || adminUserId != restored.Value.Entry.AdminUserId)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        var store = context.HttpContext.RequestServices.GetRequiredService<IAdminUserStore>();
        var user = await store.FindByIdAsync(adminUserId, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (user is null
            || user.IsDisabled
            || !string.Equals(
                principal!.FindFirstValue(ClaimTypes.Role),
                user.Role.ToString(),
                StringComparison.Ordinal))
        {
            await context.HttpContext.RequestServices.GetRequiredService<IAdminSessionStore>()
                .DeleteAsync(restored.Value.Entry.AdminSessionId, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        // **Pleasanter のログインで入った人は、一定間隔で Pleasanter に確かめ直す**（Issue #464）。
        // Pleasanter でログアウト・無効化された人を、本アプリに残さない
        if (kind == AdminSessionKind.Session
            && context.HttpContext.RequestServices.GetService<PleasanterSsoSessionRevalidator>()
                is { } revalidator)
        {
            var revalidation = await revalidator
                .RevalidateAsync(
                    context.HttpContext,
                    principal,
                    restored.Value.Entry.AdminSessionId,
                    context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (revalidation is PleasanterSsoRevalidation.Rejected or PleasanterSsoRevalidation.UpstreamError)
            {
                if (revalidation is PleasanterSsoRevalidation.UpstreamError)
                {
                    // **401 ではなく 503 を返させる印**（AdminAuthSchemes.Configure）。
                    // 画面が「ログアウトされた」ではなく「確かめられなかった」と出せるように
                    context.HttpContext.Items[PleasanterSsoSessionRevalidator.UpstreamErrorItemKey] = true;
                }

                await context.HttpContext.RequestServices.GetRequiredService<IAdminSessionStore>()
                    .DeleteAsync(restored.Value.Entry.AdminSessionId, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }
        }

        context.ReplacePrincipal(principal);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();

        // **cookie も消す。** 残すと、要求のたびに DB を引き直すだけの死んだ cookie になる
        await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
    }
}
