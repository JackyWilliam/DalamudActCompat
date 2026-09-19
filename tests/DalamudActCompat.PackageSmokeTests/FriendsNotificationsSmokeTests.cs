using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using NAudio.Wave;
using Newtonsoft.Json;

internal static partial class FriendsUiSmokeTests
{
    private static void NotificationModel()
    {
        var api = new Fake(); var model = new FriendsIncomingNotifications();
        CloudChatMessage Message(long id, bool own = false) => new(id, api.Id,
            new("user", own ? api.Self.Id : api.Peer.Id, "sender"), own ? api.Peer.Id : api.Self.Id, id, Guid.NewGuid(), "消息 " + id, null, "history", DateTimeOffset.UtcNow, null);
        var state = FriendsChatSnapshot.Empty(api.FriendsSession) with { Friends = new([], 0, [], CloudChatPolicy.Notice, api.Self),
            Conversations = ImmutableDictionary<string, FriendConversationView>.Empty.Add(api.Id, new(api.Chat with { History = [Message(10)] }, null, "", true)) };
        Check(!model.Update(state, true, 0), "Initial partial history produced a bubble/sound.");
        state = state with { InitialSyncComplete = true, NotificationBaseline = 10 };
        Check(!model.Update(state, true, 50), "Old unread history replayed at bootstrap.");
        void Put(params CloudChatMessage[] messages) => state = state with { Conversations = state.Conversations.SetItem(api.Id,
            new(api.Chat with { History = messages }, null, "", true)) };
        Put(Message(10), Message(11));
        Check(model.Update(state, true, 100) && model.Visible.Single().Message.Id == 11, "New incoming message was not shown.");
        Check(!model.Update(state, true, 200) && model.Visible.Single().ShownAt == 100, "Refresh restarted a notification.");
        var row = state.Conversations[api.Id];
        state = state with { Conversations = state.Conversations.SetItem(api.Id, row with { Chat = row.Chat with { Pending = [row.Chat.History.Last()] } }) };
        Check(!model.Update(state, true, 300) && model.Visible.Count == 1, "ACK/history/pending duplicated notification.");
        Put(Message(12, true)); Check(!model.Update(state, true, 400), "Own sent/retried message produced a notification.");
        var otherId = Guid.NewGuid().ToString();
        state = state with { Conversations = state.Conversations.Add(otherId, new(api.Chat with { Id = otherId, History = [Message(15) with { ConversationId = otherId }] }, null, "", true)) };
        Check(model.Update(state, true, 500), "Second conversation arrival was lost.");
        Put(Message(13)); Check(model.Update(state, true, 600), "Different conversation's higher ID suppressed a genuine arrival.");
        Check(model.Visible.Select(n => n.Message.Id).SequenceEqual([15L, 13L, 11L]), "Notification order/count is not newest-first and bounded.");
        Put(Enumerable.Range(20, 20).Select(i => Message(i)).ToArray()); model.Update(state, true, 1000);
        Check(model.Visible.Select(n => n.Message.Id).SequenceEqual([39L, 38L, 37L]), "Burst created an unbounded queue or reversed order.");
        Check(model.Visible[0].SlideProgress(1000) == 0 && model.Visible[0].SlideProgress(1220) == 1 &&
            model.Visible[0].Opacity(6600) == .5f, "Slide/fade lifetime is incorrect.");
        model.Update(state, true, 7000); Check(model.Visible.Count == 0, "Expired bubbles remain interactive.");
        Put(Message(40)); Check(model.Update(state, false, 8000) && model.Visible.Count == 0, "Visual mute also swallowed independent sound events.");
        Check(!model.Update(state, true, 8100) && model.Visible.Count == 0, "Re-enabling bubbles replayed muted history.");
        Put(Message(41)); model.Update(state, true, 8200); model.ClearVisible();
        Check(!model.Update(state, true, 8300) && model.Visible.Count == 0, "Hide replayed old action context.");
        state = FriendsChatSnapshot.Empty(new(2, false, null)); model.Update(state, true, 9000);
        Check(model.Visible.Count == 0, "Logout retained private notification data.");
        var quick = Message(42) with { Text = "下把邀我" };
        Check(FriendsIncomingNotifications.Replies(quick).Count == 0 &&
            FriendsIncomingNotifications.Replies(quick with { QuickMessageId = CloudChatPolicy.InviteNext }).Count == 2 &&
            FriendsIncomingNotifications.Replies(quick with { QuickMessageId = CloudChatPolicy.WhenFinished })[0] == "快结束了" &&
            FriendsIncomingNotifications.Replies(quick with { Sender = new("official", null, "DACT"), QuickMessageId = CloudChatPolicy.InviteNext }).Count == 0,
            "Quick replies guessed text, ignored metadata or permitted official replies.");
    }

