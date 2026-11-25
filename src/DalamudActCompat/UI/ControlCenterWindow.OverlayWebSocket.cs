using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 悬浮窗 · WebSocket 地址: the address pages outside DACT (a browser, OBS, another device)
// connect to, and the two things a player may need to change about it. ACT's OverlayPlugin
// has a WSServer tab for this; without an entry here players assumed the server was missing.
public sealed partial class ControlCenterWindow
{
    private const string OverlayWebSocketPopup = "###dact-overlay-websocket";

    // Set by the plugin after construction, like the other optional services. A window built
    // without them (previews, tests) simply does not show the entry.
    public Func<OverlayWebSocketEndpoint?>? GetOverlayWebSocket { get; set; }
    public Func<string, int, Task<OverlayWebSocketChange>>? ApplyOverlayWebSocket { get; set; }

    // The search box and the button in the list panel live on other ID stacks than the
    // dialog, so they only ask; DrawOverlays opens it where BeginDialog will look for it.
    private bool overlayWebSocketOpenRequested;
    private bool overlayWebSocketLanDraft;
    private bool overlayWebSocketLanTouched;
    private string overlayWebSocketPortDraft = string.Empty;
    private string[] overlayWebSocketLanAddresses = [];
    private string? overlayWebSocketCopied;
    private long overlayWebSocketCopiedAt;
    // Written by the worker that applies the change and turned into text on the draw thread:
    // the translation catalog is only read from there.
    private volatile bool overlayWebSocketBusy;
    private volatile bool overlayWebSocketFaulted;
    private volatile OverlayWebSocketChange? overlayWebSocketResult;

    internal void RequestOverlayWebSocketDialog() => overlayWebSocketOpenRequested = true;

    private void OpenOverlayWebSocketDialog()
    {
        var endpoint = GetOverlayWebSocket?.Invoke();
        overlayWebSocketLanDraft = endpoint?.ReachableFromNetwork ?? false;
        overlayWebSocketLanTouched = false;
        overlayWebSocketPortDraft = (endpoint?.Port ?? OverlayWebSocketEndpoint.DefaultPort).ToString(CultureInfo.InvariantCulture);
        overlayWebSocketResult = null;
        overlayWebSocketFaulted = false;
        overlayWebSocketCopied = null;
        // Listing adapters takes a few milliseconds, so once per opening rather than per frame.
        overlayWebSocketLanAddresses = endpoint?.ListensOnEveryInterface == true ? FindLanAddresses() : [];
        DactUi.OpenDialog(OverlayWebSocketPopup);
    }

