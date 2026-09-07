using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.UI;

internal static class FriendsUiSmokeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> check, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!check())
        {
            try { await Task.Delay(10, timeout.Token); }
            catch (OperationCanceledException) { throw new Exception("Friends UI timed out: " + label); }
        }
    }
    public static async Task RunAsync(string root)
    {
        await DeliveryAndReadAsync();
        await DurableRetryAsync(root);
        await SaveFailureAsync();
        await SwitchAndPreparationAsync();
        await LocalStateAsync(root);
        await PresenceAsync();
        Layout();
        Console.WriteLine("Friends UI: bounded consumption, unread persistence, immutable retry, disk failures, account isolation and layout passed.");
    }
    private static async Task DeliveryAndReadAsync()
    {
        var api = new Fake(); var disk = new MemoryDisk(); api.AddIncoming(3);
        using (var controller = new FriendsChatController(api, disk, TimeSpan.FromHours(1)))
        {
            await Until(() => controller.Snapshot.Friends is not null, "initial list");
            Check(api.Acks == 0 && controller.Snapshot.Conversations.Count == 0, "No consumer silently consumed offline messages.");
            api.BeforeAck = () => Check(controller.Snapshot.Conversations[api.Id].Chat.Pending.Count == 3, "ACK preceded publishing to live UI model.");
            controller.AttachConsumer();
            await Until(() => api.Acks == 1 && controller.Snapshot.Conversations.GetValueOrDefault(api.Id)?.Chat.Pending.Count == 0, "delivery");
            Check(controller.Snapshot.HasUnread && controller.Snapshot.Friends!.OnlineCount == 0, "Unread replaced the online count or delivery implied reading.");
            controller.MarkRead(api.Id, long.MaxValue);
            await Until(() => !controller.Snapshot.HasUnread, "mark read");
            var saves = disk.Saves;
            for (var i = 0; i < 100; i++) controller.MarkRead(api.Id, long.MaxValue);
            controller.Refresh(); await Task.Delay(100);
            Check(disk.Saves == saves, "Already-read chat wrote disk every frame.");
            controller.DetachConsumer(); api.AddIncoming(1); controller.Refresh();
            await Task.Delay(100); Check(api.Acks == 1, "Detached consumer ACKed a message.");
        }
        using var restarted = new FriendsChatController(api, disk, TimeSpan.FromHours(1));
        api.BeforeAck = null; restarted.AttachConsumer();
        await Until(() => restarted.Snapshot.Conversations.GetValueOrDefault(api.Id)?.Chat.Pending.Count == 0, "restart");
        Check(restarted.Snapshot.HasUnread, "New arrival lost unread state across restart.");
        restarted.MarkRead(api.Id, long.MaxValue);
        await Until(() => !restarted.Snapshot.HasUnread, "restart read");
        api.AddIncoming(28, history: true); restarted.Refresh();
        await Until(() => restarted.Snapshot.Conversations[api.Id].Chat.History.Last().Id == api.Chat.History.Last().Id, "bounded update");
        Check(restarted.Snapshot.Conversations[api.Id].Chat.History.Count == 20, "UI accumulated an unbounded transcript.");
    }
    private static async Task DurableRetryAsync(string root)
    {
        var api = new Fake { FailSend = true }; var directory = Path.Combine(root, "friends-durable");
        Guid operation;
        using (var controller = new FriendsChatController(api, new FriendsLocalStateStore(directory), TimeSpan.FromHours(1)))
        {
            controller.AttachConsumer(); await Until(() => controller.Snapshot.Conversations.ContainsKey(api.Id), "outbox ready");
            operation = controller.Send(api.Id, "immutable pending text")!.Value;
            Check(controller.Send(api.Id, "double click") is null, "Concurrent send was accepted.");
            await Until(() => !controller.Snapshot.Busy && api.Attempts.Count == 1, "uncertain send");
            Check(controller.Snapshot.Conversations[api.Id].PendingSend?.OperationId == operation, "Uncertain send lost identity.");
        }
        using (var restarted = new FriendsChatController(api, new FriendsLocalStateStore(directory), TimeSpan.FromHours(1)))
        {
            restarted.AttachConsumer(); await Until(() => restarted.Snapshot.Conversations.ContainsKey(api.Id), "load outbox");
            Check(api.Attempts.Count == 1 && restarted.Snapshot.Conversations[api.Id].PendingSend?.OperationId == operation, "Restart auto-replayed or lost unknown send.");
            api.FailSend = false; restarted.Retry(api.Id);
            await Until(() => !restarted.Snapshot.Busy && api.Attempts.Count == 2, "original retry");
            Check(api.Attempts.First() == api.Attempts.Last() && api.Chat.History.Count == 1, "Retry changed identity/body or duplicated message.");
        }
        // Simulate a server commit followed by a lost HTTP response. Pull must settle
        // the durable operation without resending it, including after restart.
        api.CommitThenFail = true;
        using (var controller = new FriendsChatController(api, new FriendsLocalStateStore(directory), TimeSpan.FromHours(1)))
        {
            controller.AttachConsumer(); await Until(() => controller.Snapshot.Conversations.ContainsKey(api.Id), "lost response ready");
            controller.Send(api.Id, "committed before disconnect");
            await Until(() => !controller.Snapshot.Busy && api.Attempts.Count == 3, "committed response lost");
        }
        using var recovered = new FriendsChatController(api, new FriendsLocalStateStore(directory), TimeSpan.FromHours(1));
        recovered.AttachConsumer(); await Until(() => recovered.Snapshot.Conversations.ContainsKey(api.Id), "commit recovered");
        Check(recovered.Snapshot.Conversations[api.Id].PendingSend is null && api.Attempts.Count == 3 && api.Chat.History.Count == 2,
            "Recovery resent an already committed operation.");
    }
    private static async Task SaveFailureAsync()
    {
        var api = new Fake(); var disk = new MemoryDisk { FailSave = true };
        using var controller = new FriendsChatController(api, disk, TimeSpan.FromHours(1));
        controller.AttachConsumer(); await Until(() => controller.Snapshot.Conversations.ContainsKey(api.Id), "disk failure ready");
        var op = controller.Send(api.Id, "must persist before HTTP");
        await Until(() => !controller.Snapshot.Busy, "failed disk");
        Check(api.Attempts.IsEmpty && controller.Snapshot.Conversations[api.Id].PendingSend?.OperationId == op, "Sent without durable identity or lost retry content.");
        disk.FailSave = false; controller.Retry(api.Id);
        await Until(() => !controller.Snapshot.Busy && api.Attempts.Count == 1, "disk retry");
        Check(api.Attempts.Single().OperationId == op, "Saving retry changed operation.");
        disk.FailOnSave = disk.Saves + 2;
        controller.Send(api.Id, "cleanup failure after success");
        await Until(() => !controller.Snapshot.Busy && api.Attempts.Count == 2, "post-commit disk error");
        Check(controller.Snapshot.Conversations[api.Id].SendStatus.Contains("发送已确认"), "Post-commit disk failure falsely claimed no send.");
    }
    private static async Task SwitchAndPreparationAsync()
    {
        var api = new Fake(); using var controller = new FriendsChatController(api, new MemoryDisk(), TimeSpan.FromHours(1));
        controller.AttachConsumer(); await Until(() => controller.Snapshot.Conversations.ContainsKey(api.Id), "switch ready");
        api.FailGet = true; var op = controller.Send(api.Id, "keep editor draft");
        await Until(() => !controller.Snapshot.Busy, "preparation failure");
        Check(api.Attempts.IsEmpty && controller.Snapshot.Conversations[api.Id].LastPreparedOperation != op, "Failed preparation told UI to erase draft.");
        api.FailGet = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.GetGate = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        controller.Send(api.Id, "old account command"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        api.SwitchAccount();
        Check(controller.Snapshot.Conversations.IsEmpty && controller.Snapshot.Friends is null, "Stale snapshot exposed old account data.");
        release.TrySetResult();
        await Until(() => !controller.Snapshot.Busy && controller.Snapshot.Friends?.User?.Id == api.Self.Id, "new account");
        Check(api.Attempts.IsEmpty, "Old command was dispatched after account switch.");
    }
    private static async Task LocalStateAsync(string root)
    {
        var dir = Path.Combine(root, "friends-encrypted"); var disk = new FriendsLocalStateStore(dir);
        var a = Guid.NewGuid().ToString(); var b = Guid.NewGuid().ToString(); var chat = Guid.NewGuid().ToString();
        var state = FriendsLocalState.Empty(); state.Outbox[chat] = new(1, Guid.NewGuid(), "unique secret body 这是内容"); state.ReadThrough[chat] = 19;
        await disk.LoadAsync(a, default);
        await disk.SaveAsync(a, state, default);
        Check((await disk.LoadAsync(a, default)).Outbox[chat] == state.Outbox[chat], "DPAPI roundtrip lost request.");
        Check((await disk.LoadAsync(b, default)).Outbox.Count == 0, "Different account loaded another outbox.");
        await disk.LoadAsync(a, default);
        var bytes = await File.ReadAllBytesAsync(Directory.GetFiles(dir).Single());
        Check(!Encoding.UTF8.GetString(bytes).Contains("unique secret"), "Local pending message stored plaintext.");
        var invalid = state with { Outbox = new() { [chat] = new(1, Guid.NewGuid(), "text", CloudChatPolicy.InviteNext) } };
        try { await disk.SaveAsync(a, invalid, default); throw new Exception("Invalid outbox persisted."); } catch (InvalidDataException) { }
        Check((await disk.LoadAsync(a, default)).Outbox[chat] == state.Outbox[chat], "Rejected save corrupted durable outbox.");
        var otherDisk = new FriendsLocalStateStore(dir);
        var otherState = await otherDisk.LoadAsync(a, default);
        var secondChat = Guid.NewGuid().ToString(); otherState.Outbox[secondChat] = new(1, Guid.NewGuid(), "other process unknown send");
        await otherDisk.SaveAsync(a, otherState, default);
        state.ReadThrough[chat] = 25; await disk.SaveAsync(a, state, default);
        Check((await otherDisk.LoadAsync(a, default)).Outbox.ContainsKey(secondChat), "Shared profile overwrote another process outbox.");
        state.Outbox[secondChat] = new(1, Guid.NewGuid(), "conflicting local operation");
        try { await disk.SaveAsync(a, state, default); throw new Exception("Conflicting process replaced unknown send."); } catch (IOException) { }
        var backup = new PortableConfigurationBackupService();
        var config = Path.Combine(root, "pluginConfigs", "DalamudActCompat");
        Check(!backup.IsIncludedPath(config, Path.Combine(config, "friends-state", "state.dat")), "Cloud config backup included chat state.");
        await File.WriteAllBytesAsync(Directory.GetFiles(dir).Single(), RandomNumberGenerator.GetBytes(40));
        try { await disk.LoadAsync(a, default); throw new Exception("Corrupt state silently erased unknown sends."); } catch (CryptographicException) { }
    }
    private static void Layout()
    {
        foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
        foreach (var surface in new[] { new Vector2(800, 600), new Vector2(1920, 1080), new Vector2(3840, 2160) })
        foreach (var anchor in new[] { new Vector2(50, 40), surface - new Vector2(650, 460), new Vector2(-50, -40) })
        {
            var (position, size) = FriendsWindowLayout.Drawer(anchor, new(640, 500), Vector2.Zero, surface, scale);
            Check(position.X >= 8 && position.Y >= 8 && position.X + size.X <= surface.X - 8 && position.Y + size.Y <= surface.Y - 8,
                "Drawer escaped the viewport at scaling/edge.");
        }
    }
    private static async Task PresenceAsync()
    {
        var api = new Fake(); using var controller = new FriendsChatController(api, new MemoryDisk(), TimeSpan.FromHours(1));
        controller.AttachConsumer(); await Until(() => controller.Snapshot.Friends?.PresenceSettings is not null, "presence ready");
        Check(controller.UpdatePresence(api.Profile with { Status = "busy", Text = "今晚刷坐骑", ShareDuty = true }), "Presence command rejected.");
        await Until(() => !controller.Snapshot.Busy && controller.Snapshot.Friends?.PresenceSettings?.ShareDuty == true, "presence saved");
        Check(controller.Snapshot.Friends!.PresenceSettings!.Text == "今晚刷坐骑", "Custom status was lost.");
        api.FailPresence = true;
        Check(controller.UpdatePresence(api.Profile with { ShareDuty = false }), "Privacy command rejected.");
        Check(api.DutySuppressed, "Privacy disabled only after HTTP instead of immediately.");
        await Until(() => !controller.Snapshot.Busy && controller.Snapshot.State == "error", "presence failure");
        Check(api.DutySuppressed && api.Profile.ShareDuty, "Uncertain privacy update re-enabled local reporting or claimed server success.");
        api.SwitchAccount(); controller.Refresh();
        await Until(() => controller.Snapshot.Friends?.User?.Id == api.Self.Id, "presence new account");
        Check(controller.Snapshot.Friends!.PresenceSettings == CloudPresenceSettings.Default, "Account switch exposed another profile.");
        var first = new CloudDutyActivity(1, "普通难度"); var second = new CloudDutyActivity(2, "零式难度");
        var duties = new Dictionary<uint, (uint Territory, CloudDutyActivity Duty)> { [1] = (50, first), [2] = (50, second) };
        Check(FriendDutySnapshotProvider.Resolve(true, true, false, 50, 2, duties) == second, "Same-territory duty variant was guessed.");
        foreach (var state in new[] { (false, true, false, 50u, 2u), (true, false, false, 50u, 2u), (true, true, true, 50u, 2u),
            (true, true, false, 51u, 2u), (true, true, false, 50u, 0u) })
            Check(FriendDutySnapshotProvider.Resolve(state.Item1, state.Item2, state.Item3, state.Item4, state.Item5, duties) is null,
                "Logout/loading/field/unknown content exposed a duty.");
    }
    public static async Task RunNativeAsync()
    {
        var api = new Fake { VisualData = true, Self = new("native-self", "Raynor") }; var disk = new MemoryDisk();
        using var controller = new FriendsChatController(api, disk, TimeSpan.FromHours(1));
        using var ui = new FriendsUiManager(controller, () => { });
        await Until(() => controller.Snapshot.Conversations.ContainsKey(api.Id), "native model");
        NativeFrames(api, controller, ui);
        Console.WriteLine("Friends UI: real cimgui draw, viewport/scaling, explicit chat focus and combat notification focus passed (outside the game).");
    }
    private static unsafe void NativeFrames(Fake api, FriendsChatController controller, FriendsUiManager ui)
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") ?? throw new Exception("Set DACT_TEST_CIMGUI to the installed cimgui.dll.");
        // Dalamud's generated binding resolves beside the test assembly.
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), overwrite: true);
        NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(1920, 1080); io.DeltaTime = 1f / 60;
            // The common Chinese preset omits some product words and U+25CF.
            // Include the real font's CJK/punctuation/symbol ranges for evidence.
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x2000, 0x30ff, 0x31f0, 0x31ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Native test font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
            var anchor = new Vector2(60, 120); var mainSize = new Vector2(920, 720);
            var drag = new WindowDragController(); var texture = new EmptyTexture();
            void Frame(bool focusGame = false)
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(10, 10)); ImGui.SetNextWindowSize(new(500, 80));
                if (focusGame) ImGui.SetNextWindowFocus();
                ImGui.Begin("isolated-game-input"); ImGui.TextUnformatted("game controls"); ImGui.End();
                ImGui.SetNextWindowPos(anchor); ImGui.SetNextWindowSize(mainSize);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.045f, .064f, .09f, .98f));
                ImGui.Begin("isolated-dact-header", ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDecoration);
                ui.SetAnchor(ImGui.GetWindowPos(), ImGui.GetWindowSize(), 1, ImGuiP.GetCurrentWindow().ID);
                BrandedWindowChrome.Draw(drag, texture, "主页", "运行中", Vector4.One, "0.4.0.4", "friend-native",
                    helpAction: () => { }, statusAction: () => { }, statusLabel: "● 云同步", friendsAction: () => ui.ToggleDrawer(), onlineFriends: 2, friendsUnread: true);
                ImGui.SetCursorPos(new(28, 110)); ImGui.TextColored(new Vector4(.42f, .78f, .96f, 1), "DACT · 原生界面验证");
                ImGui.SetCursorPos(new(28, 146)); ImGui.TextUnformatted("主窗口占位内容；右侧好友抽屉、图标和聊天使用实际插件代码绘制。");
                ImGui.End(); ImGui.PopStyleColor();
                ui.Draw(true, true); ImGui.Render();
                Check(ImGui.GetDrawData().TotalVtxCount > 0, "Native UI emitted no draw data.");
            }
            string Focus() => Marshal.PtrToStringUTF8((nint)context.NavWindow.Name) ?? "";
            Frame(true); ui.ToggleDrawer(); Frame(); Frame();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-drawer-opening.png"));
            for (var i = 0; i < 10; i++) Frame();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-drawer-expanded.png"));
            Check(Focus() == "isolated-game-input", "Opening a no-focus drawer stole game focus.");
            typeof(FriendsUiManager).GetMethod("OpenChat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ui, [api.Id]);
            Frame(); Frame(); Check(Focus().Contains("DACTFriendChat"), "Explicit chat opening did not focus the chat.");
            Frame(true);
            api.AddIncoming(1, history: true); controller.Refresh();
            Check(SpinWait.SpinUntil(() => controller.Snapshot.HasUnread, TimeSpan.FromSeconds(3)), "Native notification never reached model.");
            Frame(); Check(Focus() == "isolated-game-input", "Combat arrival stole game input focus.");
            foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
            foreach (var surface in new[] { new Vector2(800, 600), new Vector2(1920, 1080) })
            {
                io.FontGlobalScale = scale; io.DisplaySize = surface;
                anchor = surface - new Vector2(660, 560); mainSize = new(640, 550);
                for (var i = 0; i < 12; i++) Frame();
                var drawerFound = false;
                for (var i = 0; i < context.Windows.Size; i++)
                {
                    var window = context.Windows[i]; var name = Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
                    if (!name.Contains("##DACTFriendsDrawer") && !name.Contains("###DACTFriendChat-")) continue;
                    if (name == "##DACTFriendsDrawer") drawerFound = true;
                    Check(window.Pos.X >= 0 && window.Pos.Y >= 0 && window.Pos.X + window.Size.X <= surface.X && window.Pos.Y + window.Size.Y <= surface.Y,
                        "Native friend window escaped viewport: " + name);
                }
                Check(drawerFound, "Native drawer disappeared during scale/viewport checks.");
                if (output is not null && scale == 1 && surface.X == 800)
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-drawer-edge-with-chat.png"));
            }
            BubbleBounds(raster, output);
        }
        finally { ImGui.DestroyContext(context); }
    }
    private static void BubbleBounds(NativeUiRasterizer raster, string? output)
    {
        var method = typeof(FriendsUiManager).GetMethod("DrawBubble", BindingFlags.NonPublic | BindingFlags.Static)!;
        var io = ImGui.GetIO(); io.DisplaySize = new(1440, 1500);
        foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
        {
            io.FontGlobalScale = scale;
            ImGui.NewFrame(); ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(680, 1400));
            ImGui.Begin("原生聊天气泡内边距验证", ImGuiWindowFlags.NoSavedSettings);
            foreach (var kind in new[] { "peer", "own", "official" })
            {
                var message = new CloudChatMessage(1, "native", new(kind == "official" ? "official" : "user", "user", "旅行者 · 多行消息"), "peer", 1, Guid.NewGuid(),
                    "今晚刷坐骑，这是一条会自动换行的中文消息，用于验证每一行文字与气泡左右边缘都有足够距离。\nSecond line with a long unbroken URL: https://example.test/abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789", null, "history", DateTimeOffset.Now, null);
                var list = ImGui.GetWindowDrawList(); var before = list.VtxBuffer.Size;
                method.Invoke(null, [message, kind == "own"]);
                var after = list.VtxBuffer.Size;
                var textColors = new[] { ImGui.GetColorU32(Vector4.One), ImGui.GetColorU32(new Vector4(.42f, .78f, .96f, 1)), ImGui.GetColorU32(new Vector4(.62f, .69f, .75f, 1)) };
                // Actual emitted ink vertices must remain inside the emitted bubble
                // geometry. This catches cursor/word-wrap regressions, not only a constant.
                var background = ImGui.GetColorU32(kind == "own" ? new Vector4(.09f, .23f, .31f, 1) : new Vector4(.09f, .115f, .15f, 1));
                var left = float.MaxValue; var right = float.MinValue;
                for (var i = before; i < after; i++)
                    if (list.VtxBuffer[i].Col == background) { left = Math.Min(left, list.VtxBuffer[i].Pos.X); right = Math.Max(right, list.VtxBuffer[i].Pos.X); }
                var count = 0;
                for (var i = before; i < after; i++)
                {
                    var v = list.VtxBuffer[i]; if (!textColors.Contains(v.Col)) continue; count++;
                    Check(v.Pos.X >= left + 3 && v.Pos.X <= right - 3, $"{kind} text exceeded 3px padding at scale {scale}: {v.Pos.X} in {left}..{right}");
                }
                Check(count > 0, "Bubble padding test saw no actual text vertices.");
            }
            ImGui.End(); ImGui.Render();
            if (output is not null && scale == 1) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "friends-bubble-padding.png"));
        }
    }
    private sealed class EmptyTexture : ISharedImmediateTexture, IDalamudTextureWrap
    {
        public ImTextureID Handle => default;
        public int Width => 1;
        public int Height => 1;
        public Vector2 Size => Vector2.One;
        public IDalamudTextureWrap GetWrapOrEmpty() => this;
        public IDalamudTextureWrap GetWrapOrDefault(IDalamudTextureWrap? fallback = null) => this;
        public bool TryGetWrap([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDalamudTextureWrap? wrap, out Exception? exception) { wrap = this; exception = null; return true; }
        public Task<IDalamudTextureWrap> RentAsync(CancellationToken ct = default) => Task.FromResult<IDalamudTextureWrap>(this);
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => this;
        public void Dispose() { }
    }
    private sealed class MemoryDisk : IFriendsLocalStateStore
    {
        private readonly Dictionary<string, FriendsLocalState> states = new();
        public int Saves; public bool FailSave; public int FailOnSave;
        private static FriendsLocalState Clone(FriendsLocalState s) => new(new(s.Outbox), new(s.ReadThrough));
        public Task<FriendsLocalState> LoadAsync(string id, CancellationToken ct) => Task.FromResult(Clone(states.GetValueOrDefault(id) ?? FriendsLocalState.Empty()));
        public Task SaveAsync(string id, FriendsLocalState s, CancellationToken ct)
        {
            Saves++;
            if (FailSave || Saves == FailOnSave) throw new IOException("isolated save failure");
            states[id] = Clone(s); return Task.CompletedTask;
        }
    }
    private sealed class Fake : ICloudFriendsSession
    {
        public CloudApiUser Self = new(Guid.NewGuid().ToString(), "isolated_a");
        public readonly CloudApiUser Peer = new(Guid.NewGuid().ToString(), "isolated_b");
        public readonly string Id = Guid.NewGuid().ToString();
        public CloudFriendsSession FriendsSession { get; private set; } = new(1, true, "isolated_a");
        public CloudChatConversation Chat;
        public readonly ConcurrentQueue<CloudChatSendRequest> Attempts = new();
        public bool FailSend, CommitThenFail, FailGet, FailPresence, DutySuppressed, VisualData;
        public CloudPresenceSettings Profile = CloudPresenceSettings.Default;
        public Func<CancellationToken, Task>? GetGate; public Action? BeforeAck;
        public int Acks; private long nextMessage;
        public Fake() => Chat = new(Id, "friend", 0, Peer, 0, 0, 0, [], [], 1, CloudChatPolicy.Notice);
        public void SwitchAccount() { Self = new(Guid.NewGuid().ToString(), "isolated_c"); FriendsSession = new(2, true, Self.Username); GetGate = null; Profile = CloudPresenceSettings.Default; }
        public void AddIncoming(int count, bool history = false)
        {
            var messages = Enumerable.Range(0, count).Select(_ => new CloudChatMessage(++nextMessage, Id, new("user", Peer.Id, Peer.Username),
                Self.Id, nextMessage, Guid.NewGuid(), "incoming " + nextMessage, null, history ? "history" : "pending", DateTimeOffset.UtcNow, null)).ToArray();
            Chat = Chat with { Revision = Chat.Revision + 1, History = history ? Chat.History.Concat(messages).ToArray() : Chat.History,
                Pending = history ? Chat.Pending : messages, LatestMessageId = nextMessage };
        }
        private void Guard(CloudFriendsSession? expected) => Check(expected == FriendsSession, "Controller omitted expected account generation.");
        public Task<CloudFriendList> ListFriendsAsync(CancellationToken ct, CloudFriendsSession? expectedSession = null)
        { Guard(expectedSession); return Task.FromResult(new CloudFriendList(VisualData ? [
            new("native-friend", "accepted", "", Peer, default, default, Id, true, "busy", "今晚刷坐骑", new(123, "阿卡狄亚零式登天斗技场 重量级3")),
            new("native-away", "accepted", "", new("away", "远方的旅行者"), default, default, null, true, "away", "晚点回来"),
            new("native-offline", "accepted", "", new("offline", "星光下的猫"), default, default, null)] : [], VisualData ? 2 : 0, [], CloudChatPolicy.Notice, Self, Profile)); }
        public void SuppressFriendDuty(CloudFriendsSession expectedSession) { Guard(expectedSession); DutySuppressed = true; }
        public Task<CloudPresenceSettings> UpdateFriendPresenceSettingsAsync(CloudPresenceSettings settings, CancellationToken ct, CloudFriendsSession? expectedSession = null)
        {
            Guard(expectedSession); if (FailPresence) throw new HttpRequestException("isolated uncertain privacy save");
            Profile = settings with { Revision = Profile.Revision + 1 }; DutySuppressed = false; return Task.FromResult(Profile);
        }
        public Task<CloudChatSync> SyncChatAsync(CancellationToken ct, CloudFriendsSession? expectedSession = null)
        { Guard(expectedSession); return Task.FromResult(new CloudChatSync([new(Id, Chat.Kind, Chat.Revision, Peer, Chat.History.Count, Chat.Pending.Count, Chat.LatestMessageId)], Chat.Pending, CloudChatPolicy.Notice, [])); }
        public async Task<CloudChatConversation> GetChatAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null)
        { Guard(expectedSession); if (FailGet) throw new HttpRequestException("isolated preparation failure"); if (GetGate is { } gate) await gate(ct); return Chat; }
        public Task<CloudChatConversation> AcknowledgeChatAsync(string id, IReadOnlyList<long> ids, CancellationToken ct, CloudFriendsSession? expectedSession = null)
        {
            Guard(expectedSession); BeforeAck?.Invoke(); Acks++;
            Chat = Chat with { Revision = Chat.Revision + 1, History = Chat.History.Concat(Chat.Pending.Where(m => ids.Contains(m.Id)).Select(m => m with { State = "history" })).TakeLast(20).ToArray(), Pending = Chat.Pending.Where(m => !ids.Contains(m.Id)).ToArray() };
            return Task.FromResult(Chat);
        }
        public Task<CloudChatSendResult> SendChatAsync(string id, CloudChatSendRequest message, CancellationToken ct, CloudFriendsSession? expectedSession = null)
        {
            Guard(expectedSession); Attempts.Enqueue(message); if (FailSend) throw new HttpRequestException("isolated uncertain send");
            var row = new CloudChatMessage(++nextMessage, Id, new("user", Self.Id, Self.Username), Peer.Id, message.Sequence, message.OperationId, message.Text ?? "quick", message.QuickMessageId, "history", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Chat = Chat with { Revision = Chat.Revision + 1, History = Chat.History.Append(row).TakeLast(20).ToArray(), NextSendSequence = message.Sequence + 1 };
            if (CommitThenFail) throw new HttpRequestException("isolated response lost after commit");
            return Task.FromResult(new CloudChatSendResult(row, false, message.Sequence + 1));
        }
        public Task<CloudFriendLookup> LookupFriendAsync(string username, CancellationToken ct, CloudFriendsSession? expectedSession = null) => throw new NotSupportedException();
        public Task<CloudFriendRelation> RequestFriendAsync(string username, CancellationToken ct, CloudFriendsSession? expectedSession = null) => throw new NotSupportedException();
        public Task<CloudFriendRelation> AcceptFriendAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => throw new NotSupportedException();
        public Task<CloudFriendRelation> DeclineFriendAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => throw new NotSupportedException();
        public Task<CloudFriendRemoval> RemoveFriendAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => throw new NotSupportedException();
    }
}
