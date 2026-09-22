using System.Reflection;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>動作中のアプリケーション版を管理画面へ返す入口。</summary>
/// <remarks>
/// <para>
/// **ログイン済みの管理者だけに返す。** 公開の生存確認へ版を載せると、
/// 既知の弱点を狙う手掛かりを認証前に渡してしまうため。
/// </para>
/// <para>
/// **表示用に短くする。** 情報版へ付く完全なコミット ID をそのまま画面へ流さず、
/// 先頭 12 桁だけを返す。
/// </para>
/// </remarks>
public static class AdminVersionEndpoints
{
    private const int CommitLength = 12;

    public static IEndpointRouteBuilder MapAdminVersionEndpoints(
        this IEndpointRouteBuilder builder,
        bool allowInsecure,
        bool usesSqlite = false,
        DatabaseStartupState? startupState = null)
    {
        var group = builder.MapGroup("/api/admin/application")
            .WithTags("管理 API")
            .RequireAuthorization(AdminAuthSchemes.SessionPolicy);

        AdminAuthSchemes.AddNoStore(group);

        group.MapGet("/version", () => Results.Ok(ReadVersion(
            allowInsecure,
            usesSqlite,
            startupState?.MigrationStatus)));

        return builder;
    }

    /// <summary>この Web アセンブリの情報版を、画面へ返す形にする。</summary>
    public static ApplicationVersionResponse ReadVersion(
        bool allowInsecure,
        bool usesSqlite,
        MigrationStatus? migrationStatus) =>
        ToResponse(typeof(AdminVersionEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AdminVersionEndpoints).Assembly.GetName().Version?.ToString()
            ?? string.Empty,
            allowInsecure,
            usesSqlite,
            migrationStatus);

    /// <summary>情報版を表示用の版と短いコミット ID に分ける。</summary>
    public static ApplicationVersionResponse ToResponse(
        string informationalVersion,
        bool allowInsecure = false,
        bool usesSqlite = false,
        MigrationStatus? migrationStatus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationalVersion);

        var parts = informationalVersion.Split('+', 2, StringSplitOptions.TrimEntries);
        var commit = parts.Length == 2 && parts[1].Length >= CommitLength && parts[1].All(Uri.IsHexDigit)
            ? parts[1][..CommitLength].ToLowerInvariant()
            : null;

        return new ApplicationVersionResponse(
            parts[0],
            commit,
            allowInsecure,
            usesSqlite,
            migrationStatus is null
                ? null
                : new DatabaseMigrationStatusResponse(
                    migrationStatus.AppliedVersion,
                    migrationStatus.LatestVersion,
                    migrationStatus.PendingCount,
                    migrationStatus.LastAppliedAt,
                    migrationStatus.AppliedVersion is null ? "none" : "succeeded"));
    }
}

/// <summary>管理画面へ返す動作中のアプリケーション版。</summary>
/// <param name="Version">`Directory.Build.props` の <c>VersionPrefix</c> から作られた版。</param>
/// <param name="Commit">情報版に含まれるコミット ID の先頭 12 桁。含まれない場合は null。</param>
/// <param name="AllowInsecure">閉じたネットワーク向けの HTTP 運用を明示的に許しているか。</param>
/// <param name="UsesSqlite">簡易セットアップ用の SQLite を使っているか。</param>
/// <param name="DatabaseMigration">DB マイグレーションの適用状況。</param>
public sealed record ApplicationVersionResponse(
    string Version,
    string? Commit,
    bool AllowInsecure,
    bool UsesSqlite,
    DatabaseMigrationStatusResponse? DatabaseMigration);

/// <summary>管理画面へ返す DB マイグレーションの適用状況。</summary>
public sealed record DatabaseMigrationStatusResponse(
    long? AppliedVersion,
    long LatestVersion,
    int PendingCount,
    DateTime? LastAppliedAt,
    string LastResult);
