using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

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
        app.MapAdminNoteEndpoints();
        app.MapAdminNotificationEndpoints();
        app.MapAdminOutboxEndpoints();
        app.MapAdminSamlEndpoints();
        app.MapAdminSurveyEndpoints();
        app.MapAdminTemplateEndpoints();
        app.MapAdminUserEndpoints();
        app.MapAnalyticsEndpoints();
        app.MapFormEndpoints();

        // **ここで初めて RequestDelegateFactory が走る。**
        // 引数の取り方が通らない宣言は、この列挙で例外になる
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .ToList();

        Assert.NotEmpty(endpoints);
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