    private static void NotificationAssetsAndPreferences()
    {
        var defaults = JsonConvert.DeserializeObject<PluginConfiguration>("{}")!;
        Check(defaults.FriendNotificationsEnabled && defaults.FriendNotificationSoundEnabled && !defaults.FriendNotificationsOnRight && defaults.FriendNotificationSound == 0,
            "Old configurations lost notification defaults.");
        defaults.FriendNotificationsEnabled = false; defaults.FriendNotificationSoundEnabled = false; defaults.FriendNotificationsOnRight = true;
        defaults.FriendNotificationBackgroundTransparency = 80; defaults.FriendNotificationSound = 3;
        var saved = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(defaults))!;
        Check(!saved.FriendNotificationsEnabled && !saved.FriendNotificationSoundEnabled && saved.FriendNotificationsOnRight &&
            saved.FriendNotificationBackgroundTransparency == 80 && saved.FriendNotificationSound == 3, "Notification preferences did not survive a cold config roundtrip.");
        var hashes = new HashSet<string>();
        for (var sound = 1; sound <= 4; sound++)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", $"friend-message-{sound}.mp3");
            Check(File.Exists(path), "Notification sound was not packaged: " + sound);
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            using var reader = new Mp3FileReader(path);
            Check(reader.TotalTime.TotalSeconds is > .4 and < 5 && reader.Read(new byte[8192], 0, 8192) > 0, "Packaged MP3 could not decode: " + sound);
        }
        Check(hashes.Count == 4, "Sound selector contains duplicate assets.");
        Console.WriteLine("Friends notifications: bootstrap/ACK/account/mute dedupe, bounded lifetime, quick metadata, local preferences and four decoded audio assets passed (no audio played).");
    }

    private static unsafe void NotificationFrames(Fake api, FriendsChatController controller, NativeUiRasterizer raster, string? output)
    {
        var io = ImGui.GetIO(); io.DisplaySize = new(1440, 1000); io.FontGlobalScale = 1; io.DeltaTime = 1f / 60;
        var context = ImGui.GetCurrentContext(); var configuration = new PluginConfiguration(); long now = 10000; var sounds = 0;
        using var ui = new FriendsUiManager(controller, () => throw new Exception("Notification opened the main drawer."), configuration,
            () => now, () => sounds++, administratorIcon: new AdministratorSmokeTests.PreviewIcon(),
            sponsorIcon: new AdministratorSmokeTests.PreviewIcon(1000));
        Check(SpinWait.SpinUntil(() => controller.Snapshot.InitialSyncComplete, TimeSpan.FromSeconds(3)), "Native notification bootstrap did not finish.");
        void Frame(bool focus = false)
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(new(600, 10)); ImGui.SetNextWindowSize(new(500, 80));
            if (focus) ImGui.SetNextWindowFocus();
            ImGui.Begin("isolated-game-input"); ImGui.TextUnformatted("game controls"); ImGui.End();
            ui.Draw(false, true); ImGui.Render(); now += 17;
        }
        string Name(ImGuiWindowPtr window) => window.Handle == null ? "" : Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
        bool HasNotificationWindows()
        {
            for (var i = 0; i < context.Windows.Size; i++)
                if (context.Windows[i].Active && Name(context.Windows[i]).StartsWith("##DACTIncoming-")) return true;
            return false;
        }
        ImGuiWindowPtr Window(long id, bool expand = false)
        {
            for (var i = 0; i < context.Windows.Size; i++)
                if (context.Windows[i].Active && Name(context.Windows[i]) == $"##DACTIncoming-{api.FriendsSession.Generation}-{id}" + (expand ? "-expand" : "")) return context.Windows[i];
            throw new Exception("Missing native notification window: " + id);
        }
        void Click(Vector2 position)
        {
            io.AddMousePosEvent(position.X, position.Y); Frame();
            io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame(); Frame();
        }
        long Arrive(string text, string? quick = null)
        {
            api.AddIncoming(1, history: true); var last = api.Chat.History.Last() with { Text = text, QuickMessageId = quick };
            api.Chat = api.Chat with { History = api.Chat.History.SkipLast(1).Append(last).ToArray() }; controller.Refresh();
            Check(SpinWait.SpinUntil(() => controller.Snapshot.Conversations[api.Id].Chat.History.Last().Id == last.Id, TimeSpan.FromSeconds(3)), "Native arrival did not sync.");
            return last.Id;
        }
        Frame(true); Frame(); Check(sounds == 0, "Opening UI replayed the initial history sound.");
        var first = Arrive("下把邀我", CloudChatPolicy.InviteNext); Frame();
        Check(Window(first).Pos.X < 0, "Left bubble did not start outside the viewport.");
        for (var i = 0; i < 4; i++) Frame();
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-left-sliding.png"));
        for (var i = 0; i < 14; i++) Frame();
        Check(Math.Abs(Window(first).Pos.X - 8) < 1 && Name(context.NavWindow) == "isolated-game-input", "Left bubble missed its margin or stole focus.");
        Check(sounds == 1 && !io.WantCaptureKeyboard, "Arrival duplicated sound or captured keyboard.");
        io.AddMousePosEvent(Window(first).Pos.X + 30, Window(first).Pos.Y + 20); Frame(); Frame();
        Check(!io.WantCaptureMouse, "Notification text/background intercepted game mouse input.");
        io.AddMousePosEvent(Window(first).Pos.X + Window(first).Size.X + 4, Window(first).Pos.Y + 20); Frame(); Frame();
        Check(!io.WantCaptureMouse, "Gap beside notification intercepted game mouse input.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-quick-reply.png"));
        api.FailSend = true; var bubble = Window(first); var before = api.Attempts.Count;
        Click(bubble.Pos + new Vector2(80, bubble.Size.Y - 20));
        Check(SpinWait.SpinUntil(() => !controller.Snapshot.Busy && api.Attempts.Count == before + 1, TimeSpan.FromSeconds(3)), "Quick reply button did not send.");
        Frame(); var original = api.Attempts.Last();
        Check(original.Text == "好，下把叫你" && original.QuickMessageId is null && Name(context.NavWindow) == "isolated-game-input", "Quick reply changed semantics or took focus.");
        api.FailSend = false; Click(bubble.Pos + new Vector2(55, bubble.Size.Y - 20));
        Check(SpinWait.SpinUntil(() => !controller.Snapshot.Busy && api.Attempts.Count == before + 2, TimeSpan.FromSeconds(3)), "Original quick reply retry did not send.");
        Check(api.Attempts.Last() == original, "Quick retry allocated another operation or body.");
        Frame(); Frame(); Check(sounds == 1 && !io.WantCaptureKeyboard, "Own retry sounded or held keyboard input.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-replied.png"));
        var expand = Window(first, true); Click(expand.Pos + expand.Size / 2);
        Check(Name(context.NavWindow).Contains("DACTFriendChat-" + api.Id), "Expand did not open/focus this exact conversation.");
        Check(SpinWait.SpinUntil(() => { Frame(); return controller.Snapshot.Conversations[api.Id].UnreadCount == 0; }, TimeSpan.FromSeconds(3)),
            "Opening chat from a notification did not clear its unread count.");
        Arrive(string.Join('\n', Enumerable.Repeat("用于上翻聊天记录的较长消息", 32)));
        for (var i = 0; i < 20; i++) Frame();
        ImGuiWindowPtr Messages()
        {
            for (var i = 0; i < context.Windows.Size; i++)
                if (context.Windows[i].Active && Name(context.Windows[i]).Contains("DACTFriendChat-" + api.Id) &&
                    Name(context.Windows[i]).Contains("/messages")) return context.Windows[i];
            throw new Exception("Native chat history child was not found.");
        }
        var historyWindow = Messages();
        Check(historyWindow.ScrollMax.Y > 100, "Chat history did not overflow for the reopen regression.");
        io.AddMousePosEvent(historyWindow.Pos.X + 40, historyWindow.Pos.Y + 50); Frame();
        io.AddMouseWheelEvent(0, 100); for (var i = 0; i < 10; i++) Frame();
        Check(Messages().Scroll.Y < Messages().ScrollMax.Y - 50, "Actual mouse wheel did not scroll into older messages.");
        var reopen = Arrive("请从气泡回到最新消息并清除未读。");
        for (var i = 0; i < 20; i++) Frame();
        Check(controller.Snapshot.Conversations[api.Id].UnreadCount > 0, "Background arrival was marked read while viewing old history.");
        expand = Window(reopen, true); Click(expand.Pos + expand.Size / 2);
        Check(SpinWait.SpinUntil(() => { Frame(); return controller.Snapshot.Conversations[api.Id].UnreadCount == 0; }, TimeSpan.FromSeconds(3)),
            "Reopening a scrolled chat from its notification left unread counts and old scroll position.");
        var chatWindow = Messages().ParentWindow;
        Check((chatWindow.Flags & ImGuiWindowFlags.NoTitleBar) != 0, "Chat retained the native title bar.");
        var originalPosition = chatWindow.Pos; var grab = originalPosition + new Vector2(200, 22); var movement = new Vector2(90, 45);
        io.AddMousePosEvent(grab.X, grab.Y); Frame(); io.AddMouseButtonEvent(0, true); Frame();
        io.AddMousePosEvent(grab.X + movement.X, grab.Y + movement.Y); Frame(); Frame();
        io.AddMouseButtonEvent(0, false); Frame();
        Check(Vector2.Distance(chatWindow.Pos, originalPosition + movement) < 2, "Custom header drag lost mouse movement.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-chat-custom-header.png"));
        Click(chatWindow.Pos + new Vector2(chatWindow.Size.X - 23, 23)); Frame();
        Check(!chatWindow.Active, "Custom header close did not close chat.");
        ui.Hide(); Frame(true); configuration.FriendNotificationsOnRight = true; now += 1500;
        var second = Arrive("你什么时候结束", CloudChatPolicy.WhenFinished); Frame();
        Check(Window(second).Pos.X >= io.DisplaySize.X, "Right bubble did not start outside the right edge.");
        for (var i = 0; i < 4; i++) Frame();
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-right-sliding.png"));
        for (var i = 0; i < 14; i++) Frame();
        var oldY = Window(second).Pos.Y;
        var third = Arrive("这是一条普通消息，没有快捷回复按钮。正文较长时会自动换行；提示只显示有限内容，点击全部展开可以查看完整聊天记录。\nhttps://example.test/abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz");
        Frame(); Check(Window(second).Pos.Y > oldY, "Old bubble did not move downward for the newer message.");
        for (var i = 0; i < 20; i++) Frame();
        Check(Window(third).Pos.Y < Window(second).Pos.Y && Window(third).Pos.X + Window(third).Size.X <= io.DisplaySize.X - 7,
            "Right stack order or safe edge is wrong.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-right-stack.png"));
        configuration.FriendNotificationsOnRight = false; configuration.FriendNotificationBackgroundTransparency = 80; Frame();
        Check(Math.Abs(Window(third).Pos.X - 8) < 1, "Switching sides left stale bubble positions.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-transparent.png"));
        configuration.FriendNotificationsEnabled = false; Frame();
        Check(!HasNotificationWindows(), "Visual mute left invisible hit targets.");
        now += 1500; var soundBeforeMutedArrival = sounds; Arrive("气泡关闭时也可独立响铃"); Frame();
        Check(sounds == soundBeforeMutedArrival + 1, "Visual mute incorrectly muted the independent sound.");
        configuration.FriendNotificationsEnabled = true; Frame();
        Check(!HasNotificationWindows(), "Re-enabling replayed hidden messages.");
        configuration.FriendNotificationSoundEnabled = false; Arrive("只有视觉提示"); Frame();
        Check(sounds == soundBeforeMutedArrival + 1, "Sound mute was ignored.");
        now += 6500; Frame();
        Check(!HasNotificationWindows(), "Expired notification kept an input window.");
        var longMessage = Arrive("这是一条包含多行的通知，用于确认窗口缩放、文字内边距与长消息省略。\nhttps://example.test/abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz");
        foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
        foreach (var right in new[] { false, true })
        {
            io.FontGlobalScale = scale; io.DisplaySize = new(800, 600); configuration.FriendNotificationsOnRight = right;
            for (var i = 0; i < 22; i++) Frame();
            var body = Window(longMessage); var action = Window(longMessage, true);
            foreach (var window in new[] { body, action })
                Check(window.Pos.X >= 0 && window.Pos.Y >= 0 && window.Pos.X + window.Size.X <= 800 && window.Pos.Y + window.Size.Y <= 600,
                    "Scaled notification or its clickable action escaped the viewport.");
            var ink = body.DrawList.VtxBuffer;
            for (var i = 0; i < ink.Size; i++)
            {
                // Body background is translucent; author/body ink stays opaque
                // until lifetime fade and all glyphs retain actual 3px padding.
                if (ink[i].Col >> 24 != 255) continue;
                Check(ink[i].Pos.X >= body.Pos.X + 3 && ink[i].Pos.X <= body.Pos.X + body.Size.X - 3 &&
                    ink[i].Pos.Y >= body.Pos.Y + 3 && ink[i].Pos.Y <= body.Pos.Y + body.Size.Y - 3, "Notification ink violated 3px padding.");
            }
            if (output is not null && scale == 2 && right) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-scale2.png"));
        }
        ui.Hide(); io.DisplaySize = new(1440, 1000); io.FontGlobalScale = 1;
        var previews = new List<int>();
        void SettingsFrame()
        {
            ImGui.NewFrame(); ControlCenterWindow.PushTheme();
            ImGui.SetNextWindowPos(new(320, 120)); ImGui.SetNextWindowSize(new(780, 500));
            ImGui.Begin("来信气泡设置 · 原生验证", ImGuiWindowFlags.NoSavedSettings);
            FriendsNotificationSettings.Draw(configuration, new UiText(configuration), previews.Add);
            ImGui.End(); ControlCenterWindow.PopTheme(); ImGui.Render();
        }
        SettingsFrame(); SettingsFrame(); Check(previews.Count == 0, "Opening sound settings automatically played audio.");
        void SettingsClick(Vector2 position)
        {
            io.AddMousePosEvent(position.X, position.Y); SettingsFrame();
            io.AddMouseButtonEvent(0, true); SettingsFrame(); io.AddMouseButtonEvent(0, false); SettingsFrame(); SettingsFrame();
        }
        SettingsClick(new(400, 360));
        Check(context.OpenPopupStack.Size > 0, "Sound selector did not open.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-sounds.png"));
        var choices = context.NavWindow;
        SettingsClick(choices.Pos + new Vector2(40, choices.Size.Y - 12));
        Check(configuration.FriendNotificationSound == 3 && previews.Count == 0, "Choosing a sound did not save selection or played without Preview.");
        SettingsClick(new(549, 360));
        Check(previews.SequenceEqual([3]), "Explicit preview did not dispatch the selected packaged sound exactly once.");
        SettingsClick(new(400, 243)); choices = context.NavWindow;
        SettingsClick(choices.Pos + new Vector2(35, 15));
        Check(!configuration.FriendNotificationsOnRight, "Position setting did not switch to the left.");
        SettingsClick(new(339, 212)); Check(!configuration.FriendNotificationsEnabled, "Notification checkbox did not disable visuals.");
        SettingsClick(new(339, 212)); SettingsClick(new(339, 329));
        Check(configuration.FriendNotificationsEnabled && configuration.FriendNotificationSoundEnabled, "Visual/sound switches are not independently editable.");
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-notification-settings.png"));
        Console.WriteLine("Friends notifications native: mirrored offscreen slides, stack movement, live quick reply/retry, explicit chat focus, independent mute and lifetime passed.");
    }
}
