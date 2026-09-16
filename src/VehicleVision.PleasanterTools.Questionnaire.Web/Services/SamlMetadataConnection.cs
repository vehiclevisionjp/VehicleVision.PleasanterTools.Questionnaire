using System.Net;
using System.Net.Sockets;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>IdP メタデータの取得先を公開ネットワークへ限定して接続する。</summary>
public static class SamlMetadataConnection
{
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsPublic)
            ?? throw new HttpRequestException("SAML metadata host is not a public address.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(address, context.DnsEndPoint.Port),
                cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>サーバ自身や内部ネットワークへ到達するアドレスを除く。</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && bytes[0] < 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IPv6Loopback.Equals(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast)
        {
            return false;
        }

        var first = address.GetAddressBytes()[0];
        return (first & 0xfe) != 0xfc;
    }
}
