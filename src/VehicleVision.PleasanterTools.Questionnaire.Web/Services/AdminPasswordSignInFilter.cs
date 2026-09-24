namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>SAML 有効時に、許可されていない合言葉ログイン要求を入口で拒否する。</summary>
public sealed class AdminPasswordSignInFilter(
    ISamlOptionsProvider samlProvider,
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
        if (!passwordSignIn.IsAllowed(context.HttpContext, saml.Enabled))
        {
            return Results.NotFound();
        }

        return await next(context).ConfigureAwait(false);
    }
}
