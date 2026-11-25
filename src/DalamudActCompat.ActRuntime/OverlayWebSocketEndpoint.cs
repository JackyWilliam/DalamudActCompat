using System.Net;
using System.Net.Sockets;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.WebSocket;

namespace DalamudActCompat.ActRuntime;

/// <summary>
/// Where the OverlayPlugin WebSocket server listens, as saved in the OverlayPlugin config.
/// <see cref="Address"/> is the saved value: an IP address, or "*" for every interface.
/// </summary>
public sealed record OverlayWebSocketEndpoint(string Address, int Port, bool Running)
{
    public const string LoopbackAddress = "127.0.0.1";
    public const string AnyAddress = "*";
    public const int DefaultPort = 10501;

    public bool ListensOnEveryInterface => IsWildcard(Address);

    // A specific non-loopback address is also reachable from other devices.
    public bool ReachableFromNetwork =>
        ListensOnEveryInterface || (IPAddress.TryParse(Address, out var ip) && !IPAddress.IsLoopback(ip));

    // Neither of the two choices the control center offers, so a hand-edited config.
    public bool IsCustomAddress => !ListensOnEveryInterface && Address != LoopbackAddress;

    // A wildcard listener is not an address a page can connect to; pages on this PC reach it
    // through loopback, the same substitution ServerController.GetModernUrl makes.
    public string LocalUrl => BuildUrl(ListensOnEveryInterface ? LoopbackAddress : Address, Port);

    public static string BuildUrl(string host, int port)
        => $"ws://{(host.Contains(':') ? $"[{host}]" : host)}:{port}/ws";

    internal static bool IsWildcard(string? address) => address is AnyAddress or "0.0.0.0";
}

public enum OverlayWebSocketChangeStatus
{
    Applied,
    Unchanged,
    NotRunning,
    InvalidAddress,
    InvalidPort,
    PortInUse,
    StartFailed,
}

public sealed record OverlayWebSocketChange(OverlayWebSocketChangeStatus Status, string? Detail = null)
{
    public bool Succeeded => Status is OverlayWebSocketChangeStatus.Applied or OverlayWebSocketChangeStatus.Unchanged;
}

public static class OverlayWebSocketEndpointSwitch
{
    /// <summary>
    /// Moves a running server to a new endpoint and saves it, or leaves the old endpoint
    /// running when the new one cannot be used.
    /// </summary>
    // DACT treats a WebSocket server that cannot start as a failed parser start. A saved
    // endpoint that does not bind would therefore lock the player out of the parser, and
    // out of this setting with it, so nothing is saved until the server is accepting on it.
    public static OverlayWebSocketChange Apply(ServerController server, IPluginConfig config, string address, int port)
    {
        if (!TryNormalizeAddress(address, out var normalized))
        {
            return new(OverlayWebSocketChangeStatus.InvalidAddress);
        }

        if (port is < 1 or > 65535)
        {
            return new(OverlayWebSocketChangeStatus.InvalidPort);
        }

        var previousAddress = config.WSServerIP;
        var previousPort = config.WSServerPort;
        if (previousAddress == normalized && previousPort == port && server.Running)
        {
            return new(OverlayWebSocketChangeStatus.Unchanged);
        }

        server.Stop();

        // The server binds with address reuse, which on Windows succeeds even while another
        // program is listening on the port; both would then receive connections at random.
        // Only a different port is probed: on the same port the connections just closed can
        // keep an exclusive bind failing for a moment, and that port was ours anyway.
        if (port != previousPort && !CanBindExclusively(normalized, port, out var bindError))
        {
            server.Start();
            return new(OverlayWebSocketChangeStatus.PortInUse, bindError);
        }

        config.WSServerIP = normalized;
        config.WSServerPort = port;
        server.Start();
        if (server.Running)
        {
            config.Save();
            return new(OverlayWebSocketChangeStatus.Applied);
        }

        var failure = server.LastException?.Message;
        config.WSServerIP = previousAddress;
        config.WSServerPort = previousPort;
        server.Start();
        return new(OverlayWebSocketChangeStatus.StartFailed, failure);
    }

    internal static bool TryNormalizeAddress(string? address, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = address?.Trim() ?? string.Empty;
        if (OverlayWebSocketEndpoint.IsWildcard(trimmed))
        {
            normalized = OverlayWebSocketEndpoint.AnyAddress;
            return true;
        }

        if (!IPAddress.TryParse(trimmed, out var ip))
        {
            return false;
        }

        normalized = ip.ToString();
        return true;
    }

    private static bool CanBindExclusively(string address, int port, out string? error)
    {
        var ip = address == OverlayWebSocketEndpoint.AnyAddress ? IPAddress.Any : IPAddress.Parse(address);
        try
        {
            using var probe = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true,
            };
            probe.Bind(new IPEndPoint(ip, port));
            error = null;
            return true;
        }
        catch (SocketException ex)
        {
            error = ex.SocketErrorCode.ToString();
            return false;
        }
    }
}
