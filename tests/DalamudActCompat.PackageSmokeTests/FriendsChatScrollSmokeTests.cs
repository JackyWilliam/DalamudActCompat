using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static partial class FriendsUiSmokeTests
{
    private static unsafe void ChatScrollFrames(NativeUiRasterizer raster, string? output)
    {
        var api = new Fake { VisualData = true };
        api.AddIncoming(12, history: true);
        using var controller = new FriendsChatController(api, new MemoryDisk(), TimeSpan.FromHours(1));
        using var ui = new FriendsUiManager(controller, () => { }, new PluginConfiguration { FriendNotificationsEnabled = false, FriendNotificationSoundEnabled = false });
        Check(SpinWait.SpinUntil(() => controller.Snapshot.InitialSyncComplete, TimeSpan.FromSeconds(3)), "Scroll fixture did not sync.");
        var io = ImGui.GetIO(); io.DisplaySize = new(1440, 1000); io.FontGlobalScale = 1;
        var context = ImGui.GetCurrentContext();
        DactTheme.SetCurrent(new(), false, 0);
        void Frame()
        {
            ImGui.NewFrame();
            using (DactTheme.PushFrame()) ui.Draw(false, false);
            ImGui.Render();
        }
        void Settle() { for (var i = 0; i < 8; i++) Frame(); }
        Frame();
        typeof(FriendsUiManager).GetMethod("OpenChat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ui, [api.Id]);
        Settle();
        ImGuiWindowPtr History()
        {
            for (var i = 0; i < context.Windows.Size; i++)
            {
                var w = context.Windows[i]; var name = Marshal.PtrToStringUTF8((nint)w.Name) ?? "";
                if (w.Active && name.Contains("DACTFriendChat-" + api.Id) && name.Contains("/messages")) return w;
            }
            throw new Exception("Missing scroll fixture history.");
        }
        void Bottom(string stage) => Check(History().Scroll.Y >= History().ScrollMax.Y - 4, stage + ": latest message is offscreen.");
        void Arrive(string text)
        {
            api.AddIncoming(1, history: true);
            var last = api.Chat.History.Last() with { Text = text };
            api.Chat = api.Chat with { History = api.Chat.History.SkipLast(1).Append(last).TakeLast(20).ToArray() };
            controller.Refresh();
            Check(SpinWait.SpinUntil(() => controller.Snapshot.Conversations[api.Id].Chat.History.Last().Id == last.Id, TimeSpan.FromSeconds(3)), "Scroll arrival did not sync.");
        }
        void WheelUp()
        {
            var w = History(); io.AddMousePosEvent(w.Pos.X + 50, w.Pos.Y + 60); Frame();
            io.AddMouseWheelEvent(0, 3); Settle();
            Check(History().Scroll.Y < History().ScrollMax.Y - 50, "Mouse wheel was overridden by follow-to-latest.");
        }
        void Send(string body, string? quick = null)
        {
            var before = api.Attempts.Count;
            Check(controller.Send(api.Id, body, quick) is not null, "Scroll fixture send was not accepted.");
            Check(SpinWait.SpinUntil(() => !controller.Snapshot.Busy && api.Attempts.Count == before + 1, TimeSpan.FromSeconds(3)), "Scroll fixture send did not finish.");
            Settle();
        }
        try
        {
            Bottom("open overflowing history");
            Arrive(string.Join('\n', Enumerable.Repeat("新消息很长，底部必须在布局完成后可见。", 32)));
            Frame();
            Check(controller.Snapshot.Conversations[api.Id].UnreadCount > 0, "New offscreen tail was marked read using stale scroll extents.");
            Settle(); Bottom("receive long message");
            WheelUp(); var oldY = History().Scroll.Y;
            Arrive("上翻阅读时收到新消息"); Settle();
            Check(Math.Abs(History().Scroll.Y - oldY) < 2 && controller.Snapshot.Conversations[api.Id].UnreadCount > 0,
                "Incoming message moved history or cleared unread while scrolled away.");
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-chat-new-message-jump.png"));
            var w = History();
            io.AddMousePosEvent(w.Pos.X + 120, w.Pos.Y + w.Size.Y + 12); Frame();
            io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame(); Settle();
            Bottom("jump-to-latest click");
            WheelUp(); Send("我发出的消息必须出现在底部"); Bottom("own send from old history");
            WheelUp(); Send("", CloudChatPolicy.InviteNext); Bottom("quick send from old history");
            // The retry footer changes history height twice; both transitions must
            // keep the tail visible without relying on another message arriving.
            api.FailSend = true; Send("发送失败后重试的消息"); Bottom("pending send footer");
            api.FailSend = false; controller.Retry(api.Id);
            Check(SpinWait.SpinUntil(() => !controller.Snapshot.Busy && controller.Snapshot.Conversations[api.Id].PendingSend is null, TimeSpan.FromSeconds(3)), "Scroll retry did not finish.");
            Settle(); Bottom("retry removes footer");
            for (var i = 0; i < 9; i++) { Arrive("保留最近二十条 " + i); Settle(); Bottom("bounded history rollover"); }
            io.FontGlobalScale = 1.4f; Settle(); Bottom("font scale and wrapped rows");
            io.FontGlobalScale = 1; Settle();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-chat-follow-latest.png"));
            Console.WriteLine("Friends chat scroll: long arrivals, deferred read, native wheel/jump, own/quick send, retry footer, bounded history and scale changes passed.");
        }
        finally { ui.Hide(); io.FontGlobalScale = 1; }
    }
}
