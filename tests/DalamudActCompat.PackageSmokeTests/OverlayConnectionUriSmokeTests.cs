using System.Text.RegularExpressions;
using DalamudActCompat.ActRuntime;

internal static class OverlayConnectionUriSmokeTests
{
    internal static void Run()
    {
        var endpoint = new Uri("ws://127.0.0.1:10501/ws");
        string[] sources =
        [
            "https://act.vps.imrhj.cn/",
            "https://act.vps.imrhj.cn",
            "https://example.invalid/overlay?theme=dark&label=a%26b",
            "https://example.invalid/overlay?theme=dark&",
            "https://act.vps.imrhj.cn/#/",
            "https://souma.diemoe.net/ff14-overlay-vue/#/teamWatch?theme=dark",
            "https://example.invalid/?theme=dark#/teamWatch?label=a%26b",
            "https://example.invalid/?OVERLAY_WS=ws://old:123/ws&theme=dark",
            "https://example.invalid/?HOST_PORT=ws://old:123#/teamWatch?OVERLAY_WS=ws://old:456/ws&label=a%26b",
            "file:///C:/overlay/index.html?theme=dark",
        ];

        foreach (var source in sources)
        {
            foreach (var mode in new[] { OverlayConnectionMode.Auto, OverlayConnectionMode.OverlayPlugin, OverlayConnectionMode.ActWebSocket })
            {
                Check(SelfHostedActRuntime.TryBuildCustomOverlayUri(source, endpoint, mode, out var actual),
                    $"Cannot build overlay URI for {source} ({mode}).");
                // Horizoverlay renders HashRouter before reading location.href. Its old API
                // stops at '&', not '#', so exercise the URL after that real navigation step.
                var href = actual.AbsoluteUri + (string.IsNullOrEmpty(actual.Fragment) ? "#/" : "");
                var modern = Regex.Match(href, @"[?&]OVERLAY_WS=([^&]+)");
                var legacy = Regex.Match(href, @"[?&]HOST_PORT=([^&]+)");
                var isLegacy = mode == OverlayConnectionMode.ActWebSocket;
                var match = isLegacy ? legacy : modern;
                var expected = isLegacy ? "ws://127.0.0.1:10501" : endpoint.AbsoluteUri;
                Check(match.Success && match.Groups[1].Value == expected,
                    $"A page-added hash route contaminated the {mode} connection: {href}");
                Check(!(isLegacy ? modern : legacy).Success,
                    $"The old protocol parameter survived {mode}: {href}");
                Check(Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var socket) && socket.Fragment.Length == 0,
                    $"The generated socket URL has a forbidden fragment: {href}");

                foreach (var option in new[] { "theme=dark", "label=a%26b" })
                    if (source.Contains(option, StringComparison.Ordinal))
                        Check(actual.AbsoluteUri.Contains(option, StringComparison.Ordinal), $"Lost unrelated option {option}.");
                var original = new Uri(source);
                Check(actual.GetLeftPart(UriPartial.Path) == original.GetLeftPart(UriPartial.Path), "The page path changed.");
                Check(actual.Fragment.Split('?')[0] == original.Fragment.Split('?')[0], "The existing hash route changed.");
                Check(Regex.Matches(actual.AbsoluteUri, isLegacy ? "HOST_PORT=" : "OVERLAY_WS=").Count == 1,
                    "Connection retries accumulated duplicate parameters.");
                Check(SelfHostedActRuntime.TryBuildCustomOverlayUri(actual.AbsoluteUri, endpoint, mode, out var retry) &&
                      retry.AbsoluteUri == actual.AbsoluteUri, "Rebuilding the same connection URL is not stable.");
            }

            Check(SelfHostedActRuntime.TryBuildCustomOverlayUri(source, endpoint, OverlayConnectionMode.Original, out var unchanged) &&
                  unchanged.AbsoluteUri == new Uri(source).AbsoluteUri, "Original mode modified the saved URL.");
        }
        Console.WriteLine("Overlay connection URI regression passed: 30 automatic/modern/legacy cases and 10 original-mode cases.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
