using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Encounters;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using DalamudActCompat.UI.Kit;

// Renders every shipped DACT window and page from the real UI code, in its default skin and
// Simplified Chinese, as the 1:1 baseline for the 1.0 UI redesign. Each capture is a PNG plus
// (with DACT_NATIVE_UI_VECTOR=1) an editable layer export, and an index.json lists them:
//   DACT_TEST_CIMGUI=<cimgui.dll> DACT_NATIVE_UI_VECTOR=1 PackageSmokeTests --ui-baseline <directory>
internal static class UiBaselineGenerator
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private sealed record Shot(string Group, string Name, string File, int Width, int Height, string Note);

    internal static unsafe int Run(string output)
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI")
                      ?? throw new InvalidOperationException("Set DACT_TEST_CIMGUI to Dalamud's cimgui.dll.");
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        // Capture names are Chinese; a redirected console would print them as "?".
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(output);
        var shots = new List<Shot>();
        var failures = new List<string>();
        var context = ImGui.CreateContext();
        var root = Path.Combine(Path.GetTempPath(), $"dact-ui-baseline-{Guid.NewGuid():N}");
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[]
            {
                0x20, 0x17f, 0x2000, 0x206f, 0x2190, 0x21ff, 0x2500, 0x27bf,
                0x3000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0,
            };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            if (!io.Fonts.Build()) throw new InvalidOperationException("Baseline font failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var textureApi = DispatchProxy.Create<ITextureProvider, PreviewTextureProvider>();
            var textures = (PreviewTextureProvider)(object)textureApi;
            textures.Raster = raster;
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            var logo = textures.Load(Path.Combine(assets, "dact-logo.png"));

            // DACT_UI_BASELINE_LANG renders another UI language (e.g. de, ja) to check long translations.
            var config = new PluginConfiguration { UiLanguage = Environment.GetEnvironmentVariable("DACT_UI_BASELINE_LANG") ?? "zh-CN" };
            config.Fflogs.Enabled = false;
            config.OverlayWindows = new(StringComparer.OrdinalIgnoreCase)
            {
                [DalamudActCompat.ActRuntime.SelfHostedActRuntime.CactbotAlertsOverlayName] = new() { OpenOnStartup = true, HasBeenOpened = true },
                [DalamudActCompat.ActRuntime.SelfHostedActRuntime.CactbotTimelineOverlayName] = new() { OpenOnStartup = true, HasBeenOpened = true },
            };
            var text = new UiText(config);
            var logger = new PluginLogger(EntityServiceProxy.Create<IPluginLog>(new()));
            var sample = SampleEncounterFactory.Create(DateTimeOffset.UtcNow);
            var fflogs = new FflogsEstimateService(() => config.Fflogs, Path.Combine(root, "fflogs.json"), logger);
            var store = new EncounterStateStore();
            store.UpdateCurrent(sample);
            Encounter Pull(int index) => sample with { Id = Guid.NewGuid(), StartTime = sample.StartTime.AddMinutes(index * 6) };
            var folder = sample with { Id = Guid.NewGuid(), SegmentRecords = [Pull(0), Pull(1)] };
            store.UpdateRecent([folder, sample]);
            var paths = new PluginPaths(root);
            Directory.CreateDirectory(paths.EncounterLogDirectory);
            File.WriteAllText(Path.Combine(paths.EncounterLogDirectory, "20261030-193519-sample.json"), JsonSerializer.Serialize(sample));
            var icons = new JobIconTextureSet(textureApi, Path.Combine(assets, "JobIcons"));
            var meterService = new MeterService(store, config.Meter);
            DactTheme.SetCurrent(config.Appearance, true, 0);
            // DACT_UI_BASELINE_SKIN=<skin id> renders every window in that skin (with DACT_GAME_UI_DIR
            // for 艾欧泽亚's sprites) to check skin-specific layout such as the game frame's bottom rim.
            var baselineSkin = Environment.GetEnvironmentVariable("DACT_UI_BASELINE_SKIN") is { Length: > 0 } skinId ? skinId : null;
            void UseBaselineSkin()
            {
                if (baselineSkin is null) return;
                config.Appearance.SelectedSkin = baselineSkin;
                if (Environment.GetEnvironmentVariable("DACT_GAME_UI_DIR") is { Length: > 0 } spriteDir)
                    DactTheme.GameAssets = new GameSkinAssets(name => Path.Combine(spriteDir, name + "_hr1.png") is var file && File.Exists(file)
                        ? textures.Load(file) : null);
                DactTheme.SetCurrent(config.Appearance, true, SkinCatalog.EorzeaSponsorTier);
            }
            UseBaselineSkin();

            // Constructor arguments are resolved by name first, then by type; callbacks become
            // inert stubs so no window can reach real services while it is being captured.
            var byType = new Dictionary<Type, object?>
            {
                [typeof(PluginConfiguration)] = config, [typeof(UiText)] = text, [typeof(PluginLogger)] = logger,
                [typeof(FflogsEstimateService)] = fflogs, [typeof(EncounterStateStore)] = store, [typeof(PluginPaths)] = paths,
                [typeof(JobIconTextureSet)] = icons, [typeof(MeterService)] = meterService, [typeof(ISharedImmediateTexture)] = logo,
                [typeof(ITextureProvider)] = textureApi,
                [typeof(DalamudActCompat.Core.Interfaces.IParserEngine)] = DispatchProxy.Create<DalamudActCompat.Core.Interfaces.IParserEngine, HelpScreenshotGenerator.RunningParser>(),
            };
            T Construct<T>(Dictionary<string, object?>? byName = null, Dictionary<string, object?>? returns = null)
            {
                var constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .OrderByDescending(c => c.GetParameters().Length).First();
                var arguments = constructor.GetParameters().Select(parameter =>
                {
                    if (byName?.TryGetValue(parameter.Name!, out var named) == true) return named;
                    if (byType.TryGetValue(parameter.ParameterType, out var typed)) return typed;
                    if (typeof(Delegate).IsAssignableFrom(parameter.ParameterType))
                        return HelpScreenshotGenerator.Stub(parameter.ParameterType,
                            returns?.TryGetValue(parameter.Name!, out var value) == true ? value : null, returns?.ContainsKey(parameter.Name!) == true);
                    return parameter.HasDefaultValue ? parameter.DefaultValue
                        : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
                }).ToArray();
                return (T)constructor.Invoke(arguments);
            }

            void Frame(Window window, Vector2 position, Vector2? size, Vector2 display, Action? extra)
            {
                io.DisplaySize = display;
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    if (window.DrawConditions())
                    {
                        window.PreDraw();
                        ImGui.SetNextWindowPos(position);
                        if (size is { } fixedSize) ImGui.SetNextWindowSize(fixedSize);
                        if (ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings)) window.Draw();
                        ImGui.End();
                        window.PostDraw();
                    }

                    extra?.Invoke();
                }

                ImGui.Render();
            }

            // Largest remaining vertical scroll among the active windows whose name contains the
            // marker; full-length captures grow until nothing is left to scroll.
            float ScrollLeft(string marker)
            {
                var left = 0f;
                for (var i = 0; i < context.Windows.Size; i++)
                {
                    var native = context.Windows[i];
                    if (!native.Active) continue;
                    var name = Marshal.PtrToStringUTF8((nint)native.Name) ?? "";
                    if (name.Contains(marker, StringComparison.Ordinal)) left = MathF.Max(left, native.ScrollMax.Y);
                }

                return left;
            }

            // DACT_UI_BASELINE_JITTER=1 freezes time after the capture settles and redraws 40
            // frames: with nothing animating, a window whose vertices still change in the last 20
            // lays itself out from its own last frame and shakes in the game (the 1.0 sign-in form
            // did at non-integer UI scales, on a 16-frame cycle). A change that stops early is a
            // capture still settling. DACT_UI_BASELINE_SCALE sets the UI scale to check.
            // Expected movers, all on the wall clock: the splash filmstrip, 听雨窗's rain, the skin
            // catalogue's animated thumbnails, and 战斗记录 · 日志文件 (the sample fight has no end
            // time, so its DPS keeps counting).
            var jitterCheck = Environment.GetEnvironmentVariable("DACT_UI_BASELINE_JITTER") == "1";
            if (float.TryParse(Environment.GetEnvironmentVariable("DACT_UI_BASELINE_SCALE"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var uiScale)) io.FontGlobalScale = uiScale;
            var jitters = new List<string>();
            void CheckStill(string group, string name, Action frame)
            {
                // Four seconds of normal frames let every DactMotion ease land first; an ease still
                // creeping under the frozen clock would cross a rounding step and look like a shake.
                for (var index = 0; index < 240; index++) frame();
                var delta = io.DeltaTime;
                io.DeltaTime = 1e-5f;
                var states = new Dictionary<string, HashSet<int>>();
                // Scrollbar and content size per window, to tell a scrollbar flipping on and off
                // from content laid out from last frame's measurement.
                var layouts = new Dictionary<string, HashSet<string>>();
                try
                {
                    for (var index = 0; index < 40; index++)
                    {
                        frame();
                        if (index < 20) continue;
                        var owners = new Dictionary<nint, string>();
                        for (var i = 0; i < context.Windows.Size; i++)
                        {
                            var native = context.Windows[i];
                            if (!native.Active) continue;
                            var windowName = Marshal.PtrToStringUTF8((nint)native.Name) ?? "?";
                            owners[(nint)native.DrawList.Handle] = windowName;
                            if (!layouts.TryGetValue(windowName, out var layout)) layouts[windowName] = layout = [];
                            layout.Add($"scrollbar={native.ScrollbarY} content={native.ContentSize.X:R}x{native.ContentSize.Y:R} scroll={native.Scroll.Y:R}");
                        }
                        var data = ImGui.GetDrawData();
                        for (var n = 0; n < data.CmdListsCount; n++)
                        {
                            var list = new ImDrawListPtr(data.CmdLists[n]);
                            var hash = new HashCode();
                            for (var v = 0; v < list.VtxBuffer.Size; v++)
                            {
                                var position = list.VtxBuffer[v].Pos;
                                hash.Add(MathF.Round(position.X * 100));
                                hash.Add(MathF.Round(position.Y * 100));
                            }
                            var owner = owners.TryGetValue((nint)list.Handle, out var known) ? known : "list " + n;
                            if (!states.TryGetValue(owner, out var seen)) states[owner] = seen = [];
                            seen.Add(hash.ToHashCode());
                        }
                    }
                }
                finally { io.DeltaTime = delta; }
                foreach (var (owner, seen) in states)
                    if (seen.Count > 1)
                    {
                        jitters.Add($"{group} / {name}: {owner} ({seen.Count} states)");
                        Console.WriteLine($"JITTER {group} / {name}: {owner} ({seen.Count} states)");
                        if (layouts.TryGetValue(owner, out var layout) && layout.Count > 1)
                            foreach (var state in layout) Console.WriteLine("    " + state);
                    }
            }

            void Capture(string group, string name, Window window, Vector2? size, string note = "", string? fullMarker = null,
                Vector2? display = null, Action? extra = null, int frames = 30, Vector2? at = null)
            {
                // DACT_UI_BASELINE_ONLY=<group prefix> re-renders one group; state set between
                // captures still runs, so later groups see the same windows as a full run.
                if (Environment.GetEnvironmentVariable("DACT_UI_BASELINE_ONLY") is { Length: > 0 } only &&
                    !group.StartsWith(only, StringComparison.Ordinal)) return;
                try
                {
                    var current = size;
                    var area = display ?? (size is { } s ? s + new Vector2(20) : new Vector2(1000, 760));
                    for (var frame = 0; frame < frames; frame++) Frame(window, at ?? new(10), current, area, extra);
                    if (fullMarker is not null && current is { } start)
                    {
                        // Re-layout with the extra height until the page no longer scrolls.
                        for (var pass = 0; pass < 6; pass++)
                        {
                            var left = ScrollLeft(fullMarker);
                            if (left < 1) break;
                            current = new Vector2(start.X, current!.Value.Y + MathF.Ceiling(left) + 4);
                            area = current.Value + new Vector2(20);
                            for (var frame = 0; frame < 6; frame++) Frame(window, new(10), current, area, extra);
                        }
                    }

                    if (jitterCheck) CheckStill(group, name, () => Frame(window, at ?? new(10), current, area, extra));
                    var file = Sanitize($"{group}--{name}") + ".png";
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, file));
                    shots.Add(new(group, name, file, (int)area.X, (int)area.Y, note));
                    Console.WriteLine($"captured {group} / {name} ({area.X}x{area.Y})");
                }
                catch (Exception exception)
                {
                    failures.Add($"{group} / {name}: {exception.GetType().Name}: {exception.Message}");
                    Console.WriteLine($"FAILED {group} / {name}: {exception}");
                    // A failed draw leaves an open frame; finish it so the next capture starts clean.
                    try { ImGui.EndFrame(); } catch { }
                }
            }

            // ---- Control center: sign-in gates, then every page at its default size and in full.
            var account = new HelpScreenshotGenerator.AccountState();
            var control = HelpScreenshotGenerator.CreateControlCenter(config, text, logger, fflogs, logo, sample, account);
            typeof(ControlCenterWindow).GetField("visibilityTransition", Private)!
                .SetValue(control, Enum.Parse(typeof(ControlCenterWindow).GetField("visibilityTransition", Private)!.FieldType, "Open"));
            var discovery = typeof(ControlCenterWindow).GetField("discoveryNoticeUntil", Private)!;
            var authenticationField = typeof(ControlCenterWindow).GetFields(Private).Single(f => f.FieldType.Name == "CloudAuthenticationPage");
            var pageField = typeof(ControlCenterWindow).GetField("selectedPage", Private)!;
            var controlSize = new Vector2(920, 640);
            void ControlCapture(string name, string note, bool full)
            {
                discovery.SetValue(control, 0L);
                Capture("01 控制中心", name, control, controlSize, note, full ? "control-center-page-content" : null);
            }

            foreach (var page in Enum.GetValues(authenticationField.FieldType))
            {
                authenticationField.SetValue(control, page);
                ControlCapture($"账号-{page}", "未登录时所有页面都被账号门槛挡住", false);
            }

            authenticationField.SetValue(control, Enum.ToObject(authenticationField.FieldType, 0));
            account.DeviceLimit = new CloudDeviceLimit(5,
            [
                new("a1", "DESKTOP-7K2Q", DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now.AddDays(-47)),
                new("a2", "LAPTOP-RAID", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(-20)),
                new("a3", "OFFICE-PC", DateTimeOffset.Now.AddDays(-3), DateTimeOffset.Now.AddDays(-90)),
                new("a4", "MINI-PC", DateTimeOffset.Now.AddDays(-6), DateTimeOffset.Now.AddDays(-12)),
                new("a5", null, DateTimeOffset.Now.AddDays(-9), null),
            ]);
            ControlCapture("账号-设备已满", "第 6 台登录时选择登出哪台或全部登出", false);
            account.DeviceLimit = null;
            account.SignedIn = true;
            ControlCapture("账号-Discord绑定", "登录后必须先绑定 Discord", false);
            account.DiscordBound = true;
            // 战斗记录: recent fights (one duty folder), a saved log file and a progression that
            // mirrors the Figma sample (32 pulls of an ultimate, two cleared savage floors).
            var historyRoot = Path.Combine(Path.GetTempPath(), "dact-ui-baseline-history");
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, true);
            var historyPaths = new PluginPaths(historyRoot);
            Directory.CreateDirectory(historyPaths.EncounterLogDirectory);
            File.WriteAllText(Path.Combine(historyPaths.EncounterLogDirectory, "20261107-214210-光暗未来绝境战.json"), JsonSerializer.Serialize(sample));
            const uint ultimate = 1238;
            var pullTimes = Enumerable.Range(0, 32).Select(index => DateTimeOffset.Now.AddMinutes(-6 * (31 - index))).ToArray();
            Encounter UltimatePull(int index) => sample with
            {
                Id = Guid.NewGuid(), TerritoryId = ultimate, ZoneName = "光暗未来绝境战", EnemyName = "光暗未来绝境战",
                StartTime = pullTimes[index], EndTime = pullTimes[index].AddSeconds(192),
            };
            var ultimateFolder = UltimatePull(31) with { Id = Guid.NewGuid(), SegmentRecords = [UltimatePull(30), UltimatePull(31)] };
            var historyStore = new EncounterStateStore();
            historyStore.UpdateRecent([ultimateFolder, sample with { Id = Guid.NewGuid(), StartTime = DateTimeOffset.Now.AddHours(-2) }]);
            var progression = new ProgressionStore(new JsonFileStore(), Path.Combine(historyRoot, "progression.json"));
            string[] ultimateBosses = ["绝命战士", "希瓦·米特隆", "暗之巫女", "潘多拉·米特隆"];
            double[] reached = [0.4, 0.7, 1.2, 0.9, 1.5, 1.6, 1.3, 1.8, 1.7, 2.1, 1.9, 2.3, 1.4, 2.2, 2.4, 2.6, 2.1, 2.5, 2.8, 2.3, 2.9, 3.1, 2.7, 3.2, 3.4, 2.6, 3.3, 3.5, 3.68, 3.1, 3.2, 1.5];
            string?[] casts = ["光之海啸", "时间压缩·绝", null, "黑暗狂水"];
            for (var index = 0; index < reached.Length; index++)
            {
                var phase = (int)Math.Floor(reached[index]) + 1;
                progression.Add(new ProgressionPull(ultimate, "光暗未来绝境战", pullTimes[index], pullTimes[index].AddSeconds(150 + 40 * reached[index]),
                    false, ultimateBosses.Take(phase).ToArray(), 1 - (reached[index] - (phase - 1)), casts[(index * 7 + phase) % casts.Length], 120 + index));
            }
            foreach (var (territory, name, count) in new[] { (1257u, "阿卡狄亚零式登天斗技场 重量级4", 11), (1255u, "阿卡狄亚零式登天斗技场 重量级3", 6) })
                for (var index = 0; index < count; index++)
                {
                    var at = DateTimeOffset.Now.AddDays(-1).AddMinutes(-9 * (count - index));
                    progression.Add(new ProgressionPull(territory, name, at, at.AddMinutes(8), index == count - 1, ["Boss"],
                        index == count - 1 ? 0 : .1 + index * .05, null, index == count - 1 ? null : 300));
                }
            control.EncounterHistory = historyStore;
            control.Progression = progression;
            control.Paths = historyPaths;
            control.LocalizeZone = (_, name) => name;
            var historyTabField = typeof(ControlCenterWindow).GetField("historyTab", Private)!;
            var friendsUi = FriendsUiSmokeTests.CreateVisualSampleAsync(() => { }).GetAwaiter().GetResult();
            control.Friends = friendsUi;
            var friendSectionField = typeof(FriendsUiManager).GetField("friendSection", Private)!;
            var friendAlertsField = typeof(ControlCenterWindow).GetField("friendNotificationsPage", Private)!;
            foreach (var page in Enum.GetValues(pageField.FieldType))
            {
                pageField.SetValue(control, page);
                ControlCapture($"{page}-默认尺寸", "920×640 默认窗口", false);
                ControlCapture($"{page}-完整长页", "展开整页内容（游戏内需滚动）", true);
                if (page.ToString() == "Friends")
                {
                    friendSectionField.SetValue(friendsUi, 1);
                    ControlCapture("Friends-申请", "好友 · 申请", false);
                    friendSectionField.SetValue(friendsUi, 2);
                    ControlCapture("Friends-添加", "好友 · 添加", false);
                    friendSectionField.SetValue(friendsUi, 0);
                    friendAlertsField.SetValue(control, true);
                    ControlCapture("Friends-来信提醒", "好友 · 来信提醒", false);
                    friendAlertsField.SetValue(control, false);
                }
                if (page.ToString() == "Diagnostics")
                {
                    // 场景方案 sample matching the Figma board.
                    var scenes = config.Scenes;
                    scenes.Enabled = true;
                    var alerts = DalamudActCompat.ActRuntime.SelfHostedActRuntime.CactbotAlertsOverlayName;
                    var timeline = DalamudActCompat.ActRuntime.SelfHostedActRuntime.CactbotTimelineOverlayName;
                    var high = scenes.Get(SceneKind.HighEnd); high.Meter = "Classic"; high.Overlays[alerts] = SceneOverlayAction.Open; high.Overlays[timeline] = SceneOverlayAction.Open;
                    var normal = scenes.Get(SceneKind.Normal); normal.Meter = "Horizontal"; normal.Overlays[alerts] = SceneOverlayAction.Open; normal.Overlays[timeline] = SceneOverlayAction.Close;
                    var field = scenes.Get(SceneKind.Field); field.Meter = "hidden"; field.Overlays[alerts] = SceneOverlayAction.Close; field.Overlays[timeline] = SceneOverlayAction.Close;
                    var scenesPageField = typeof(ControlCenterWindow).GetField("scenesPage", Private)!;
                    scenesPageField.SetValue(control, true);
                    ControlCapture("Diagnostics-场景方案", "设置 · 场景方案", false);
                    typeof(ControlCenterWindow).GetField("pendingScenePopover", Private)!.SetValue(control, SceneKind.Normal);
                    ControlCapture("Diagnostics-场景方案-选择悬浮窗", "设置 · 场景方案 · 选择悬浮窗", false);
                    scenesPageField.SetValue(control, false);
                }
                if (page.ToString() == "Ai")
                {
                    // AI 助手: settings, then the three tabs with sample conversations (the
                    // default capture above is the 未开启 page). Replies use the real parser.
                    var aiSettingsField = typeof(ControlCenterWindow).GetField("aiSettingsPage", Private)!;
                    config.Ai.Enabled = true;
                    aiSettingsField.SetValue(control, true);
                    ControlCapture("Ai-AI设置", "AI 助手 · AI 设置（未测试）", false);
                    // Tall enough that the page's own scroll area shows its end: the last card keeps
                    // its bottom margin instead of sitting on the window edge.
                    Capture("01 控制中心", "Ai-AI设置-到底", control, new Vector2(920, 1180), "AI 设置 · 页面末尾留白");
                    aiSettingsField.SetValue(control, false);
                    config.Ai.Verified = true;
                    config.Ai.KnownModels = ["claude-haiku-5-5", "claude-opus-5-5", "claude-sonnet-5-5"];
                    var conversations = (Array)typeof(ControlCenterWindow).GetField("aiConversations", Private)!.GetValue(control)!;
                    var entryType = typeof(ControlCenterWindow).GetNestedType("AiEntry", Private)!;
                    void Say(int tab, bool user, string display, string? json = null, string? context = null)
                    {
                        var conversation = conversations.GetValue(tab)!;
                        var entries = (System.Collections.IList)conversation.GetType().GetProperty("Entries")!.GetValue(conversation)!;
                        var reply = json is null ? null : DalamudActCompat.Ai.AiReplyFormat.Parse(json);
                        entries.Add(Activator.CreateInstance(entryType, user, reply?.Text ?? display, json ?? display, reply, context));
                    }
                    Say(0, true, "触发器突然不播报了，邮差标点也没反应。", context: "[DACT runtime snapshot] …");
                    Say(0, false, "", """{"text":"找到 2 处问题，按顺序处理：","findings":[{"level":"error","title":"共享 Host 已停止","detail":"12:41 退出（代码 -1），触发器、邮差和 TTS 都跑在它里面。","action":"restart_host:shared","action_label":"重启共享 Host"},{"level":"warn","title":"鲶鱼精邮差没有「发送游戏指令」权限","detail":"标点和地标需要这项权限；上次更新扩展后被重置。","action":"open:extensions","action_label":"去授权"}],"advice":[],"actions":[],"sources":[],"escalate":false}""");
                    Say(0, true, "重启了，还是不行");
                    Say(0, false, "", """{"text":"Host 已正常运行，但日志里触发器每次都报「找不到语音引擎」。这个情况我没法在本机修好，建议把日志和这次对话发给开发者。","findings":[],"advice":[],"actions":[],"sources":[],"escalate":true}""");
                    var reviewConversation = conversations.GetValue(1)!;
                    reviewConversation.GetType().GetProperty("EncounterId")!.SetValue(reviewConversation, ultimateFolder.SegmentRecords[^1].Id);
                    Say(1, true, "请复盘这场战斗");
                    Say(1, false, "", """{"text":"P2 后段团灭，你的输出在同职业里偏低，主要损失来自转阶段和一次可以避免的死亡。","findings":[],"advice":[{"title":"把爆发药挪到第二次团辅前 5 秒","detail":"这次在团辅后 14 秒才用，药效只覆盖了一半团辅窗口。"},{"title":"转阶段后先打预读技能再进循环","detail":"P1→P2 与 P2→P3 转场后各空转约 2.5 秒，合计损失约 1.1% 伤害。"},{"title":"P2 点名分散时向场边多走一步","detail":"两次被同伴的范围技能擦到，第二次后马上死亡，导致这次团灭。"}],"actions":[],"sources":[],"escalate":false}""");
                    Say(2, true, "战斗统计怎么只显示我自己？", context: "[DACT settings catalog] …");
                    Say(2, false, "", """{"text":"在「战斗统计」点「自定义…」，打开窗口行为里的「只显示自己」。这只影响统计窗显示，不会改变解析和日志；关掉后马上恢复显示全队。","findings":[],"advice":[],"actions":[{"id":"open:meter","label":"打开自定义布局"},{"id":"help:CombatMeter","label":"在帮助里查看"}],"sources":["帮助 · 战斗统计 ·「打开与摆放」","你当前的战斗统计设置"],"escalate":false}""");
                    Say(2, true, "那 rDPS 和 DPS 有什么区别？");
                    Say(2, false, "", """{"text":"DPS 按你自己的出手时间计算；rDPS 去掉别人给你的团辅、加回你给队友的团辅，更接近 FFLogs 的口径。DACT 的 rDPS 是本地实时估算，和上传后的 FFLogs 会有小差异。","findings":[],"advice":[],"actions":[],"sources":["帮助 · 战斗统计 ·「数值是怎么算的」"],"escalate":false}""");
                    var aiTabField = typeof(ControlCenterWindow).GetField("aiTab", Private)!;
                    aiTabField.SetValue(control, 0);
                    ControlCapture("Ai-诊断问题", "AI 助手 · 诊断问题", false);
                    aiTabField.SetValue(control, 1);
                    ControlCapture("Ai-战斗复盘", "AI 助手 · 战斗复盘", false);
                    aiTabField.SetValue(control, 2);
                    ControlCapture("Ai-使用问答", "AI 助手 · 使用问答", false);
                    aiTabField.SetValue(control, 0);
                    config.Ai.Enabled = false;
                    config.Ai.Verified = false;
                }
                if (page.ToString() != "History") continue;
                historyTabField.SetValue(control, 1);
                ControlCapture("History-开荒进度", "战斗记录 · 开荒进度", false);
                historyTabField.SetValue(control, 2);
                ControlCapture("History-日志文件", "战斗记录 · 日志文件", false);
                historyTabField.SetValue(control, 0);
            }
            pageField.SetValue(control, Enum.Parse(pageField.FieldType, "Overview"));
            // 开屏动画 as a filmstrip of the logo assembling (each shot replays the splash from its
            // start at 60 fps), then 新版导览 on its first and third steps.
            var splashField = typeof(ControlCenterWindow).GetField("splashPlayedThisSession", BindingFlags.NonPublic | BindingFlags.Static)!;
            var splashStartField = typeof(ControlCenterWindow).GetField("splashStartedAt", Private)!;
            control.LogoLayers = textures.Load(Path.Combine(assets, "dact-logo-layers.png"));
            // DACT_UI_BASELINE_SPLASH_MS=0,40,80,… replaces the filmstrip times, e.g. to make a GIF.
            var splashTimes = Environment.GetEnvironmentVariable("DACT_UI_BASELINE_SPLASH_MS") is { Length: > 0 } times
                ? times.Split(',').Select(int.Parse).ToArray()
                : [250, 450, 650, 850, 1100, 1250, 1450, 1650];
            foreach (var milliseconds in splashTimes)
            {
                splashField.SetValue(null, false);
                splashStartField.SetValue(control, -1d);
                Capture("01 控制中心", $"开屏动画-{milliseconds}ms", control, controlSize, $"开屏动画 · {milliseconds} ms",
                    frames: milliseconds * 60 / 1000 + 1);
            }
            splashField.SetValue(null, true);
            splashStartField.SetValue(control, -1d);
            config.ShowNewVersionTour = true;
            ControlCapture("新版导览-1", "新版导览 · 左侧导航", false);
            var tourStepField = typeof(ControlCenterWindow).GetField("tourStep", Private)!;
            tourStepField.SetValue(control, 2);
            ControlCapture("新版导览-3", "新版导览 · 好友搬到了侧栏", false);
            tourStepField.SetValue(control, 6);
            ControlCapture("新版导览-7", "新版导览 · AI 助手", false);
            tourStepField.SetValue(control, -1);
            config.ShowNewVersionTour = false;
            pageField.SetValue(control, Enum.Parse(pageField.FieldType, "Overview"));
            // Modal dialogs stay open between captures; each is closed again right after its shot.
            // 全局搜索: the header field searches in place; the shortcut opens a standalone box.
            control.OpenSearch("fflogs");
            ControlCapture("Overview-全局搜索", "顶栏搜索 · 原地下拉结果", false);
            typeof(ControlCenterWindow).GetMethod("CloseHeaderSearch", Private)!.Invoke(control, null);
            var searchWindow = new SearchWindow(control);
            searchWindow.ToggleSearch();
            // The harness positions windows itself; place the box where the game would (centred, 16 % down).
            Capture("01 控制中心", "独立搜索框", searchWindow, null, "快捷键（默认 Ctrl+Shift+K）弹出的独立搜索框，不加遮罩",
                display: new Vector2(1000, 560), at: new Vector2((1000 - 560) / 2f, 560 * .16f));
            searchWindow.IsOpen = false;
            // DACT's own picker (安装 DLL / ZIP, Cactbot, 截图, 日志目录) on a fixture folder.
            var pickerFolder = Path.Combine(Path.GetTempPath(), "dact-picker-baseline");
            Directory.CreateDirectory(Path.Combine(pickerFolder, "ACT Plugins"));
            Directory.CreateDirectory(Path.Combine(pickerFolder, "Triggernometry"));
            File.WriteAllBytes(Path.Combine(pickerFolder, "PostNamazu.dll"), new byte[48_000]);
            File.WriteAllBytes(Path.Combine(pickerFolder, "SilverDasher-v0.6.0.4.zip"), new byte[2_300_000]);
            File.WriteAllText(Path.Combine(pickerFolder, "readme.txt"), "");
            var picker = new DactFilePicker(text, logo);
            picker.OpenFile("baseline", text.Get("选择 ACT 扩展 DLL 或 ZIP", "Select ACT plugin DLL or ZIP"), [".dll", ".zip"], (_, _) => { },
                startDirectory: pickerFolder);
            Capture("01 控制中心", "选文件窗口", picker, new Vector2(780, 520), "安装 DLL / ZIP 等选文件、选目录窗口（DACT 样式）");
            picker.IsOpen = false;
            control.OpenFeedback("触发器不播报、邮差标点没反应。AI 诊断：共享 Host 重启后恢复运行，但触发器每次报「找不到语音引擎」，本机无法修复。", "（AI 对话）", 4);
            ControlCapture("Overview-反馈", "反馈（AI 转交）", false);
            DalamudActCompat.UI.Kit.DactUi.CloseDialog("##dact-feedback");
            ControlCapture("Overview-反馈已关闭", "确认弹窗关闭后不残留", false);
            typeof(ControlCenterWindow).GetField("cloudChipAnchor", Private)!.SetValue(control, new Vector2(806, 54));
            typeof(ControlCenterWindow).GetField("cloudQuickPopupRequested", Private)!.SetValue(control, true);
            ControlCapture("Overview-云同步状态", "顶栏 · 云同步状态浮层（最后拍，弹层不会挡住其他页面）", false);
            // Opening another popup at the root closes the popover (ImGui replaces the popup at that
            // level), so it does not hang over the skin captures that follow.
            Frame(control, new(10), controlSize, controlSize + new Vector2(20), () => ImGui.OpenPopup("##baseline-close-popovers"));
            Frame(control, new(10), controlSize, controlSize + new Vector2(20), null);

            // ---- Every skin on the overview page. The window sits 90 px in so the corner emblem
            // and the ornaments drawn behind it (Alexander's gears) stay in the capture. Game
            // textures (Eorzea sprites, ultimate plate backdrops) are not available offline, so
            // those parts show the code's own fallbacks.
            SkinCatalog.UltimateUnlocked = () => true;
            foreach (var skin in SkinCatalog.All.Where(skin => skin.EasterEgg)) config.Appearance.UnlockedEasterEggs.Add(skin.Id);
            // DACT_GAME_UI_DIR may point at Light-theme sprites (<name>_hr1.png) exported from the
            // local game install for reference; they are never shipped. Without it the Eorzea
            // capture shows the code's untextured fallbacks.
            if (Environment.GetEnvironmentVariable("DACT_GAME_UI_DIR") is { Length: > 0 } gameUi)
                DactTheme.GameAssets = new GameSkinAssets(name => Path.Combine(gameUi, name + "_hr1.png") is var file && File.Exists(file)
                    ? textures.Load(file) : null);
            void UseSkin(string id)
            {
                config.Appearance.SelectedSkin = id;
                DactTheme.SetCurrent(config.Appearance, true, SkinCatalog.EorzeaSponsorTier);
            }
            pageField.SetValue(control, Enum.ToObject(pageField.FieldType, 0));
            foreach (var skin in SkinCatalog.All)
            {
                UseSkin(skin.Id);
                discovery.SetValue(control, 0L);
                Capture("08 皮肤", skin.ChineseName, control, controlSize, "概览页（窗口外留出装饰空间）",
                    display: controlSize + new Vector2(180), at: new Vector2(90));
            }
            // 艾欧泽亚's fields and switches (下拉框、云同步状态、开关) on the pages that use them most.
            UseSkin(SkinCatalog.Eorzea);
            foreach (var page in new[] { "Diagnostics", "Extensions", "Cloud" })
            {
                pageField.SetValue(control, Enum.Parse(pageField.FieldType, page));
                discovery.SetValue(control, 0L);
                Capture("08 皮肤", $"艾欧泽亚-{page}", control, controlSize, "艾欧泽亚 · 下拉框、开关与云同步状态");
            }
            // 艾欧泽亚 hover help: the game window sprite is the tooltip itself (paper and rim), one
            // short line and a long wrapped one, so the rim's fit on small panels can be checked.
            foreach (var (name, hint) in new[] { ("短", "关闭窗口"), ("长", "登录状态使用 Windows 当前用户加密，并在重启游戏或电脑后恢复。不同电脑或 Windows 用户需要重新登录。\n右键可以管理好友") })
                Capture("08 皮肤", $"艾欧泽亚-悬停说明-{name}", control, controlSize, "艾欧泽亚 · 悬停说明（游戏窗口外框）", extra: () =>
                {
                    ImGui.SetNextWindowPos(new Vector2(300, 220));
                    DactUi.Tooltip(hint);
                });
            pageField.SetValue(control, Enum.ToObject(pageField.FieldType, 0));

            // The skin manager page, browsed in the default skin and in one ultimate skin.
            var skinPanel = (SkinSettingsPanel)typeof(ControlCenterWindow).GetField("skinSettings", Private)!.GetValue(control)!;
            pageField.SetValue(control, Enum.Parse(pageField.FieldType, "Diagnostics"));
            foreach (var (id, preview) in new[] { (SkinCatalog.Default, SkinCatalog.UltimateBahamut), (SkinCatalog.UltimateBahamut, SkinCatalog.LiquidGlass) })
            {
                UseSkin(id);
                typeof(SkinSettingsPanel).GetProperty(nameof(SkinSettingsPanel.IsOpen))!.SetValue(skinPanel, true);
                typeof(SkinSettingsPanel).GetProperty(nameof(SkinSettingsPanel.PreviewSkinId), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                    .SetValue(skinPanel, preview);
                discovery.SetValue(control, 0L);
                Capture("08 皮肤", $"管理皮肤-{SkinCatalog.All.First(skin => skin.Id == id).ChineseName}", control, controlSize, "设置 / 外观与皮肤");
            }
            skinPanel.Close();
            DactTheme.GameAssets = null;
            UseSkin(SkinCatalog.Default);
            SkinCatalog.UltimateUnlocked = () => false;
            UseBaselineSkin();
            pageField.SetValue(control, Enum.ToObject(pageField.FieldType, 0));

            // ---- Combat meters and their style editor.
            var status = new Dictionary<string, object?>
            {
                ["runningStatusIcon"] = textures.Load(Path.Combine(assets, "StatusIcons", "CombatRunning.png")),
                ["transitionStatusIcon"] = textures.Load(Path.Combine(assets, "StatusIcons", "CombatTransition.png")),
                ["endedStatusIcon"] = textures.Load(Path.Combine(assets, "StatusIcons", "CombatEnded.png")),
                ["localizeZoneName"] = (Func<uint?, string, string>)((_, zone) => zone),
            };
            config.Meter.IsVisible = true;
            var meter = Construct<MeterWindow>(status);
            byType[typeof(MeterWindow)] = meter;
            config.Meter.CompactMode = false;
            Capture("02 战斗统计", "竖版-标准", meter, new(540, 300), "经典竖版");
            config.Meter.CompactMode = true;
            Capture("02 战斗统计", "竖版-紧凑", meter, new(540, 300), "紧凑模式");
            config.Meter.CompactMode = false;

            config.Meter.HorizontalWindow.IsEnabled = true;
            var horizontal = Construct<HorizontalMeterWindow>();
            byType[typeof(HorizontalMeterWindow)] = horizontal;
            Capture("02 战斗统计", "横版", horizontal, null, "横版战斗统计（自动尺寸）", display: new(1100, 420));
            config.Meter.HorizontalWindow.IsEnabled = false;

            config.Meter.RoleSplitWindow.IsEnabled = true;
            var damage = Construct<RoleSplitMeterWindow>(new() { ["group"] = RoleSplitGroup.DamageTank });
            var healer = Construct<RoleSplitMeterWindow>(new() { ["group"] = RoleSplitGroup.Healer });
            Capture("02 战斗统计", "分职能-DT", damage, null, "D/T 伤害榜", display: new(700, 520));
            Capture("02 战斗统计", "分职能-治疗", healer, null, "治疗 HPS 榜", display: new(700, 420));
            config.Meter.RoleSplitWindow.IsEnabled = false;

            var editor = Construct<MeterStyleEditorWindow>(new()
            {
                ["meterWindow"] = meter, ["horizontalMeterWindow"] = horizontal,
                ["roleSplitDamageWindow"] = damage, ["roleSplitHealerWindow"] = healer,
            });
            editor.Open();
            var kindField = typeof(MeterStyleEditorWindow).GetField("selectedKind", Private)!;
            var propertiesTabField = typeof(MeterStyleEditorWindow).GetField("propertiesTab", Private)!;
            var selectedSlotField = typeof(MeterStyleEditorWindow).GetField("selectedSlotId", Private)!;
            foreach (var kind in Enum.GetValues<MeterWindowKind>())
            {
                kindField.SetValue(editor, kind);
                propertiesTabField.SetValue(editor, 0);
                Capture("03 战斗统计样式编辑器", kind.ToString(), editor, new(1100, 760), "样式编辑器");
                if (kind != MeterWindowKind.Classic) continue;
                // 所选槽位 with 职业 / ID selected, like the Figma board.
                propertiesTabField.SetValue(editor, 1);
                selectedSlotField.SetValue(editor, config.Meter.ClassicWindow.Slots.First(slot => slot.Metric == MeterSlotMetric.PlayerIdentity).Id);
                Capture("03 战斗统计样式编辑器", "Classic-所选槽位", editor, new(1100, 760), "样式编辑器 · 所选槽位");
            }

            // ---- Combat history.
            var history = Construct<EncounterWindow>(new() { ["localizeZoneName"] = (Func<uint?, string, string>)((_, zone) => zone) });
            var historyPage = typeof(EncounterWindow).GetField("selectedPage", Private)!;
            var selectedRecent = typeof(EncounterWindow).GetField("selectedRecentId", Private)!;
            var expanded = (HashSet<Guid>)typeof(EncounterWindow).GetField("expandedRecentFolderIds", Private)!.GetValue(history)!;
            historyPage.SetValue(history, Enum.ToObject(historyPage.FieldType, 0));
            expanded.Add(folder.Id);
            selectedRecent.SetValue(history, folder.SegmentRecords[1].Id);
            Capture("04 战斗历史", "最近战斗", history, new(980, 620), "最近战斗（展开分段）");
            historyPage.SetValue(history, Enum.ToObject(historyPage.FieldType, 1));
            Capture("04 战斗历史", "日志文件", history, new(980, 620), "日志文件列表");

            // ---- Secondary windows.
            var helpDirectory = Path.Combine(assets, "Help");
            var help = Construct<HelpWindow>(new()
            {
                ["imageDirectory"] = helpDirectory,
                ["loadImage"] = (Func<string, ISharedImmediateTexture>)(path => textures.Load(path)),
            });
            var helpPage = typeof(HelpWindow).GetField("selectedPage", Private)!;
            foreach (var page in Enum.GetValues(helpPage.FieldType))
            {
                helpPage.SetValue(help, page);
                Capture("05 使用帮助", page.ToString()!, help, new(1100, 760), "帮助窗口（完整长页）", "help-document-content");
            }

            var advanced = Construct<SettingsWindow>();
            Capture("06 高级设置", "默认", advanced, new(860, 640), "旧版完整设置（高级入口）");
            Capture("06 高级设置", "完整长页", advanced, new(860, 640), "旧版完整设置（展开）", "DalamudActCompat");

            // A healthy running Host, as most users see it.
            var hostSnapshot = new DalamudActCompat.Infrastructure.Processes.HostSupervisorSnapshot(
                DalamudActCompat.Infrastructure.Processes.HostSupervisorState.Running, true,
                DalamudActCompat.Infrastructure.Ipc.HostConnectionStatus.Connected, 0, 0, 0, "baseline", 12840, 12840,
                186L * 1024 * 1024, 38, "Healthy", string.Empty, [], [], []);
            var hostSnapshots = new Dictionary<string, object?>
            {
                ["getHostSnapshot"] = hostSnapshot, ["getMatchaHostSnapshot"] = hostSnapshot, ["getGenericHostSnapshot"] = hostSnapshot,
            };
            var statusWindow = Construct<StatusWindow>(returns: hostSnapshots);
            Capture("07 其他窗口", "运行状态", statusWindow, new(760, 560), "Host / 解析器运行状态", "DalamudActCompat");

            var launcher = Construct<LauncherWindow>(new() { ["buttonTexture"] = textures.Load(Path.Combine(assets, "dact-logo.png")) });
            Capture("07 其他窗口", "快捷悬浮按钮", launcher, null, "左键设置 / 右键显示隐藏 / 中键拖动", display: new(160, 160));

            var home = Construct<SimplifiedHomeWindow>();
            Capture("07 其他窗口", "简化主页", home, null, "简化模式主页", display: new(700, 520));

            // CoreResourceDownloadWindow is skipped: its Dalamud icon-font buttons need a live
            // UiBuilder and block outside the game.

            // The disclosure lists the real bundled plugins from the repository lock file.
            var lockFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "vendor", "BundledActPlugins", "bundled-plugins.lock.json"));
            IReadOnlyList<DalamudActCompat.Compatibility.PluginHost.BundledActPluginDescriptor> disclosures = File.Exists(lockFile)
                ? JsonSerializer.Deserialize<DalamudActCompat.Compatibility.PluginHost.BundledActPluginLock>(
                    File.ReadAllText(lockFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Plugins
                : [];
            // Two cards pending, like the design: an upstream update and a bundled version.
            IReadOnlyList<DalamudActCompat.Compatibility.PluginHost.BundledActPluginDescriptor> pendingNotices =
                disclosures.Count >= 5 ? [disclosures[0], disclosures[4]] : disclosures;
            var notice = Construct<ThirdPartyPluginNoticeWindow>(returns: new() { ["getDisclosures"] = disclosures, ["getPending"] = pendingNotices });
            notice.OpenManualDisclosure();
            notice.CompleteUpdateCheck("发现 2 项需要确认：Triggernometry 上游有新版本；抹茶随本插件更新了安装包。", false, false);
            Capture("07 其他窗口", "三方扩展-待确认", notice, new(1000, 640), "三方扩展 · 待确认（1000×640）");
            typeof(ThirdPartyPluginNoticeWindow).GetField("showPermissionChoice", Private)!.SetValue(notice, true);
            typeof(ThirdPartyPluginNoticeWindow).GetField("permissionPopupRequested", Private)!.SetValue(notice, true);
            Capture("07 其他窗口", "三方扩展-权限选择", notice, new(1000, 640), "三方扩展 · 权限选择");
            typeof(ThirdPartyPluginNoticeWindow).GetField("showPermissionChoice", Private)!.SetValue(notice, false);
            DalamudActCompat.UI.Kit.DactUi.CloseDialog("##dact-full-permissions");
            var acknowledged = Construct<ThirdPartyPluginNoticeWindow>(returns: new() { ["getDisclosures"] = disclosures, ["getPending"] = Array.Empty<DalamudActCompat.Compatibility.PluginHost.BundledActPluginDescriptor>() });
            acknowledged.OpenManualDisclosure();
            Capture("07 其他窗口", "三方扩展-声明已确认", acknowledged, new(1000, 640), "三方扩展 · 声明已确认");
            if (jitterCheck)
            {
                Console.WriteLine($"UI jitter check at {io.FontGlobalScale * 100:0}% scale: {jitters.Count} moving windows.");
                foreach (var jitter in jitters) Console.WriteLine("  " + jitter);
            }
        }
        finally
        {
            DactTheme.SetCurrent(new(), false, 0);
            ImGui.DestroyContext(context);
            try { Directory.Delete(root, true); } catch { }
        }

        File.WriteAllText(Path.Combine(output, "index.json"), JsonSerializer.Serialize(new { version = VersionLabel(), shots, failures },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"UI baseline: {shots.Count} captures, {failures.Count} failures -> {output}");
        foreach (var failure in failures) Console.WriteLine("  " + failure);
        return 0;
    }

    private static string VersionLabel()
        => typeof(ControlCenterWindow).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    // Serves image files as textures the software rasterizer can sample, keyed by path so a
    // window that asks for the same file twice gets the same handle.
    public class PreviewTextureProvider : DispatchProxy
    {
        internal NativeUiRasterizer? Raster;
        private readonly Dictionary<string, SkinSmokeTests.EmptyTexture> cache = new(StringComparer.OrdinalIgnoreCase);
        private int next = 5000;

        internal SkinSmokeTests.EmptyTexture Load(string path)
        {
            if (cache.TryGetValue(path, out var cached)) return cached;
            if (!File.Exists(path)) return cache[path] = new SkinSmokeTests.EmptyTexture(next++);
            using var bitmap = new System.Drawing.Bitmap(path);
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y); var offset = (y * bitmap.Width + x) * 4;
                pixels[offset] = c.R; pixels[offset + 1] = c.G; pixels[offset + 2] = c.B; pixels[offset + 3] = c.A;
            }

            var texture = new SkinSmokeTests.EmptyTexture(next++, bitmap.Width, bitmap.Height);
            Raster!.AddTexture(texture.Handle, pixels, bitmap.Width, bitmap.Height);
            return cache[path] = texture;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method is null) return null;
            if (method.Name is "GetFromFile" or "GetFromFileAbsolute" && args is [var source, ..])
                return Load(source is FileInfo info ? info.FullName : (string)source!);
            if (method.ReturnType == typeof(ISharedImmediateTexture)) return new SkinSmokeTests.EmptyTexture(next++);
            return HelpScreenshotGenerator.Neutral(method.ReturnType);
        }
    }
}
