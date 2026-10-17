using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class SecretKeyCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "questionnaire-key-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 既存の標準出力による生成を維持する()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, SecretKeyCommand.Run(["--generate-secret-key"], output, error));
        Assert.Equal(32, SecretProtector.DecodeKey(output.ToString().Trim()).Length);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void 生成したファイルを実際のパラメータ読込で利用できる()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var path = Path.Combine(directory, "Security.local.json");

        Assert.Equal(0, SecretKeyCommand.Run(["--generate-secret-key", "--output", path], output, error));
        var configuration = new ConfigurationBuilder().AddParameterFiles(directory).Build();
        var key = configuration["QUESTIONNAIRE_SECRET_KEY"]!;
        Assert.Equal(32, SecretProtector.DecodeKey(key).Length);
        var protector = new SecretProtector(key);
        Assert.Equal("test", protector.Unprotect(protector.Protect("test")));
        Assert.DoesNotContain(key, output.ToString());
        Assert.Empty(error.ToString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Theory]
    [InlineData("{\"SecretKey\":\"existing-key\"}")]
    [InlineData("{\"PasswordMinimumLength\":16}")]
    public void 既存ファイルは内容にかかわらず変更しない(string original)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Security.local.json");
        File.WriteAllText(path, original);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, SecretKeyCommand.Run(["--generate-secret-key", "--output", path], output, error));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(output.ToString());
        Assert.Contains("never overwritten", error.ToString());
    }

    [Fact]
    public void 出力先を省略した誤指定では鍵を出さない()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, SecretKeyCommand.Run(["--generate-secret-key", "--output"], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString());
    }
}
