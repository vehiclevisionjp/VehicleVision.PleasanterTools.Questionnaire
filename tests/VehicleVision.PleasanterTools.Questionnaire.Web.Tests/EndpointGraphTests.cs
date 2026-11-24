using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>口の宣言そのものが組み立てられるかを見る。</summary>
/// <remarks>
/// <para>
/// ⚠️ **これまで、口の宣言の誤りは起動するまで分からなかった。**
/// 単体テストは口の中身を呼ぶだけで、**最小 API の束ね直し
/// （<c>RequestDelegateFactory</c>）を通らない。** 結合テストは
/// 既に動いているアプリへ HTTP で当てる作りなので、
/// **DB が無い手元では丸ごと飛ばされる。**
/// </para>
/// <para>
/// 実際に、<c>DELETE</c> で本文を推論させる宣言を入れたときに
/// <c>dotnet test</c> が全部通り、**CI の起動確認で初めて落ちた**（Issue #235）。
/// **ここで組み立てておけば、手元の <c>dotnet test</c> で気付ける。**
/// </para>
/// </remarks>
public class EndpointGraphTests
{
    [Fact]
    public void すべての口が組み立てられる()
    {
        var endpoints = BuildEndpoints();

        Assert.NotEmpty(endpoints);
        Assert.Contains(
            endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/admin/saml/settings");
        Assert.Contains(
            endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/admin/saml/settings/test");
        Assert.Contains(
            endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/admin/captcha/challenge");

        var questionImport = endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText
                == "/api/admin/surveys/{surveyId:guid}/question-import/{sourceSurveyId:guid}")
            .ToList();
        Assert.Equal(2, questionImport.Count);
        Assert.All(
            questionImport,
            endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));

        var autoReplyTest = Assert.Single(
            endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/admin/auto-reply/test-send");
        Assert.NotEmpty(autoReplyTest.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            AdminAutoReplyEndpoints.TestSendRateLimitPolicy,
            autoReplyTest.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);

        // **Pleasanter のログインの確認は未認証で呼ばれ、専用の枠で数える**（Issue #464）。
        // ログインの試行の枠を使うと、ログインを待つ間の問い合わせで合言葉のログインまで止まる
        var pleasanterCheck = Assert.Single(
            endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/admin/pleasanter-sso/check");
        Assert.Empty(pleasanterCheck.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            AdminPleasanterSsoEndpoints.CheckRateLimitPolicy,
            pleasanterCheck.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);

        // **API キー（個人トークン）の入口は、すべて認証を要求する**（Issue #1025）。
        // ⚠️ **足し忘れて、認証なしで開く口を作らない。** 発行はパスワードの試行を数える枠にも載せる
        var apiTokenRoutes = endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("api-tokens", StringComparison.Ordinal) == true)
            .ToList();
        Assert.True(apiTokenRoutes.Count >= 6, "API キーの入口が足りない");
        Assert.All(apiTokenRoutes, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
        Assert.Equal(
            AdminAuthSchemes.LoginRateLimitPolicy,
            Assert.Single(
                apiTokenRoutes,
                endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/admin/me/api-tokens"
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
                .Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);

        // **設定の入口は特権管理者の権限を要求する**
        foreach (var route in new[]
                 {
                     "/api/admin/pleasanter-sso/settings",
                     "/api/admin/pleasanter-sso/settings/test",
                 })
        {
            Assert.All(
                endpoints.OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText == route),
                endpoint => Assert.Contains(
                    endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                    data => data.Policy == Services.AdminPermissions.PolicyOf(
                        Services.AdminPermissions.PleasanterSsoSettings)));
        }

        // **テスト回答の削除は専用の権限を要求し、完全削除の権限ではない**（Issue #1024）
        foreach (var route in new[]
                 {
                     "/api/admin/surveys/{surveyId:guid}/test-responses",
                     "/api/admin/surveys/{surveyId:guid}/test-responses/delete",
                 })
        {
            var testResponseEndpoint = Assert.Single(
                endpoints.OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == route);
            Assert.Contains(
                testResponseEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                data => data.Policy == Services.AdminPermissions.PolicyOf(
                    Services.AdminPermissions.SurveysDeleteTestResponses));
        }

        // **CAPTCHA の検証より前にレート制限を通す。**
        // handler の中で検証するため、入口にこの metadata が無い変更を通さない。
        foreach (var route in new[] { "/api/admin/login", "/api/admin/invitations/accept" })
        {
            var endpoint = Assert.Single(
                endpoints.OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == route);
            Assert.NotNull(endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>());
        }

        // **別オリジンからの状態変更を拒まない口は、許可リストの 2 本だけ**（Issue #907）。
        // 口を足したとき、うっかり例外に入れたり、例外の経路が消えたまま残ったりしないように見る
        var unsafeAdminRoutes = endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                AdminCsrfGuard.ProtectedPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? [])
                .Where(method => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
                    && !HttpMethods.IsOptions(method))
                .Select(method => (Method: method, Path: endpoint.RoutePattern.RawText!)))
            .ToList();
        Assert.True(unsafeAdminRoutes.Count > 50);
        Assert.Equal(
            ["/api/admin/saml/acs", "/api/admin/saml/slo"],
            unsafeAdminRoutes
                .Where(route => AdminCsrfGuard.CrossSitePostPaths.Contains(
                    route.Path, StringComparer.OrdinalIgnoreCase))
                .Select(route => route.Path)
                .Order(StringComparer.Ordinal));
        Assert.All(
            AdminCsrfGuard.CrossSitePostPaths,
            path => Assert.Contains(unsafeAdminRoutes, route => route.Method == "POST" && route.Path == path));
    }

    /// <summary>
    /// <b>個人トークン（API キー）で入れる口の一覧を固定する</b>（Issue #1026）。口を足しても、許可宣言（<c>AllowAutomation</c>）が
    /// 無ければトークンでは入れない（既定は拒否）。この一覧が変わるのは、トークンで触れる範囲が変わるとき。レビューで必ず見る。
    /// </summary>
    [Fact]
    public void トークンで入れる口は_宣言した一覧だけ()
    {
        var endpoints = BuildEndpoints().OfType<RouteEndpoint>().ToList();

        var allowed = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<AutomationAccessMetadata>() is not null)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText} [{endpoint.Metadata.GetMetadata<AutomationAccessMetadata>()!.Scope}]"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                "GET /api/admin/surveys/ [surveys.read]",
                "GET /api/admin/surveys/{surveyId:guid} [surveys.read]",
                "GET /api/admin/surveys/{surveyId:guid}/problems [surveys.read]",
                "GET /api/admin/surveys/{surveyId:guid}/versions [surveys.read]",
                "GET /api/admin/surveys/{surveyId:guid}/versions/{version:int} [surveys.read]",
                "POST /api/admin/surveys/{surveyId:guid}/publish [surveys.publish]",
                "POST /api/admin/surveys/{surveyId:guid}/resume [surveys.publish]",
                "POST /api/admin/surveys/{surveyId:guid}/suspend [surveys.publish]",
                "POST /api/admin/surveys/{surveyId:guid}/test-publish [surveys.publish]",
                "POST /api/admin/surveys/{surveyId:guid}/test-republish [surveys.publish]",
                "PUT /api/admin/surveys/{surveyId:guid} [surveys.write]",
            }.Order(StringComparer.Ordinal),
            allowed);

        foreach (var endpoint in endpoints.Where(item => item.Metadata.GetMetadata<AutomationAccessMetadata>() is not null))
        {
            var scope = endpoint.Metadata.GetMetadata<AutomationAccessMetadata>()!.Scope;
            var route = endpoint.RoutePattern.RawText!;

            // **トークンの管理・人・設定の口には宣言しない**（トークンでトークンは作れない）
            Assert.StartsWith("/api/admin/surveys", route, StringComparison.Ordinal);

            // **宣言のスコープは、その口が要求する権限のうち、いちばん口に近いもの**（群のものでなく）と一致する。
            // 口の認可が、宣言と別の権限を要求していると、トークンの判定が食い違う
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(data => data.Policy)
                .ToList();
            Assert.Equal(AdminPermissions.PolicyOf(scope), policies.Last());

            // **要求する権限は、すべてトークンに付けられるスコープ**（users.* などの口に誤って宣言しても開かない）
            Assert.All(
                policies,
                policy => Assert.Contains(
                    AdminPermissions.All.Where(AdminApiTokenScopes.IsAllowed),
                    permission => AdminPermissions.PolicyOf(permission) == policy));
        }
    }

    /// <summary>宣言できるのは、トークンに付けられるスコープだけ。外れていれば起動時に落ちる。</summary>
    [Theory]
    [InlineData("users.write")]
    [InlineData("settings.manage")]
    [InlineData("lookups.manage")]
    [InlineData("surveys.delete")]
    [InlineData("surveys.deleteTestResponses")]
    [InlineData("nonexistent")]
    public void トークンに付けられないスコープは_宣言できない(string scope)
    {
        var app = WebApplication.CreateBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.MapGet("/x", () => "x").AllowAutomation(scope));
    }

    private static List<Endpoint> BuildEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRouting();

        // **本物を組み立てない。** ここで見たいのは引数の取り方だけで、
        // 中身は呼ばない。最小 API は「DI に居るか」で
        // サービスか本文かを決めるので、**居ることだけ分かればよい**
        RegisterEndpointDependencies(builder.Services);

        var app = builder.Build();

        app.MapAdminAuditLogEndpoints();
        app.MapAdminAuthEndpoints();
        app.MapAdminSessionEndpoints();
        app.MapAdminNoteEndpoints();
        app.MapAdminAutoReplyEndpoints();
        app.MapAdminNotificationEndpoints();
        app.MapAdminOutboxEndpoints();
        app.MapAdminSamlEndpoints(new AdminPathOptions(AdminPathOptions.DefaultPath));
        app.MapAdminPleasanterSsoEndpoints();
        app.MapAdminSurveyEndpoints();
        app.MapAdminTemplateEndpoints();
        app.MapAdminUserEndpoints();
        app.MapAdminApiTokenEndpoints();
        app.MapAdminVersionEndpoints(
            allowInsecure: false,
            usesSqlite: true,
            new DatabaseStartupState());
        app.MapAnalyticsEndpoints();
        app.MapFormEndpoints();
        app.MapLookupEndpoints();
        app.MapMonitoringEndpoints(new MonitoringToken("test-monitoring-token"));

        // **ここで初めて RequestDelegateFactory が走る。**
        // 引数の取り方が通らない宣言は、この列挙で例外になる
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .ToList();

        return endpoints;
    }

    /// <summary>口が受け取るサービスを、型だけ DI へ置く。</summary>
    /// <remarks>
    /// ⚠️ **要求の本文で受け取る型を置かないこと。** 置くと、本文として扱われるはずのものが
    /// サービス扱いになり、**この検査が素通りする。**
    /// 本文の型は入れ子の <c>record</c>（<c>…Endpoints.XxxRequest</c>）にそろえてあるので、
    /// **入れ子の型と `Request` で終わる名前を外す。**
    /// </remarks>
    private static void RegisterEndpointDependencies(IServiceCollection services)
    {
        var assemblies = typeof(AdminSurveyEndpoints).Assembly
            .GetReferencedAssemblies()
            .Where(name => name.Name?.StartsWith("VehicleVision.", StringComparison.Ordinal) == true)
            .Select(System.Reflection.Assembly.Load)
            .Append(typeof(AdminSurveyEndpoints).Assembly);

        // **本体が DI へ入れている枠の型。** 自前の組み立て先には出てこない
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddHttpClient();

        var candidates = assemblies
            .SelectMany(TypesOf)
            .Where(type => type.IsInterface
                || (type is { IsClass: true, IsAbstract: false, IsNested: false, IsGenericType: false }
                    && !type.Name.EndsWith("Request", StringComparison.Ordinal)));

        foreach (var type in candidates)
        {
            // **実体は要らない。** 解決されることは無く、居るかどうかだけ見られる
            services.AddSingleton(type, _ => null!);
        }
    }

    private static IEnumerable<Type> TypesOf(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
