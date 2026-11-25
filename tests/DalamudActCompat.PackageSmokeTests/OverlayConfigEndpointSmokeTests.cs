using System.Net;
using System.Reflection;
using DalamudActCompat.ActRuntime;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.WebSocket;

// Player report (1.0.0.3): "WS: Failed to start: ArgumentNullException (Parameter 'ipString')"
// followed by "Parser initialization failed". An existing OverlayPlugin config without a
// WebSocket address left WSServerIP null, and DACT fails the whole parser when the
// WebSocket server cannot start.
internal static class OverlayConfigEndpointSmokeTests
{
    internal static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "overlay-config-endpoint");
        Directory.CreateDirectory(root);
        using var container = new TinyIoCContainer();
        container.Register<ILogger>(DispatchProxy.Create<ILogger, NoOpPluginLogProxy>());
        var normalize = typeof(ServerController).GetMethod("NormalizeEndpoint", BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new MissingMethodException(nameof(ServerController), "NormalizeEndpoint");

        // Loads through the real PluginConfig so the test covers what a config file on disk
        // actually leaves in memory, then applies the step ServerController runs before binding.
        void Expect(string name, string? main, string? backup, bool repaired, string ip, int port)
        {
            var path = Path.Combine(root, name + ".config.json");
            if (main is not null) File.WriteAllText(path, main);
            if (backup is not null) File.WriteAllText(path + ".backup", backup);
            var config = new PluginConfig(path, container);
            var changed = (bool)normalize.Invoke(null, [config])!;
            Check(changed == repaired && config.WSServerIP == ip && config.WSServerPort == port,
                $"{name}: repaired={changed}, endpoint {config.WSServerIP ?? "<null>"}:{config.WSServerPort}; expected repaired={repaired}, {ip}:{port}.");
            // The exact expression ServerController evaluates next; it must not throw again.
            _ = config.WSServerIP == "*" ? IPAddress.Any : IPAddress.Parse(config.WSServerIP!);
        }

        Expect("fresh", null, null, repaired: false, "127.0.0.1", 10501);
        // The reported case: the file exists, so first-run defaults are skipped, but it
        // carries no endpoint.
        Expect("missing", """{"Overlays":[],"EventSourceConfigs":{}}""", null, repaired: true, "127.0.0.1", 10501);
        Expect("null", """{"WSServerIP":null,"WSServerPort":0,"Overlays":[]}""", null, repaired: true, "127.0.0.1", 10501);
        Expect("garbage", """{"WSServerIP":"localhost:x","WSServerPort":70000,"Overlays":[]}""", null, repaired: true, "127.0.0.1", 10501);
        Expect("corrupt", "{ not json", "also not json", repaired: true, "127.0.0.1", 10501);
        // Deliberate choices survive: LAN sharing and a custom port are user settings.
        Expect("any", """{"WSServerIP":"*","WSServerPort":10501,"Overlays":[]}""", null, repaired: false, "*", 10501);
        Expect("custom", """{"WSServerIP":"192.168.1.20","WSServerPort":12345,"Overlays":[]}""", null, repaired: false, "192.168.1.20", 12345);
        Console.WriteLine("OverlayPlugin WebSocket endpoint: missing/null/unparseable saved values are repaired to 127.0.0.1:10501; wildcard and custom endpoints are kept.");
    }

    // 悬浮窗 · WebSocket 地址: the control center can move the server to another port or
    // open it to the local network. Whatever is entered, the server has to end up accepting
    // and the saved endpoint has to be one that works: DACT fails the whole parser otherwise.
    internal static void RunEndpointSwitch()
    {
        using var container = new TinyIoCContainer();
        var config = new OverlayServerTestConfig { WSServerIP = "127.0.0.1", WSServerPort = FreePort(), WSServerRunning = true };
        container.Register<ILogger>(new OverlayServerTestLogger());
        container.Register<IPluginConfig>(config);
        var server = new ServerController(container);
        server.Start();
        Check(server.Running, "Endpoint switch probe could not start its listener.");
        try
        {
            var original = config.WSServerPort;
            Check(Switch("127.0.0.1", original) == OverlayWebSocketChangeStatus.Unchanged && server.Running,
                "Saving the current endpoint restarted or stopped the server.");
            Check(Switch("localhost", original) == OverlayWebSocketChangeStatus.InvalidAddress &&
                  Switch("127.0.0.1", 70000) == OverlayWebSocketChangeStatus.InvalidPort &&
                  Switch("127.0.0.1", 0) == OverlayWebSocketChangeStatus.InvalidPort &&
                  server.Running && config.WSServerIP == "127.0.0.1" && config.WSServerPort == original,
                "Invalid input changed the running endpoint.");

            var moved = FreePort();
            Check(Switch("127.0.0.1", moved) == OverlayWebSocketChangeStatus.Applied &&
                  server.Running && server.Port == moved && config.WSServerPort == moved &&
                  Listening(IPAddress.Loopback, moved) && !Listening(IPAddress.Loopback, original),
                "The server did not move to the new port.");
            // Same port, different address is the path the LAN switch takes. A second loopback
            // address stands in for "every interface": listening on that from a test binary
            // would raise a Windows Firewall prompt on the machine running the suite.
            var alias = IPAddress.Parse("127.0.0.2");
            Check(Switch("127.0.0.2", moved) == OverlayWebSocketChangeStatus.Applied &&
                  server.Running && config.WSServerIP == "127.0.0.2" && config.WSServerPort == moved && Listening(alias, moved),
                "Changing the listening address on the same port failed.");
            // "0.0.0.0" is the same choice as "*", compared here without binding it.
            config.WSServerIP = "*";
            Check(Switch("0.0.0.0", moved) == OverlayWebSocketChangeStatus.Unchanged, "0.0.0.0 was not treated as the wildcard address.");
            config.WSServerIP = "127.0.0.2";

            using var foreign = new System.Net.Sockets.TcpListener(alias, 0);
            foreign.Start();
            var taken = ((IPEndPoint)foreign.LocalEndpoint).Port;
            Check(Switch("127.0.0.2", taken) == OverlayWebSocketChangeStatus.PortInUse &&
                  server.Running && config.WSServerIP == "127.0.0.2" && config.WSServerPort == moved && Listening(alias, moved),
                "A port another program listens on was accepted, or the refusal left the server down.");
            // 203.0.113.0/24 is reserved for documentation, so no adapter owns it and the bind fails.
            Check(Switch("203.0.113.1", moved) == OverlayWebSocketChangeStatus.StartFailed &&
                  server.Running && config.WSServerIP == "127.0.0.2" && config.WSServerPort == moved && Listening(alias, moved),
                "An address the server cannot bind was saved, or the rollback left the server down.");
        }
        finally
        {
            server.Stop();
        }

        Console.WriteLine("OverlayPlugin WebSocket endpoint switch: port and LAN changes apply live; invalid input, a port in use and an unbindable address keep the previous endpoint running.");

        OverlayWebSocketChangeStatus Switch(string address, int port)
            => OverlayWebSocketEndpointSwitch.Apply(server, config, address, port).Status;
    }

    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // Connecting would make the server build a session, which needs the whole OverlayPlugin
    // container. An exclusive bind tells whether the port is held without touching the server.
    private static bool Listening(IPAddress address, int port)
    {
        using var probe = new System.Net.Sockets.Socket(
            address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true,
        };
        try
        {
            probe.Bind(new IPEndPoint(address, port));
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
