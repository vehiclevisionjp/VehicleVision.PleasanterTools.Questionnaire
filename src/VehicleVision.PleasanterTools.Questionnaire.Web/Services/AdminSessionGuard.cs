using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>ログイン済みの cookie を、要求のたびに DB と突き合わせる。</summary>
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
    {
        var principal = context.Principal;

        if (!Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var adminUserId))
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
            await RejectAsync(context).ConfigureAwait(false);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();

        // **cookie も消す。** 残すと、要求のたびに DB を引き直すだけの死んだ cookie になる
        await context.HttpContext.SignOutAsync(AdminAuthSchemes.Session).ConfigureAwait(false);
    }
}
