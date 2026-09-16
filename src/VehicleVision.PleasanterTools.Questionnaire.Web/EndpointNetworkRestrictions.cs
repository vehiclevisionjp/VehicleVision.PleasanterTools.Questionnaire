using System.Net;

namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>生存確認と監視 API の送信元ネットワークを制限する。</summary>
public sealed class EndpointNetworkRestrictions
{
    /// <summary>生存確認と DB 接続確認を許すネットワークの設定。</summary>
    public const string HealthSetting = "QUESTIONNAIRE_HEALTH_NETWORKS";

    /// <summary>監視 API を許すネットワークの設定。</summary>
    public const string MonitoringSetting = "QUESTIONNAIRE_MONITORING_NETWORKS";

    /// <summary>生存確認の設定を引き継ぐ値。</summary>
    public const string Inherit = "inherit";

    private readonly IReadOnlyList<IPNetwork> healthNetworks;
    private readonly IReadOnlyList<IPNetwork> monitoringNetworks;

    private EndpointNetworkRestrictions(
        IReadOnlyList<IPNetwork> healthNetworks,
        IReadOnlyList<IPNetwork> monitoringNetworks)
    {
        this.healthNetworks = healthNetworks;
        this.monitoringNetworks = monitoringNetworks;
    }

    /// <summary>設定を読み、CIDR でない値があれば起動を止める。</summary>
    public static EndpointNetworkRestrictions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var health = ForwardedProxyNetworks.Parse(
            configuration[HealthSetting],
            HealthSetting);
        var monitoringValue = configuration[MonitoringSetting];
        var monitoring = string.Equals(
            monitoringValue?.Trim(),
            Inherit,
            StringComparison.Ordinal)
            ? health
            : ForwardedProxyNetworks.Parse(monitoringValue, MonitoringSetting);

        return new EndpointNetworkRestrictions(health, monitoring);
    }

    /// <summary>対象経路を今の送信元へ見せてよいか。</summary>
    public bool IsAllowed(PathString path, IPAddress? remoteAddress)
    {
        var networks = IsHealthPath(path)
            ? healthNetworks
            : IsMonitoringPath(path)
                ? monitoringNetworks
                : null;

        if (networks is null || networks.Count == 0)
        {
            return true;
        }

        if (remoteAddress is null)
        {
            return false;
        }

        // リバースプロキシが IPv4 を IPv6 射影で渡しても、IPv4 の CIDR と同じ送信元として扱う。
        var normalized = remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;
        return networks.Any(network => network.Contains(normalized));
    }

    private static bool IsHealthPath(PathString path) =>
        path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);

    private static bool IsMonitoringPath(PathString path) =>
        path.StartsWithSegments(
            "/api/monitoring/status",
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>生存確認と監視 API の送信元を絞るパイプライン。</summary>
public static class EndpointNetworkRestrictionExtensions
{
    /// <summary>許可したネットワーク以外には対象経路の存在を隠す。</summary>
    public static IApplicationBuilder UseEndpointNetworkRestrictions(
        this IApplicationBuilder app,
        EndpointNetworkRestrictions restrictions)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(restrictions);

        return app.Use(async (context, next) =>
        {
            if (!restrictions.IsAllowed(
                    context.Request.Path,
                    context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next().ConfigureAwait(false);
        });
    }
}
