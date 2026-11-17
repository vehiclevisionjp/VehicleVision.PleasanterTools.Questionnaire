namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 管理 API の状態を変える要求のうち、別オリジンから来たものを 403 で拒む（Issue #907）。
/// 判定は <see cref="AdminCsrfGuard"/>。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ **<c>UseQuestionnairePathBase</c> と <c>UseRouting</c> より後ろに置く。**
/// 例外のパスは、サブパスを剥がした後の <c>Path</c> で照合する。
/// 認証より前に置いて、**未認証の別オリジンの要求（ログインの試行など）も同じく拒む。**
/// </para>
/// <para>
/// **ログへ出すのは、拒んだ事実と、ヘッダーの値の種別だけ。** <c>Origin</c> の値・cookie・本文・秘密は出さない。
/// </para>
/// </remarks>
public sealed class AdminCsrfMiddleware(RequestDelegate next, ILogger<AdminCsrfMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (AdminCsrfGuard.IsAllowed(context.Request, out var reason))
        {
            return next(context);
        }

        logger.LogWarning(
            "管理 API への別オリジンからの状態変更を拒否しました。method={Method} path={Path} {Reason}",
            context.Request.Method,
            context.Request.Path.Value,
            reason);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        // **理由の詳細は返さない。** 画面から正しく呼ぶ限り、ここには来ない
        return context.Response.WriteAsJsonAsync(new { code = AdminCsrfGuard.RejectedCode });
    }
}
