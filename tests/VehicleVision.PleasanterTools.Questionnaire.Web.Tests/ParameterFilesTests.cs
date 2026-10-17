using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>設定ファイルの読み込み（Issue #158）。</summary>
/// <remarks>
/// **README が書いている優先順位（<c>local.json</c> ＞ 環境変数 ＞ <c>json</c>）**と、
/// **ファイルのキーを正式な名前へ写すこと**を確かめる。
/// </remarks>
public sealed class ParameterFilesTests : IDisposable
{
    /// <summary>試験ごとの置き場。**実際のファイルを置いて読ませる。**</summary>
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "questionnaire-parameters-" + Guid.NewGuid().ToString("N"));

    /// <summary>この試験の間だけ効かせる環境変数。</summary>
    private readonly List<string> variables = [];

    public ParameterFilesTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        foreach (var name in variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // **後片付けが失敗しても試験は落とさない**
        }
    }

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(directory, name), json);

    private void Env(string name, string? value)
    {
        variables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private IConfigurationRoot Build() =>
        new ConfigurationBuilder().AddParameterFiles(directory).Build();

    [Fact]
    public void ファイルが無くても落ちない()
    {
        // **置き場が空でも起動できる。** すべて環境変数で与える構成があるため
        var configuration = Build();

        Assert.Null(configuration["QUESTIONNAIRE_PLEASANTER_BASEURL"]);
    }

    [Fact]
    public void ファイルのキーを正式な名前へ写す()
    {
        Write("Pleasanter.json", """
        {
            "BaseUrl": "https://pleasanter.example.jp",
            "ApiVersion": 1.1,
            "TimeoutSeconds": 45,
            "ApiKeyUserTimeZoneId": "Asia/Tokyo"
        }
        """);
        Write("Service.json", """{ "TimeZoneDefault": "Asia/Tokyo" }""");

        var configuration = Build();

        Assert.Equal("https://pleasanter.example.jp", configuration["QUESTIONNAIRE_PLEASANTER_BASEURL"]);
        Assert.Equal("1.1", configuration["QUESTIONNAIRE_PLEASANTER_APIVERSION"]);
        Assert.Equal("45", configuration["QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS"]);
        Assert.Equal("Asia/Tokyo", configuration["QUESTIONNAIRE_PLEASANTER_TIMEZONE"]);
        Assert.Equal("Asia/Tokyo", configuration[ParameterFiles.TimeZoneDefaultKey]);
    }

    [Fact]
    public void 環境変数はjsonより強い()
    {
        Write("Pleasanter.json", """{ "BaseUrl": "https://from-json.example.jp" }""");
        Env("QUESTIONNAIRE_PLEASANTER_BASEURL", "https://from-env.example.jp");

        Assert.Equal("https://from-env.example.jp", Build()["QUESTIONNAIRE_PLEASANTER_BASEURL"]);
    }

    [Fact]
    public void localjsonは環境変数より強い()
    {
        // **手元で一時的に上書きするための道**（README の優先順位）
        Write("Pleasanter.json", """{ "BaseUrl": "https://from-json.example.jp" }""");
        Write("Pleasanter.local.json", """{ "BaseUrl": "https://from-local.example.jp" }""");
        Env("QUESTIONNAIRE_PLEASANTER_BASEURL", "https://from-env.example.jp");

        Assert.Equal("https://from-local.example.jp", Build()["QUESTIONNAIRE_PLEASANTER_BASEURL"]);
    }

    [Fact]
    public void nullのキーは環境変数を塗り潰さない()
    {
        // ⚠️ **Pleasanter.json の ApiKey は既定で null。**
        // 写してしまうと、環境変数で与えた API キーが消える
        Write("Pleasanter.json", """
        {
            "BaseUrl": "https://pleasanter.example.jp",
            "ApiKey": null
        }
        """);
        Env("QUESTIONNAIRE_PLEASANTER_APIKEY", "secret-api-key");

        Assert.Equal("secret-api-key", Build()["QUESTIONNAIRE_PLEASANTER_APIKEY"]);
    }

    [Fact]
    public void 空のキーも環境変数を塗り潰さない()
    {
        Write("Pleasanter.json", """{ "ApiKey": "" }""");
        Env("QUESTIONNAIRE_PLEASANTER_APIKEY", "secret-api-key");

        Assert.Equal("secret-api-key", Build()["QUESTIONNAIRE_PLEASANTER_APIKEY"]);
    }

    [Fact]
    public void 名前が同じ設定はそのまま読める()
    {
        // Security.json と Analytics.json は環境変数と同じ名前なので、写す必要が無い
        Write("Security.json", """{ "PasswordMinimumLength": 16 }""");
        Write("Analytics.json", """{ "AnalyticsProvider": "Ga4", "AnalyticsSiteId": "G-XXXX" }""");

        var configuration = Build();

        Assert.Equal("16", configuration["PasswordMinimumLength"]);
        Assert.Equal("Ga4", configuration["AnalyticsProvider"]);
        Assert.Equal("G-XXXX", configuration["AnalyticsSiteId"]);
    }

    [Fact]
    public void 読めたファイルを言える()
    {
        // ⚠️ **optional なので、置き場を間違えても黙って既定で動く。**
        // 起動時に「何を読んだか」を出せるようにしてある（Issue #158 の再発防止）
        Write("Service.json", """{ "TimeZoneDefault": "Asia/Tokyo" }""");
        Write("Security.local.json", """{ "PasswordMinimumLength": 20 }""");

        var configuration = Build();

        Assert.Equal(directory, configuration[ParameterFiles.DirectoryKey]);
        Assert.Equal("Service.json / Security.local.json", configuration[ParameterFiles.FoundFilesKey]);
    }

    [Fact]
    public void 何も無ければ空と言える()
    {
        Assert.Empty(Build()[ParameterFiles.FoundFilesKey]!);
    }

    [Fact]
    public void 実際に同梱しているファイルを読める()
    {
        // **repo に入っている実物で確かめる。** 書き方を変えたときに気付けるように
        var repository = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "App_Data", "Parameters"));

        Assert.True(
            File.Exists(Path.Combine(repository, "Pleasanter.json")),
            $"同梱しているはずの設定ファイルが無い: {repository}");

        var configuration = new ConfigurationBuilder().AddParameterFiles(repository).Build();

        // **Service.json の TimeZoneDefault が効くようになったこと**が Issue #158 の主眼
        Assert.Equal("Asia/Tokyo", configuration[ParameterFiles.TimeZoneDefaultKey]);
        Assert.Equal("1.1", configuration["QUESTIONNAIRE_PLEASANTER_APIVERSION"]);
        Assert.Equal("30", configuration["QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS"]);
    }

    [Fact]
    public void 同じ短いキーを別ファイルから混同せずに読む()
    {
        Write("Pleasanter.json", """{ "BaseUrl": "https://pleasanter.example.jp", "TimeoutSeconds": 45 }""");
        Write("Mail.json", """{ "BaseUrl": "https://survey.example.jp", "TimeoutSeconds": 90, "Provider": "Smtp", "Enabled": true }""");
        Write("VirusScan.json", """{ "TimeoutSeconds": 15, "Provider": "ClamAv", "Enabled": false }""");
        Write("Rds.json", """{ "Dbms": "Sqlite", "UserConnectionString": "Data Source=test.db" }""");

        var configuration = Build();

        Assert.Equal("https://pleasanter.example.jp", configuration["QUESTIONNAIRE_PLEASANTER_BASEURL"]);
        Assert.Equal("https://survey.example.jp", configuration["QUESTIONNAIRE_MAIL_BASEURL"]);
        Assert.Equal("45", configuration["QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS"]);
        Assert.Equal("90", configuration["QUESTIONNAIRE_MAIL_TIMEOUT_SECONDS"]);
        Assert.Equal("15", configuration["QUESTIONNAIRE_VIRUSSCAN_TIMEOUTSECONDS"]);
        Assert.Equal("Smtp", configuration["QUESTIONNAIRE_MAIL_TRANSPORT"]);
        Assert.Equal("False", configuration["QUESTIONNAIRE_VIRUSSCAN_ENABLED"]);
        Assert.Equal("Sqlite", configuration["QUESTIONNAIRE_DB_PROVIDER"]);
        Assert.Equal("Data Source=test.db", configuration["QUESTIONNAIRE_DB_CONNECTIONSTRING"]);
    }

    [Fact]
    public void 新しいファイルでもlocalが環境変数より強くnullは上書きしない()
    {
        Write("Mail.json", """{ "SmtpHost": "json.example.jp", "SmtpPassword": null }""");
        Write("Mail.local.json", """{ "SmtpHost": "local.example.jp", "SmtpPassword": null, "Enabled": false }""");
        Env("QUESTIONNAIRE_MAIL_SMTP_HOST", "env.example.jp");
        Env("QUESTIONNAIRE_MAIL_SMTP_PASSWORD", "test-only-secret");
        Env("QUESTIONNAIRE_MAIL_ENABLED", "true");

        var configuration = Build();

        Assert.Equal("local.example.jp", configuration["QUESTIONNAIRE_MAIL_SMTP_HOST"]);
        Assert.Equal("test-only-secret", configuration["QUESTIONNAIRE_MAIL_SMTP_PASSWORD"]);
        Assert.Equal("False", configuration["QUESTIONNAIRE_MAIL_ENABLED"]);
        Assert.Contains("Mail.local.json", configuration[ParameterFiles.FoundFilesKey]);
    }

    [Fact]
    public void ファイル別名でも従来の環境変数名でも全層で指定できる()
    {
        Write("Rds.json", """{ "Dbms": "Sqlite" }""");
        Env("QUESTIONNAIRE_DB_PROVIDER", "SqlServer");
        Assert.Equal("SqlServer", Build()["QUESTIONNAIRE_DB_PROVIDER"]);

        Write("Rds.local.json", """{ "QUESTIONNAIRE_DB_PROVIDER": "PostgreSql" }""");
        Assert.Equal("PostgreSql", Build()["QUESTIONNAIRE_DB_PROVIDER"]);

        Write("Rds.local.json", """{ "Dbms": "MySql", "QUESTIONNAIRE_DB_PROVIDER": "PostgreSql" }""");
        Assert.Equal("MySql", Build()["QUESTIONNAIRE_DB_PROVIDER"]);
    }

    [Fact]
    public void 別名のない設定もファイルから読み込める()
    {
        Write("Security.local.json", """{ "CaptchaSecretKey": "test-only-captcha", "QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN": 25 }""");
        Write("Service.local.json", """{ "PathBase": "/questionnaire", "AdminPath": "/manage" }""");
        var configuration = Build();

        Assert.Equal("test-only-captcha", configuration["CaptchaSecretKey"]);
        Assert.Equal("25", configuration["QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN"]);
        Assert.Equal("/questionnaire", PathBaseOptions.FromConfiguration(configuration).Value);
    }

    [Fact]
    public void 壊れた設定ファイルでは起動を止める()
    {
        Write("Rds.json", """{ "Dbms": "Sqlite" """ );
        Assert.Throws<InvalidDataException>(() => Build());
    }
    [Fact]
    public void メールの実際の設定読取へ用途別ファイルの値が渡る()
    {
        Write("Mail.local.json", """
        {
            "Enabled": true,
            "Provider": "Smtp",
            "SmtpHost": "smtp.example.jp",
            "SmtpPort": 587,
            "SmtpUserName": "survey-user",
            "SmtpPassword": "test-only-secret",
            "FromAddress": "survey@example.jp"
        }
        """);
        var options = Mail.MailOptions.FromConfiguration(Build());

        Assert.True(options.Enabled);
        Assert.Equal("smtp.example.jp", options.Host);
        Assert.Equal(587, options.Port);
        Assert.Equal("survey-user", options.UserName);
        Assert.Equal("test-only-secret", options.Password);
        Assert.Equal("survey@example.jp", options.FromAddress);
    }
}
