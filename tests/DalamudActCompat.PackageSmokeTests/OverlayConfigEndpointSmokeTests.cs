using System.Net;
using System.Reflection;
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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
