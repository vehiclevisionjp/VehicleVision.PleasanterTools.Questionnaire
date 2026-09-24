using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>E2E 試験で使う DB の接続先。</summary>
internal static class E2EDatabase
{
    private const string ProviderEnvironmentVariable = "QUESTIONNAIRE_E2E_PROVIDER";

    private static string Password =>
        Environment.GetEnvironmentVariable("TESTENV_SA_PASSWORD") ?? "Questionnaire#Test1";

    public static DatabaseProvider Provider
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                return DatabaseProvider.SqlServer;
            }

            if (Enum.TryParse<DatabaseProvider>(value, ignoreCase: true, out var provider)
                && provider is DatabaseProvider.SqlServer
                    or DatabaseProvider.PostgreSql
                    or DatabaseProvider.MySql)
            {
                return provider;
            }

            throw new InvalidOperationException(
                $"{ProviderEnvironmentVariable} must be SqlServer, PostgreSql, or MySql: {value}");
        }
    }

    public static string AppConnectionString => Provider switch
    {
        DatabaseProvider.SqlServer =>
            $"Server=localhost,11433;Database=Questionnaire;UID=sa;Password={Password};TrustServerCertificate=True",
        DatabaseProvider.PostgreSql =>
            $"Host=localhost;Port=15432;Database=questionnaire;Username=postgres;Password={Password}",
        DatabaseProvider.MySql =>
            $"Server=localhost;Port=13306;Database=questionnaire;Uid=root;Password={Password}",
        _ => throw new InvalidOperationException($"対応していない RDBMS: {Provider}"),
    };

    public static string InProcessConnectionString => Provider switch
    {
        DatabaseProvider.SqlServer =>
            $"Server=localhost,11433;Database=Questionnaire_E2E;UID=sa;Password={Password};TrustServerCertificate=True",
        DatabaseProvider.PostgreSql =>
            $"Host=localhost;Port=15432;Database=questionnaire_e2e;Username=postgres;Password={Password}",
        DatabaseProvider.MySql =>
            $"Server=localhost;Port=13306;Database=questionnaire_e2e;Uid=root;Password={Password}",
        _ => throw new InvalidOperationException($"対応していない RDBMS: {Provider}"),
    };

    public static DbConnectionFactory AppFactory() =>
        new(Provider, AppConnectionString);

    public static DbConnectionFactory InProcessFactory() =>
        new(Provider, InProcessConnectionString);

    public static string Sql(string sql) => SqlDialect.Format(Provider, sql);
}
