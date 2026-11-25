using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using DalamudActCompat.ActRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.WebSocket;

// 悬浮窗 · WebSocket 地址 lets a player open the server to the local network. The socket has no
// authentication, so every device there can connect. Calls that act on the game or on this PC
// (PostNamazu sends game commands) must stay with callers on this PC, while everything on
// this PC, and reading from anywhere, keeps working as before.
internal static class OverlayLocalOnlyHandlerSmokeTests
{
    internal static async Task RunAsync()
    {
        var assembly = typeof(ServerController).Assembly;
        VerifyPeerClassification(assembly);

        using var container = new TinyIoCContainer();
        container.Register<ILogger>(new OverlayServerTestLogger());
        var dispatcherType = assembly.GetType("RainbowMage.OverlayPlugin.EventDispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, container)
                         ?? throw new InvalidOperationException("OverlayPlugin EventDispatcher could not be constructed.");
        container.Register(dispatcherType, dispatcher);
        using var postNamazu = new PostNamazuEventSource(container);
        var commands = new List<string>();
        postNamazu.SetAction((command, payload) =>
        {
            lock (commands) commands.Add(command + " " + payload);
        });
        int Sent()
        {
            lock (commands) return commands.Count;
        }

        var callFrom = dispatcherType.GetMethod("CallHandlerFrom", [typeof(JObject), typeof(bool)])
                       ?? throw new MissingMethodException(dispatcherType.FullName, "CallHandlerFrom");
        JObject Command() => new() { ["call"] = "PostNamazu", ["c"] = "command", ["p"] = "/echo ACTCOMPAT_LOCAL_ONLY", ["rseq"] = 7 };

        Check(callFrom.Invoke(dispatcher, [Command(), true]) is JObject && Sent() == 1,
            "A PostNamazu call from this PC was not dispatched.");
        // The ACT host bridge and embedded overlays look CallHandler up by name; an overload
        // would make that lookup ambiguous and take PostNamazu down for everyone.
        Check(dispatcherType.GetMethods().Count(method => method.Name == "CallHandler") == 1 &&
              dispatcherType.GetMethod("CallHandler")!.Invoke(dispatcher, [Command()]) is JObject && Sent() == 2,
            "The in-process CallHandler entry point changed.");
        try
        {
            callFrom.Invoke(dispatcher, [Command(), false]);
            throw new InvalidOperationException("A PostNamazu call from another device was dispatched.");
        }
        catch (TargetInvocationException refused) when (refused.InnerException is UnauthorizedAccessException)
        {
        }
        Check(Sent() == 2, "A refused PostNamazu call still reached the game.");

        // Reading stays open: a handler that is not marked answers another device as before.
        dispatcherType.GetMethod("RegisterHandler", [typeof(string), typeof(Func<JObject, JToken>)])!
            .Invoke(dispatcher, ["probeRead", (Func<JObject, JToken>)(_ => new JObject { ["ok"] = true })]);
        Check(callFrom.Invoke(dispatcher, [new JObject { ["call"] = "probeRead" }, false]) is JObject { } read && read.Value<bool>("ok"),
            "A reading call was refused to another device.");
        bool LocalOnly(string name) => (bool)dispatcherType.GetMethod("IsLocalOnly")!.Invoke(dispatcher, [name])!;
        Check(LocalOnly("PostNamazu") && !LocalOnly("probeRead"), "Local-only marking does not follow the registration.");

        // The page gets an answer it can match to its request instead of waiting forever.
        var handlerType = assembly.GetType("RainbowMage.OverlayPlugin.Handlers.Handler", throwOnError: true)!;
        var refusal = (JObject)handlerType.GetMethod("Refusal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Command(), "refused"])!;
        Check(refusal.Value<string>("$error") == "refused" && refusal.Value<int>("rseq") == 7,
            "A refused call is not answered with $error and the request's rseq.");

        await VerifyLoopbackSocketAsync(container, Command(), Sent);
        Console.WriteLine("Overlay local-only handlers: PostNamazu runs for in-process callers and a real loopback WebSocket, is refused with $error to another device, and reading calls stay open.");
    }

    private static void VerifyPeerClassification(Assembly assembly)
    {
        var session = assembly.GetType("RainbowMage.OverlayPlugin.WebSocket.OverlaySession", throwOnError: true)!;
        var isLocalPeer = session.GetMethod("IsLocalPeer", BindingFlags.NonPublic | BindingFlags.Static)
                          ?? throw new MissingMethodException(session.FullName, "IsLocalPeer");
        bool Local(string? remote, string? local) => (bool)isLocalPeer.Invoke(null,
        [
            remote is null ? null : new IPEndPoint(IPAddress.Parse(remote), 50123),
            local is null ? null : new IPEndPoint(IPAddress.Parse(local), 10501),
        ])!;

        Check(Local("127.0.0.1", "127.0.0.1") && Local("127.0.0.2", "127.0.0.1") && Local("::1", "::1") &&
              Local("::ffff:127.0.0.1", "::ffff:127.0.0.1"),
            "A loopback peer was treated as another device.");
        // A browser on this PC that opened the PC's own network address.
        Check(Local("192.168.1.20", "192.168.1.20") && Local("::ffff:192.168.1.20", "192.168.1.20"),
            "A peer connecting from the address it connected to was treated as another device.");
        Check(!Local("192.168.1.50", "192.168.1.20") && !Local("::ffff:192.168.1.50", "::ffff:192.168.1.20") &&
              !Local("192.168.1.50", null) && !Local(null, "127.0.0.1") && !Local(null, null),
            "Another device, or an unknown peer, was treated as this PC.");
    }

    // The unit checks above cannot show that a real accepted socket is classified as local.
    // If it were not, every overlay on this PC would lose PostNamazu, so this goes through
    // an actual WebSocket connection on loopback.
    private static async Task VerifyLoopbackSocketAsync(TinyIoCContainer container, JObject command, Func<int> sent)
    {
        var config = new OverlayServerTestConfig
        {
            WSServerIP = "127.0.0.1",
            WSServerPort = OverlayConfigEndpointSmokeTests.FreePort(),
            WSServerRunning = true,
        };
        container.Register<IPluginConfig>(config);
        // Every session resolves the repository; PluginMain registers it in the real plugin.
        var repositoryType = typeof(ServerController).Assembly.GetType("RainbowMage.OverlayPlugin.FFXIVRepository", throwOnError: true)!;
        container.Register(repositoryType, Activator.CreateInstance(repositoryType, container)!);
        var server = new ServerController(container);
        server.Start();
        Check(server.Running, "Local-only handler probe could not start its listener.");
        try
        {
            var before = sent();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{config.WSServerPort}/ws"), timeout.Token);
            await client.SendAsync(
                Encoding.UTF8.GetBytes(command.ToString(Formatting.None)), WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            var buffer = new byte[4096];
            var received = await client.ReceiveAsync(buffer, timeout.Token);
            var reply = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));
            Check(reply["$error"] is null && reply.Value<int>("rseq") == command.Value<int>("rseq") && sent() == before + 1,
                $"A PostNamazu call over a loopback WebSocket was not dispatched: {reply.ToString(Formatting.None)}");
            client.Abort();
        }
        finally
        {
            server.Stop();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
