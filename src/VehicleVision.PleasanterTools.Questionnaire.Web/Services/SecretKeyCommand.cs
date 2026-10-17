using System.Text;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>DB や Web サーバーを起動せず、運用者用の秘密鍵を生成する。</summary>
public static class SecretKeyCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args is ["--generate-secret-key"])
        {
            output.WriteLine(SecretProtector.GenerateKey());
            return 0;
        }

        if (args is not ["--generate-secret-key", "--output", var path]
            || string.IsNullOrWhiteSpace(path))
        {
            error.WriteLine("Usage: --generate-secret-key [--output <Security.local.json>]");
            return 1;
        }

        try
        {
            var destination = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            // CreateNew により、既存の鍵や他の設定、同時生成されたファイルを上書きしない。
            using (var stream = new FileStream(destination, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                var json = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["SecretKey"] = SecretProtector.GenerateKey(),
                });
                writer.WriteLine(json);
            }

            // ファイルへ保存した鍵は端末・ログへ出さない。
            output.WriteLine("Secret key saved. Keep this file private and back it up. Existing files are never overwritten.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            error.WriteLine("Could not create the secret key file. Check the path and permissions; existing files are never overwritten.");
            return 1;
        }
    }
}
