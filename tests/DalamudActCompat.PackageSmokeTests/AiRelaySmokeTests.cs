using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DalamudActCompat.Ai;
using DalamudActCompat.Infrastructure.Logging;

// AI 助手 requests through the --ai-relay Host process, against a loopback stand-in for the
// providers. Accelerators such as NetEase UU redirect the game process's own connections, so the
// point is that requests leave from the relay process: the stand-in looks up which process owns
// each incoming connection. Also covers the provider error wording, the Anthropic SDK going
// through the same transport, cancellation reaching the upstream call, the relay following the
// game's exit, and the direct fallback when no Host executable exists.
internal static class AiRelaySmokeTests
{
    public static async Task RunAsync(string testRoot)
    {
        var executable = Path.Combine(PackagedHostDirectory(), "DalamudActCompat.Host.exe");
        Assert(File.Exists(executable), $"Packaged Host executable is missing: {executable}");
        await using var provider = FakeProvider.Start();
        var previous = AiClient.Relay;
        var relay = new AiRelay(() => executable, Logger());
        AiClient.Relay = relay;
        try
        {
            await RoundTripAsync(provider, relay);
            await ProviderErrorsAsync(provider);
            await CancellationAsync(provider);
            var relayProcess = relay.ProcessId;
            relay.Dispose();
            Assert(relayProcess == 0 || await ExitedAsync(relayProcess), "The AI relay process outlived its owner's Dispose.");
            AiClient.Relay = new AiRelay(() => Path.Combine(testRoot, "missing", "DalamudActCompat.Host.exe"), Logger());
            await DirectFallbackAsync(provider);
        }
        finally
        {
            AiClient.Relay?.Dispose();
            AiClient.Relay = previous;
        }

        await ParentExitAsync(executable);
        Console.WriteLine("AI relay: requests leave from the relay process, 5 MiB round trip, region/permission wording, Claude SDK transport, cancellation, parent exit and direct fallback passed.");
    }

