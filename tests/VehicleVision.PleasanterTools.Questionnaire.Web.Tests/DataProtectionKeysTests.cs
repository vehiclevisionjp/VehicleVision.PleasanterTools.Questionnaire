using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class DataProtectionKeysTests : IDisposable
{
    private readonly string keysPath = Path.Combine(
        Path.GetTempPath(),
        $"questionnaire-data-protection-{Guid.NewGuid():N}");

    [Fact]
    public void 同じディレクトリとアプリ名なら別プロセス相当でも復号できる()
    {
        using var firstServices = BuildProvider();
        using var secondServices = BuildProvider();
        var first = firstServices.GetRequiredService<IDataProtectionProvider>();
        var second = secondServices.GetRequiredService<IDataProtectionProvider>();

        var protectedValue = first.CreateProtector("admin-cookie").Protect("管理者");
        var plaintext = second.CreateProtector("admin-cookie").Unprotect(protectedValue);

        Assert.Equal("管理者", plaintext);
        Assert.NotEmpty(Directory.GetFiles(keysPath, "*.xml"));
    }

    private ServiceProvider BuildProvider() => new ServiceCollection()
        .AddDataProtection()
        .SetApplicationName(DataProtectionKeys.ApplicationName)
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .Services
        .BuildServiceProvider();

    public void Dispose()
    {
        if (Directory.Exists(keysPath))
        {
            Directory.Delete(keysPath, recursive: true);
        }
    }
}
