using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using OtpNet;
using VehicleVision.PleasanterTools.Core.Web.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

internal sealed class AdminContractHost : IAdminContractHost
{
    private readonly string directory = Path.Combine(
        FindRepositoryRoot(), "temp", "20261022_contract-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
    private readonly ContractApplication factory;

    private AdminContractHost(Action<IServiceCollection>? configureServices)
    {
        Directory.CreateDirectory(Path.Combine(directory, "wwwroot"));
        factory = new ContractApplication(directory, configureServices);
    }

    internal IServiceProvider Services => factory.Services;

    public AdminContractCapabilities Capabilities { get; } =
        new(Enum.GetValues<AdminContractOperation>().ToHashSet());

    public AdminContractSettings Settings { get; } = new()
    {
        Cookies = new("q.admin", "q.admin.pending", "q.admin.reenroll"),
        NextProperty = "next",
        TotpNextValue = "totp",
        LoginFailureShape = new Dictionary<string, JsonValueKind> { ["message"] = JsonValueKind.String },
        Endpoints = new Dictionary<AdminContractOperation, AdminContractEndpoint>
        {
            [AdminContractOperation.PasswordLogin] = new(HttpMethod.Post, "/api/admin/login",
                input => new { loginId = input.LoginId, password = input.Password }),
            [AdminContractOperation.TotpLogin] = new(HttpMethod.Post, "/api/admin/login/totp",
                input => new { code = input.Code }),
            [AdminContractOperation.EnrollmentBegin] = new(HttpMethod.Post, "/api/admin/enroll/begin",
                _ => new { }),
            [AdminContractOperation.EnrollmentComplete] = new(HttpMethod.Post, "/api/admin/enroll/complete",
                input => new { code = input.Code }),
            [AdminContractOperation.ReenrollmentBegin] = new(HttpMethod.Post, "/api/admin/me/totp/begin",
                input => input.Password is null ? new { } : (object)new { password = input.Password }),
            [AdminContractOperation.ReenrollmentComplete] = new(HttpMethod.Post, "/api/admin/me/totp/complete",
                input => new { code = input.Code }),
            [AdminContractOperation.Users] = new(HttpMethod.Get, "/api/admin/users", _ => null),
            [AdminContractOperation.ChangePassword] = new(HttpMethod.Post, "/api/admin/me/password",
                input => new { currentPassword = input.Password, newPassword = input.NewPassword }),
            [AdminContractOperation.AuditLogs] = new(HttpMethod.Get, "/api/admin/audit-logs", _ => null),
        },
    };

    public static async Task<AdminContractHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var host = new AdminContractHost(configureServices);
        try
        {
            QuestionnaireDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, host.factory.ConnectionString);
            using var client = host.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                using var ready = await client.GetAsync("/ready", timeout.Token);
                if (ready.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public async Task<SeededAdmin> SeedAdminAsync(bool withTotp, bool disabled = false)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var password = "contract-" + Guid.NewGuid().ToString("N");
        var store = factory.Services.GetRequiredService<IAdminUserStore>();
        var hash = factory.Services.GetRequiredService<PasswordHasher>().Hash(password);
        var user = await store.CreateAsync(loginId, hash, "Administrator", "en");
        Assert.NotNull(user);
        if (withTotp)
        {
            var secret = factory.Services.GetRequiredService<TotpService>().GenerateSecret();
            await store.EnableTotpAsync(user.Id, secret, timeStep: 0, []);
            secrets.Add(loginId, secret);
        }
        if (disabled)
        {
            Assert.Equal(AdminChangeResult.Updated, await store.SetDisabledAsync(user.Id, true));
        }
        return new(loginId, password);
    }

    public async Task<HttpResponseMessage> LoginAsync(HttpClient client, SeededAdmin admin)
    {
        using var request = Settings.Endpoints[AdminContractOperation.PasswordLogin]
            .CreateRequest(new(LoginId: admin.LoginId, Password: admin.Password));
        request.Headers.AcceptLanguage.ParseAdd("en");
        return await client.SendAsync(request);
    }

    // 本体の TotpService は TimeProvider を使わない。未使用の枠を直接登録し、実時刻で数字を作る。
    public string NextTotpCode(SeededAdmin admin) =>
        new Totp(Base32Encoding.ToBytes(secrets[admin.LoginId])).ComputeTotp();

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "VehicleVision.PleasanterTools.Questionnaire.slnx")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("Questionnaire のソリューションが見つかりません。");
    }

    private sealed class ContractApplication : WebApplicationFactory<Program>
    {
        private readonly string directory;
        private readonly Action<IServiceCollection>? configureServices;
        public string ConnectionString { get; }

        public ContractApplication(string directory, Action<IServiceCollection>? configureServices)
        {
            this.directory = directory;
            this.configureServices = configureServices;
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "contracts.db"),
                Pooling = false,
            }.ToString();
            var parameters = Path.Combine(directory, "App_Data", "Parameters");
            Directory.CreateDirectory(parameters);
            // 環境変数より強い専用の設定ファイルで、開発者の DB・資格情報を使わせない。
            File.WriteAllText(Path.Combine(parameters, "General.local.json"),
                JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["QUESTIONNAIRE_DB_PROVIDER"] = "Sqlite",
                    ["QUESTIONNAIRE_DB_CONNECTIONSTRING"] = ConnectionString,
                    ["QUESTIONNAIRE_DB_AUTO_MIGRATE"] = "false",
                    ["QUESTIONNAIRE_DB_SKIP_MIGRATION_CHECK"] = "false",
                    ["QUESTIONNAIRE_SECRET_KEY"] = SecretProtector.GenerateKey(),
                    ["QUESTIONNAIRE_ADMIN_TWOFACTOR"] = "Optional",
                    ["QUESTIONNAIRE_ADMIN_PASSWORD_SIGNIN"] = "true",
                    ["QUESTIONNAIRE_LOGIN_PROOF_OF_WORK"] = "false",
                    ["QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN"] = "1000",
                    ["QUESTIONNAIRE_ADMIN_SESSION_STORE"] = "Database",
                    ["QUESTIONNAIRE_SAML_ENABLED"] = "false",
                    ["QUESTIONNAIRE_PLEASANTERSSO_ENABLED"] = "false",
                    ["QUESTIONNAIRE_ASSET_PATH"] = Path.Combine(directory, "assets"),
                    ["QUESTIONNAIRE_PLEASANTER_BASEURL"] = "https://localhost.invalid",
                    ["QUESTIONNAIRE_PLEASANTER_APIKEY"] = "contract-test-not-a-real-key",
                }));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(directory);
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                Type[] deliveryWorkers =
                [
                    typeof(ResponseSenderHostedService),
                    typeof(AssetHistorySenderHostedService),
                    typeof(MailSenderHostedService),
                    typeof(ResponseNotificationMailerHostedService),
                ];
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType is { } type && deliveryWorkers.Contains(type)).ToArray())
                {
                    services.Remove(service);
                }
                configureServices?.Invoke(services);
            });
        }
    }
}