    private static async Task RoundTripAsync(FakeProvider provider, AiRelay relay)
    {
        using var http = new HttpClient(AiClient.Transport, disposeHandler: false);
        var body = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Base + "/echo") { Content = new ByteArrayContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-test");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.SendAsync(request);
        var echo = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert(AiClient.Transport.LastRoute == "relay" && relay.ProcessId is > 0 && relay.ProcessId != Environment.ProcessId,
            "The first AI request did not start and use the relay process.");
        Assert(response.StatusCode == HttpStatusCode.OK &&
               response.Headers.TryGetValues("X-Fake", out var marker) && marker.Single() == "yes" &&
               echo.GetProperty("method").GetString() == "POST" &&
               echo.GetProperty("auth").GetString() == "Bearer sk-test" &&
               echo.GetProperty("type").GetString() == "application/octet-stream" &&
               echo.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(body)),
            "The relay changed the method, headers, body or response of a 5 MiB request.");
        Assert(echo.GetProperty("pid").GetInt32() == relay.ProcessId,
            $"The request reached the provider from process {echo.GetProperty("pid").GetInt32()}, not the relay {relay.ProcessId}.");
    }

    private static async Task ProviderErrorsAsync(FakeProvider provider)
    {
        static AiSettings Compatible(string endpoint) => new() { Provider = AiProviders.Compatible, Endpoint = endpoint };
        static AiSettings Claude(string endpoint) => new() { Provider = AiProviders.Anthropic, Endpoint = endpoint };

        var models = await AiClient.ListModelsAsync(Compatible(provider.Base + "/ok/v1"), "sk-test", CancellationToken.None);
        Assert(models.SequenceEqual(["gpt-test"]), "An OpenAI-compatible model list did not come through the relay.");
        var region = await FailureAsync(() => AiClient.ListModelsAsync(Compatible(provider.Base + "/openai-region/v1"), "sk-test", CancellationToken.None));
        Assert(region == AiClient.RegionRefusal, $"OpenAI's region refusal was worded as: {region}");
        var scoped = await FailureAsync(() => AiClient.ListModelsAsync(Compatible(provider.Base + "/scoped/v1"), "sk-test", CancellationToken.None));
        Assert(scoped.StartsWith("这个 Key 没有权限使用该模型或接口：", StringComparison.Ordinal) && scoped.Contains("insufficient permissions", StringComparison.Ordinal),
            $"A real permission error lost the provider's explanation: {scoped}");

        // The Anthropic SDK gets the same transport through its HttpClient option.
        var claudeModels = await AiClient.ListModelsAsync(Claude(provider.Base + "/claude-ok"), "sk-ant-test", CancellationToken.None);
        Assert(claudeModels.SequenceEqual(["claude-test"]) && AiClient.Transport.LastRoute == "relay",
            "The Claude SDK did not send its request through the relay.");
        var claudeRegion = await FailureAsync(() => AiClient.ListModelsAsync(Claude(provider.Base + "/claude-region"), "sk-ant-test", CancellationToken.None));
        Assert(claudeRegion == AiClient.RegionRefusal, $"Anthropic's region refusal was worded as: {claudeRegion}");
        Assert(provider.Requests.Where(record => record.Path.Contains("claude", StringComparison.Ordinal))
                   .All(record => record.Pid == AiClient.Relay!.ProcessId),
            "A Claude SDK request left from a process other than the relay.");
    }

    private static async Task CancellationAsync(FakeProvider provider)
    {
        using var http = new HttpClient(AiClient.Transport, disposeHandler: false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        try
        {
            await http.GetAsync(provider.Base + "/slow", cancel.Token);
            Assert(false, "A request to a provider that never answers completed.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert(clock.Elapsed < TimeSpan.FromSeconds(5), $"Cancelling took {clock.Elapsed.TotalSeconds:0.0} s.");
        // The relay must drop the upstream call too, or abandoned requests would keep running.
        await provider.SlowAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var after = await http.PostAsync(provider.Base + "/echo", new ByteArrayContent([1, 2, 3]));
        Assert(after.StatusCode == HttpStatusCode.OK && AiClient.Transport.LastRoute == "relay",
            "The relay stopped serving after a cancelled request.");
    }

    private static async Task DirectFallbackAsync(FakeProvider provider)
    {
        using var http = new HttpClient(AiClient.Transport, disposeHandler: false);
        using var response = await http.PostAsync(provider.Base + "/echo", new ByteArrayContent([4, 5, 6]));
        var echo = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert(response.StatusCode == HttpStatusCode.OK && AiClient.Transport.LastRoute == "direct" &&
               echo.GetProperty("pid").GetInt32() == Environment.ProcessId,
            "Without a Host executable the request was not sent directly from the plugin's process.");
    }

    private static async Task ParentExitAsync(string executable)
    {
        // A stand-in for the game: when it exits, the relay must not linger.
        using var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--ai-relay", "--pipe", "DACT-ai-relay-parent-" + Guid.NewGuid().ToString("N"), "--parent", parent.Id.ToString() })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        _ = child.StandardOutput.ReadToEndAsync();
        await Task.Delay(500);
        Assert(!child.HasExited, "The AI relay exited while its parent was alive.");
        parent.Kill();
        Assert(await ExitedAsync(child.Id), "The AI relay kept running after its parent exited.");
    }

    private static async Task<string> FailureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex) when (ex.GetType().Name == "AiServiceException")
        {
            return ex.Message;
        }

        Assert(false, "A provider error did not surface as an AI service failure.");
        return string.Empty;
    }

    private static async Task<bool> ExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static string PackagedHostDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DalamudActCompat.slnx"))) repository = repository.Parent;
        Assert(repository is not null, "Repository was not found.");
        return Path.Combine(repository!.FullName, "src", "DalamudActCompat", "bin", "Release", "host");
    }

    private static PluginLogger Logger() => new(DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed record ProviderRequest(string Method, string Path, int Pid);

    // A tiny HTTP/1.1 server on 127.0.0.1 that answers like OpenAI and Anthropic do. Plain sockets
    // avoid HttpListener's URL reservation, and give access to the caller's port.
    internal sealed class FakeProvider : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private Task? loop;

        public string Base => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        public ConcurrentQueue<ProviderRequest> Requests { get; } = new();

        public TaskCompletionSource SlowAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeProvider Start()
        {
            var provider = new FakeProvider();
            provider.listener.Start();
            provider.loop = provider.AcceptAsync();
            return provider;
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var pid = OwningProcess((IPEndPoint)client.Client.RemoteEndPoint!);
            var head = await ReadHeadAsync(stream);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var path = requestLine[1].Split('?')[0];
            var headers = lines.Skip(1).Where(line => line.Contains(':'))
                .ToDictionary(line => line[..line.IndexOf(':')].Trim(), line => line[(line.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            var body = headers.TryGetValue("Content-Length", out var length) ? new byte[int.Parse(length)] : [];
            await stream.ReadExactlyAsync(body);
            Requests.Enqueue(new ProviderRequest(requestLine[0], path, pid));

            if (path == "/slow")
            {
                // Never answer; report when the caller (the relay) gives up on the connection.
                try { while (await stream.ReadAsync(new byte[1]) > 0) { } }
                catch (IOException) { }
                SlowAborted.TrySetResult();
                return;
            }

            var (status, json, extra) = path switch
            {
                "/echo" => (200, JsonSerializer.Serialize(new
                {
                    method = requestLine[0],
                    auth = headers.GetValueOrDefault("Authorization"),
                    type = headers.GetValueOrDefault("Content-Type"),
                    sha256 = Convert.ToHexString(SHA256.HashData(body)),
                    pid,
                }), "X-Fake: yes\r\n"),
                "/ok/v1/models" => (200, """{"object":"list","data":[{"id":"gpt-test","object":"model"}]}""", ""),
                "/openai-region/v1/models" => (403, """{"error":{"code":"unsupported_country_region_territory","message":"Country, region, or territory not supported","param":null,"type":"request_forbidden"}}""", ""),
                "/scoped/v1/models" => (403, """{"error":{"message":"You have insufficient permissions for this operation.","type":"invalid_request_error"}}""", ""),
                "/claude-ok/v1/models" => (200, """{"data":[{"type":"model","id":"claude-test","display_name":"Claude Test","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"claude-test","last_id":"claude-test"}""", ""),
                "/claude-region/v1/models" => (403, """{"type":"error","error":{"type":"forbidden","message":"Request not allowed"}}""", ""),
                _ => (404, """{"error":{"message":"not found"}}""", ""),
            };
            var payload = Encoding.UTF8.GetBytes(json);
            var reason = status switch { 200 => "OK", 403 => "Forbidden", _ => "Not Found" };
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n{extra}Connection: close\r\n\r\n");
            await stream.WriteAsync(response);
            await stream.WriteAsync(payload);
        }

        private static async Task<string?> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (buffer.Count < 64 * 1024)
            {
                if (await stream.ReadAsync(one) == 0) return null;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                    return Encoding.ASCII.GetString(buffer.ToArray(), 0, buffer.Count - 4);
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            if (loop is not null) await loop;
            stop.Dispose();
        }

        // Which process owns the caller's end of a loopback connection.
        private static int OwningProcess(IPEndPoint caller)
        {
            const int afInet = 2, tcpTableOwnerPidAll = 5;
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, afInet, tcpTableOwnerPidAll, 0);
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, afInet, tcpTableOwnerPidAll, 0) != 0) return 0;
                var count = Marshal.ReadInt32(table);
                var rowSize = Marshal.SizeOf<TcpRow>();
                var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(table + sizeof(int) + index * rowSize);
                    var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    if (row.LocalAddress == loopback && port == caller.Port) return (int)row.OwningPid;
                }

                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRow
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);
    }
}
