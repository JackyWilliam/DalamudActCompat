using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace DalamudActCompat.Protocol;

// HTTP connections that survive a failing system DNS. Players outside China report "No such host
// is known" for the CDNs the CN edition of Triggernometry and DACT's update check download from
// (123pan / 123clouddisk), because their resolver has no answer for them. When the system lookup
// fails, or none of its addresses accept a connection, the host is looked up again through
// Cloudflare (1.1.1.1) and then Google (8.8.8.8) DNS-over-HTTPS. Those are reached by IP, so they
// need no lookup themselves. TLS still validates the requested host name, so a fallback answer can
// only reach the real site. Hosts are sent to them only after the system resolver has failed.
public static class FallbackDnsHttp
{
    // A reachable server answers a TCP connect in well under a second even across the Pacific;
    // a dead CDN edge is given up quickly so the next address (or the DoH answer) still fits
    // inside Triggernometry's own 10 s request timeout.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ResolverTimeout = TimeSpan.FromSeconds(8);

    // Names that only resolve inside a home or company network (RFC 6762 / 8375 and common
    // private zones).
    private static readonly string[] PrivateSuffixes =
        [".local", ".localhost", ".internal", ".intranet", ".lan", ".home", ".corp", ".private", ".home.arpa"];

    // The system lookup; the package smoke probe swaps in a failing one to walk the fallback
    // path on a machine whose own DNS works.
    private static Func<string, CancellationToken, Task<IPAddress[]>> systemLookup = Dns.GetHostAddressesAsync;

    // Cloudflare first, Google second; both speak the JSON flavour of DoH.
    private static readonly string[] Resolvers =
    [
        "https://1.1.1.1/dns-query?name={0}&type={1}",
        "https://8.8.8.8/resolve?name={0}&type={1}",
    ];

    // The resolver client itself must never fall back to itself: it only ever dials IP literals.
    private static readonly HttpClient Resolver = new(new SocketsHttpHandler
    {
        ConnectTimeout = ResolverTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = ResolverTimeout,
    };

    // A drop-in for `new HttpClient()`: the same defaults (system proxy, cookies, redirects),
    // plus the fallback lookup when the connection is made.
    public static HttpClient CreateClient() => new(CreateHandler(), disposeHandler: true);

    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectAsync,
        // Leaves room for the failed system lookup, the DoH round trip and a few addresses.
        ConnectTimeout = TimeSpan.FromSeconds(25),
        // Pooled connections pick up DNS changes instead of living for the whole session.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        if (IPAddress.TryParse(host, out var literal))
        {
            return await ConnectAnyAsync([literal], port, fallbackFollows: false, cancellationToken).ConfigureAwait(false);
        }

        // Machine and LAN names (localhost, OBS on another PC, a NAS under home.arpa) mean nothing
        // to a public resolver and must not be sent to one.
        var publicName = host.Contains('.') &&
                         !PrivateSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        IPAddress[] system = [];
        ExceptionDispatchInfo? failure = null;
        try
        {
            system = await systemLookup(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex) when (publicName)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        if (system.Length > 0)
        {
            try
            {
                return await ConnectAnyAsync(system, port, fallbackFollows: publicName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (publicName && !cancellationToken.IsCancellationRequested &&
                                       ex is OperationCanceledException or SocketException
                                       {
                                           SocketErrorCode: not SocketError.ConnectionRefused,
                                       })
            {
                // A resolver can also answer with addresses that do not lead anywhere. A refused
                // connection is different: the address was right and the port is closed.
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (!publicName)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var fallback = (await ResolveOverHttpsAsync(host, cancellationToken).ConfigureAwait(false))
            .Except(system)
            .ToArray();
        if (fallback.Length == 0)
        {
            // Nothing new to try: report what the system resolver or connection said.
            failure?.Throw();
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return await ConnectAnyAsync(fallback, port, fallbackFollows: false, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IPAddress[]> ResolveOverHttpsAsync(string host, CancellationToken cancellationToken)
    {
        foreach (var type in new[] { "A", "AAAA" })
        {
            foreach (var resolver in Resolvers)
            {
                try
                {
                    var addresses = await QueryAsync(
                        string.Format(resolver, Uri.EscapeDataString(host), type),
                        cancellationToken).ConfigureAwait(false);
                    if (addresses.Length > 0)
                    {
                        return addresses;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or
                                               InvalidOperationException or OperationCanceledException &&
                                           !cancellationToken.IsCancellationRequested)
                {
                    // That resolver is blocked or slow here; try the next one.
                }
            }
        }

        return [];
    }

    private static async Task<IPAddress[]> QueryAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await Resolver.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0 ||
            !root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Answers include the CNAME chain; only the address records are usable.
        return answers.EnumerateArray()
            .Where(answer => answer.TryGetProperty("type", out var kind) && kind.GetInt32() is 1 or 28)
            .Select(answer => answer.TryGetProperty("data", out var data) &&
                              IPAddress.TryParse(data.GetString(), out var address)
                ? address
                : null)
            .OfType<IPAddress>()
            .Distinct()
            .ToArray();
    }

    // fallbackFollows: the DoH lookup still comes after these addresses, so even the last one is
    // given up after AttemptTimeout. Otherwise the last attempt keeps the whole connect budget,
    // because on a lossy link Windows' second SYN retry only answers after about 9 s.
    private static async Task<Stream> ConnectAnyAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        bool fallbackFollows,
        CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? last = null;
        // Random order: 123pan's edges drop connections now and then (seen from outside China
        // while testing), so a retried request should not keep dialling the same dead edge.
        // A few are enough; each dead address costs one attempt timeout.
        var order = addresses.ToArray();
        Random.Shared.Shuffle(order);
        var tried = order.Take(3).ToArray();
        for (var index = 0; index < tried.Length; index++)
        {
            var address = tried[index];
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (fallbackFollows || index < tried.Length - 1)
            {
                attempt.CancelAfter(AttemptTimeout);
            }
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = ExceptionDispatchInfo.Capture(ex);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        last?.Throw();
        throw new SocketException((int)SocketError.HostUnreachable);
    }
}
