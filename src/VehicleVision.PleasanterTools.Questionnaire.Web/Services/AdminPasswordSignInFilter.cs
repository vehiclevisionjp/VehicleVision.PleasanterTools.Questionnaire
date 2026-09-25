namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>SAML か Pleasanter のログインが有効な間、許可されていない合言葉ログイン要求を入口で拒否する。</summary>
public sealed class AdminPasswordSignInFilter(
    ISamlOptionsProvider samlProvider,
    IPleasanterSsoOptionsProvider pleasanterSsoProvider,
    AdminPasswordSignInPolicy passwordSignIn) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var saml = (await samlProvider
                .GetAsync(context.HttpContext.RequestAborted)
                .ConfigureAwait(false))
            .Options;
        var pleasanterSso = (await pleasanterSsoProvider
                .GetAsync(context.HttpContext.RequestAborted)
                .ConfigureAwait(false))
            .Options;
        if (!passwordSignIn.IsAllowed(context.HttpContext, saml.Enabled || pleasanterSso.Enabled))
        {
            return Results.NotFound();
        }

        return await next(context).ConfigureAwait(false);
    }
}
