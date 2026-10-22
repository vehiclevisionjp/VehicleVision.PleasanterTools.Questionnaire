using VehicleVision.PleasanterTools.Core.Web.Testing;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>Core と同じ C1〜C5 を、本体の HTTP パイプラインと SQLite で実行する。</summary>
public sealed class AdminContractTests : AdminAuthenticationContractTests
{
    protected override async Task<IAdminContractHost> CreateHostAsync() =>
        await AdminContractHost.StartAsync();
}