    private void DrawOverlayWebSocketDialog()
    {
        if (overlayWebSocketOpenRequested)
        {
            overlayWebSocketOpenRequested = false;
            OpenOverlayWebSocketDialog();
        }

        var hostWidth = (DactUi.HostMax.X - DactUi.HostMin.X) / DactUi.S;
        var width = hostWidth > 0 ? Math.Clamp(hostWidth - 48, 320, 480) : 480;
        if (!DactUi.BeginDialog(OverlayWebSocketPopup, width)) return;
        var s = DactUi.S;
        var endpoint = GetOverlayWebSocket?.Invoke();
        var busy = overlayWebSocketBusy;
        if (overlayWebSocketResult is { Succeeded: true })
        {
            overlayWebSocketResult = null;
            DactUi.CloseDialog(OverlayWebSocketPopup);
        }

        DactUi.Text(text.Get("WebSocket 服务", "WebSocket server"), size: DactFonts.H2);
        DactUi.Hint(text.Get(
            "外部浏览器、OBS 或其他设备里的悬浮窗通过这个地址读取战斗数据。在 DACT 里添加的悬浮窗会自动连接，不需要填写。",
            "Overlays in an external browser, OBS or on another device read combat data through this address. Overlays added in DACT connect on their own and need no setup."));

        var dirty = false;
        var address = string.Empty;
        var port = 0;
        if (endpoint is null)
        {
            DactUi.Pill(text.Get("未运行", "Not running"), PillTone.Warn);
            DactUi.Wrapped(text.Get(
                "解析器和 OverlayPlugin 运行后才能查看和修改。",
                "Available once the parser and OverlayPlugin are running."), DactTheme.Palette.Muted);
        }
        else
        {
            DactUi.Pill(endpoint.Running ? text.Get("运行中", "Running") : text.Get("未运行", "Not running"),
                endpoint.Running ? PillTone.Ok : PillTone.Warn);
            DrawOverlayWebSocketUrl("local", endpoint.IsCustomAddress ? text.Get("地址", "Address") : text.Get("本机地址", "Address on this PC"),
                endpoint.LocalUrl);
            if (endpoint.ListensOnEveryInterface)
            {
                for (var i = 0; i < overlayWebSocketLanAddresses.Length; i++)
                    DrawOverlayWebSocketUrl("lan-" + i, text.Get("局域网地址", "Address on the local network"),
                        OverlayWebSocketEndpoint.BuildUrl(overlayWebSocketLanAddresses[i], endpoint.Port));
                if (overlayWebSocketLanAddresses.Length == 0)
                    DactUi.Hint(text.Get(
                        "没有找到这台电脑的局域网地址；请确认它已连接网络。",
                        "No local network address was found for this PC. Check that it is connected to a network."));
            }

            DactUi.Divider();
            DactUi.BeginField(text.Get("端口", "Port"));
            if (DactUi.Input("overlay-websocket-port", ref overlayWebSocketPortDraft, "10501", 6, width: 120 * s, readOnly: busy))
                overlayWebSocketPortDraft = new string(overlayWebSocketPortDraft.Where(char.IsAsciiDigit).Take(5).ToArray());
            DactUi.EndField();
            var lan = overlayWebSocketLanDraft;
            // The socket has no authentication. Calls that act on the game (PostNamazu) are
            // refused to other devices by the server, so the warning is about what they can read.
            if (DactUi.ToggleRow(text.Get("允许局域网内的其他设备连接", "Allow other devices on the local network to connect"), ref lan,
                    text.Get(
                        "开启后，同一网络里的任何设备都能读到你的战斗数据，只在可信的网络里使用。通过 PostNamazu 向游戏发指令仍然只对这台电脑上的悬浮窗开放。Windows 防火墙询问时需要选择允许。",
                        "When on, any device on the same network can read your combat data, so use it only on a network you trust. Sending commands to the game through PostNamazu stays limited to overlays on this PC. Choose Allow if Windows Firewall asks."),
                    disabled: busy))
            {
                overlayWebSocketLanDraft = lan;
                overlayWebSocketLanTouched = true;
            }

            // A hand-edited address is neither of the two choices; it stays until the switch is used.
            if (endpoint.IsCustomAddress && !overlayWebSocketLanTouched)
                DactUi.Hint(text.Format(
                    $"当前监听的是手动设置的地址 {endpoint.Address}；不动上面的开关就会保留。",
                    $"The server listens on a manually set address, {endpoint.Address}. It is kept unless you use the switch above."));

            address = !overlayWebSocketLanTouched ? endpoint.Address
                : overlayWebSocketLanDraft ? OverlayWebSocketEndpoint.AnyAddress : OverlayWebSocketEndpoint.LoopbackAddress;
            var portValid = int.TryParse(overlayWebSocketPortDraft, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                            port is >= 1 and <= 65535;
            dirty = portValid && (port != endpoint.Port || address != endpoint.Address);
            if (!portValid)
                DactUi.Wrapped(DescribeOverlayWebSocketFailure(OverlayWebSocketChangeStatus.InvalidPort), DactUi.Danger, DactFonts.Small);
            else if (overlayWebSocketFaulted)
                DactUi.Wrapped(text.Get(
                    "没能完成保存，请查看解析器状态；详情在调试日志里。",
                    "The change could not be completed. Check the parser status; details are in the debug log."), DactUi.Danger, DactFonts.Small);
            else if (overlayWebSocketResult is { Succeeded: false } failure)
                DactUi.Wrapped(DescribeOverlayWebSocketFailure(failure.Status), DactUi.Danger, DactFonts.Small);
            else if (dirty)
                DactUi.Hint(text.Get(
                    "保存后解析器会重启，已打开的悬浮窗会重新连接。",
                    "Saving restarts the parser, and open overlays reconnect."));
        }

        DactUi.Divider();
        var closeLabel = dirty ? text.Get("取消", "Cancel") : text.Get("关闭", "Close");
        var saveLabel = busy ? text.Get("正在保存…", "Saving…") : text.Get("保存并重启解析器", "Save and restart parser");
        var room = ImGui.GetContentRegionAvail().X;
        var buttons = DactUi.ButtonWidth(closeLabel) + (endpoint is null ? 0 : DactUi.ButtonWidth(saveLabel, ButtonKind.Primary) + 8 * s);
        // Right-aligned on one line when both fit; long translations in a narrow dialog stack instead.
        var oneLine = buttons <= room;
        if (oneLine) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - buttons);
        if (DactUi.Button(closeLabel + "##overlay-websocket-close")) DactUi.CloseDialog(OverlayWebSocketPopup);
        if (endpoint is not null)
        {
            if (oneLine) ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(saveLabel + "##overlay-websocket-save", ButtonKind.Primary, disabled: !dirty || busy))
                StartOverlayWebSocketChange(address, port);
        }
        DactUi.EndDialog(OverlayWebSocketPopup);
    }

    private void DrawOverlayWebSocketUrl(string id, string label, string url)
    {
        var s = DactUi.S;
        var copied = overlayWebSocketCopied == url && Environment.TickCount64 - overlayWebSocketCopiedAt < 2000;
        var copyLabel = text.Get("复制", "Copy");
        var copiedLabel = text.Get("已复制", "Copied");
        // The label flips for two seconds; reserving the wider one keeps the field from jumping.
        var buttonWidth = Math.Max(DactUi.ButtonWidth(copyLabel, icon: "copy"), DactUi.ButtonWidth(copiedLabel, icon: "check"));
        DactUi.BeginField(label);
        var shown = url;
        DactUi.Input("overlay-websocket-url-" + id, ref shown, string.Empty, Math.Max(256, url.Length + 1),
            width: Math.Max(120 * s, ImGui.GetContentRegionAvail().X - buttonWidth - 8 * s), readOnly: true);
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button((copied ? copiedLabel : copyLabel) + "##overlay-websocket-copy-" + id, icon: copied ? "check" : "copy", width: buttonWidth))
        {
            ImGui.SetClipboardText(url);
            overlayWebSocketCopied = url;
            overlayWebSocketCopiedAt = Environment.TickCount64;
        }
        DactUi.EndField();
    }

    private string DescribeOverlayWebSocketFailure(OverlayWebSocketChangeStatus status) => status switch
    {
        OverlayWebSocketChangeStatus.InvalidPort => text.Get(
            "端口需要是 1 到 65535 之间的数字。",
            "The port must be a number from 1 to 65535."),
        OverlayWebSocketChangeStatus.PortInUse => text.Get(
            "这个端口已被其他程序占用，请换一个端口。",
            "That port is in use by another program. Choose a different one."),
        OverlayWebSocketChangeStatus.NotRunning => text.Get(
            "解析器和 OverlayPlugin 运行后才能查看和修改。",
            "Available once the parser and OverlayPlugin are running."),
        _ => text.Get(
            "服务没能按新设置启动，已恢复原来的设置。",
            "The server could not start with the new settings, so the previous ones were restored."),
    };

    private void StartOverlayWebSocketChange(string address, int port)
    {
        if (ApplyOverlayWebSocket is not { } apply || overlayWebSocketBusy) return;
        overlayWebSocketBusy = true;
        overlayWebSocketFaulted = false;
        overlayWebSocketResult = null;
        // Rebinding the socket and restarting the parser must not stall the game's draw thread.
        _ = Task.Run(async () =>
        {
            try
            {
                overlayWebSocketResult = await apply(address, port).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Changing the overlay WebSocket endpoint failed.");
                overlayWebSocketFaulted = true;
            }
            finally
            {
                overlayWebSocketBusy = false;
            }
        });
    }

    // IPv4 addresses another device on the same network can reach. Adapters without a gateway
    // are virtual switches and VPN stubs, which would only be misleading in the list.
    internal static string[] FindLanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                                  adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(adapter => adapter.GetIPProperties())
                .Where(properties => properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                .SelectMany(properties => properties.UnicastAddresses)
                .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                .Select(unicast => unicast.Address.ToString())
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
