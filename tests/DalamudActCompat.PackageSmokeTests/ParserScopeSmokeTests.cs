using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;
using DalamudActCompat.Meter;
using DalamudActCompat.UI;
using FFXIV_ACT_Plugin.Config;
using FFXIV_ACT_Plugin.Logfile;
using Newtonsoft.Json;

internal static class ParserScopeSmokeTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    public static void Run(bool native = false)
    {
        var legacy = JsonConvert.DeserializeObject<PluginConfiguration>("{\"Version\":17}")!;
        legacy.ApplyMigrations();
        Check(legacy.ParserScope == ParserScope.All, "Legacy configuration no longer parses everyone.");
        foreach (var scope in Enum.GetValues<ParserScope>())
        {
            var configuration = new PluginConfiguration { ParserScope = scope };
            var snapshot = configuration.CreateSnapshot();
            configuration.ResetToDefaults("logs");
            Check(configuration.ParserScope == ParserScope.All, "Factory reset retained the scope.");
            configuration.RestoreFrom(snapshot);
            var reloaded = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(configuration))!;
            reloaded.ApplyMigrations();
            Check(reloaded.ParserScope == scope, "Snapshot restore or subsequent save lost the scope.");
        }
        legacy.ParserScope = (ParserScope)99;
        Check(legacy.ApplyMigrations() && legacy.ParserScope == ParserScope.All,
            "An invalid scope could suppress all statistics.");

        // Exercise ACT's actual event filter, not a second implementation used as
        // the expected result. The native report object needs only its mediator.
        var assembly = Assembly.Load("FFXIV_ACT_Plugin.Parse");
        var reportType = assembly.GetType("FFXIV_ACT_Plugin.Parse.ReportCombatData", true)!;
        var report = RuntimeHelpers.GetUninitializedObject(reportType);
        var mediator = DispatchProxy.Create<ISettingsMediator, ParserSettingsProxy>();
        reportType.GetField("_settingsManager", Fields)!.SetValue(report, mediator);
        var nativeCheck = reportType.GetMethod("CheckParseFilter", Fields)!;
        var wrapper = (IINACT.FfxivActPluginWrapper)RuntimeHelpers.GetUninitializedObject(typeof(IINACT.FfxivActPluginWrapper));
        wrapper.ParseSettings = mediator.ParseSettings;
        Set(wrapper, "configuration", new IINACT.Configuration());
        Set(wrapper, "logFormat", DispatchProxy.Create<ILogFormat, ParserSettingsProxy>());
        Set(wrapper, "logOutput", DispatchProxy.Create<ILogOutput, ParserSettingsProxy>());

        foreach (var localGroup in new[] { 0, 1, 2, 3 })
        {
            var roster = Roster(localGroup);
            var environment = Activator.CreateInstance(assembly.GetType("FFXIV_ACT_Plugin.Parse.EnvironmentState", true)!)!;
            SetProperty(environment, "PlayerId", roster[0].EntityId);
            SetProperty(environment, "PartyCount", (byte)2);
            SetProperty(environment, "PartyMembers", roster.Select(member => member.EntityId).ToList());
            var combatants = (IDictionary)environment.GetType().GetProperty("Combatants")!.GetValue(environment)!;
            var actors = roster.Select(member => (Id: member.EntityId, Owner: member.EntityId))
                .Concat(new (uint Id, uint Owner)[] { (0x10000099u, 0x10000099u), (0x40000010u, 0x40000010u),
                    (0x40000001u, roster[0].EntityId), (0x40000002u, roster[1].EntityId),
                    (0x40000003u, roster[2].EntityId) }).ToArray();
            foreach (var (id, owner) in actors)
            {
                var actor = RuntimeHelpers.GetUninitializedObject(assembly.GetType("FFXIV_ACT_Plugin.Parse.CombatantState", true)!);
                SetProperty(actor, "Id", id); SetProperty(actor, "ParentId", id == owner ? 0u : owner);
                SetProperty(actor, "Name", id.ToString("X8")); combatants.Add(id, actor);
            }
            var selected = ParserScope.All;
            using var runtime = CreateRuntime(roster, () => selected);
            Set(runtime, "parser", wrapper);
            try
            {
                foreach (var scope in Enum.GetValues<ParserScope>())
                {
                    selected = scope;
                    runtime.UpdateFrameworkState(At);
                    var expected = scope == ParserScope.Auto ? ParserScope.Alliance : scope;
                    Check((int)mediator.ParseSettings.ParseFilter == (int)expected,
                        "Framework update did not reach ACT's live settings mediator.");
                    foreach (var source in actors)
                    foreach (var target in actors)
                    {
                        var actual = (bool)nativeCheck.Invoke(report, [environment, source.Id, target.Id, false])!;
                        var supplemental = ParserScopePolicy.IncludesEvent(scope, source.Owner.ToString("X8"),
                            target.Owner.ToString("X8"), roster);
                        Check(actual == supplemental, $"{scope}, group {localGroup}: native/supplemental filter differs for {source}/{target}.");
                    }
                }
            }
            finally { Set(runtime, "parser", null); }
        }
        ValidateAutomatic(wrapper, mediator, assembly, report, nativeCheck);
        ValidateAutomaticFallback();
        ValidateFallback();
        ValidateLedger();
        ValidateClock();
        if (native) NativeUi();
        Console.WriteLine("Parser scope: legacy/reset/restore, auto roster transitions, native live filter, A/B/C parties, pets, incoming events and fallback passed.");
    }

    public static async Task CloudAsync(string root)
    {
        var source = Path.Combine(root, "scope-source"); var destination = Path.Combine(root, "scope-destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        var sourcePaths = new PluginPaths(Path.Combine(source, "DalamudActCompat"));
        var destinationPaths = new PluginPaths(Path.Combine(destination, "DalamudActCompat"));
        var sourceFile = Path.Combine(source, "DalamudActCompat.json");
        var destinationFile = Path.Combine(destination, "DalamudActCompat.json");
        var backups = new PortableConfigurationBackupService(); var key = backups.GenerateRecoveryKey();
        var live = new PluginConfiguration();
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Set(plugin, "configuration", live); Set(plugin, "paths", destinationPaths);
        string? previous = null;
        foreach (var scope in Enum.GetValues<ParserScope>())
        foreach (var displayScope in Enum.GetValues<MeterDisplayScope>())
        {
            var configuration = new PluginConfiguration { ParserScope = scope };
            configuration.Meter.DisplayScope = displayScope;
            await File.WriteAllTextAsync(sourceFile, JsonConvert.SerializeObject(configuration));
            await File.WriteAllTextAsync(destinationFile, JsonConvert.SerializeObject(live));
            var archive = await backups.ExportEncryptedAsync(sourcePaths.ConfigDirectory,
                Path.Combine(root, $"scope-{scope}-{displayScope}.enc"), key, default);
            Check(archive.ContentId != previous, "Scope-only changes were invisible to cloud sync.");
            previous = archive.ContentId;
            await backups.RestoreEncryptedAsync(archive.ArchivePath, destinationPaths.ConfigDirectory,
                Path.Combine(root, $"scope-{scope}-{displayScope}-rollback.enc"), key, default);
            typeof(Plugin).GetMethod("ApplyRestoredConfigurationToMemory", Fields)!.Invoke(plugin, null);
            Check(live.ParserScope == scope && live.Meter.DisplayScope == displayScope,
                "Encrypted restore did not update the running parser/meter scope.");
        }
        Console.WriteLine("Parser scope cloud: content IDs, encrypted restore and live memory application passed (offline).");
    }

    private static void ValidateClock()
    {
        var roster = Roster(2); var selected = ParserScope.Self;
        var tracker = new EncounterDurationTracker((source, target) => ParserScopePolicy.IncludesEvent(selected, source, target, roster));
        tracker.StartEncounter(At, roster);
        tracker.ObserveConfirmedDamage(At, "10000002", "Party", "", "40000010", "Boss");
        tracker.ObserveConfirmedDamage(At.AddSeconds(10), "10000001", "Self", "", "40000010", "Boss");
        tracker.ObserveConfirmedDamage(At.AddSeconds(20), "40000001", "Pet", "10000001", "40000010", "Boss");
        tracker.ObserveConfirmedDamage(At.AddSeconds(30), "10000003", "Alliance", "", "40000011", "Other boss");
        Check(tracker.ResolveDamageMetricDurationSeconds(At.AddSeconds(40), true) == 10,
            "Excluded attacks extended the self/pet DPS clock.");
        selected = ParserScope.Alliance;
        tracker.ObserveConfirmedDamage(At.AddSeconds(40), "10000003", "Alliance", "", "40000010", "Boss");
        Check(tracker.ResolveDamageMetricDurationSeconds(At.AddSeconds(40), true) == 30,
            "A live scope change did not update the DPS clock.");
    }

    private static ActPlayerIdentity[] Roster(int group) =>
    [
        new("Self", "", "SMN", true, false) { EntityId = 0x10000001, PartyGroup = group },
        new("Party", "", "WHM", false, false) { EntityId = 0x10000002, PartyGroup = group },
        new("Alliance", "", "PLD", false, false) { EntityId = 0x10000003, PartyGroup = group == 1 ? 2 : 1 },
    ];

    private static SelfHostedActRuntime CreateRuntime(ActPlayerIdentity[] roster, Func<ParserScope> scope,
        Func<IReadOnlyList<ActPlayerIdentity>>? currentRoster = null)
        => new(null!, DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>(), null!, () => true,
            () => roster[0].Name, currentRoster ?? (() => roster), () => null, null!,
            DispatchProxy.Create<IFramework, NoOpPluginLogProxy>(), null!,
            () => new(134, 0, EncounterMode.OpenWorld, true, false, false), null!, null!, null!,
            _ => null, () => false, _ => new(), () => new Dictionary<string, HtmlOverlayWindowSettings>(),
            () => { }, () => false, () => false, (_, _) => false, scope);

    private static void ValidateAutomatic(IINACT.FfxivActPluginWrapper wrapper, ISettingsMediator mediator,
        Assembly assembly, object report, MethodInfo nativeCheck)
    {
        var solo = Roster(0)[..1];
        ActPlayerIdentity[] Party(int count) => Enumerable.Range(0, count).Select(index =>
            new ActPlayerIdentity(index == 0 ? "Self" : $"Member {index}", "", "PLD", index == 0, false)
            { EntityId = 0x10000001u + (uint)index }).ToArray();
        var fullAlliance = Party(24).Select((member, index) => member with { PartyGroup = index / 8 + 1 }).ToArray();
        var transitions = new (ActPlayerIdentity[] Roster, ParserScope Expected)[]
        {
            ([], ParserScope.All), (solo, ParserScope.Self), (Party(4), ParserScope.Party),
            (Party(8), ParserScope.Party), (fullAlliance, ParserScope.Alliance),
            (Roster(2), ParserScope.Alliance), (Roster(3)[..1], ParserScope.Alliance),
            (Party(4), ParserScope.Party), (solo, ParserScope.Self),
            ([Roster(0)[1]], ParserScope.All), ([], ParserScope.All), (solo, ParserScope.Self),
        };
        var current = solo;
        var selected = ParserScope.Auto;
        using var runtime = CreateRuntime(solo, () => selected, () => current);
        Set(runtime, "parser", wrapper);
        try
        {
            // Drive the live framework setter and ACT's own filter across joins,
            // leaves and partially loaded rosters; never send the DACT-only value 4.
            foreach (var requested in new[] { ParserScope.Auto, ParserScope.All, ParserScope.Self, ParserScope.Party, ParserScope.Alliance })
            foreach (var transition in transitions)
            {
                selected = requested; current = transition.Roster;
                runtime.UpdateFrameworkState(At);
                var expected = requested == ParserScope.Auto ? transition.Expected : requested;
                Check((int)mediator.ParseSettings.ParseFilter == (int)expected,
                    $"Auto/manual live transition selected {mediator.ParseSettings.ParseFilter}, expected {expected}.");
                var environment = Activator.CreateInstance(assembly.GetType("FFXIV_ACT_Plugin.Parse.EnvironmentState", true)!)!;
                var local = current.FirstOrDefault(member => member.IsLocalPlayer);
                // ACT bypasses its filter before PlayerId is available. Auto
                // mirrors that fallback; manual modes retain their existing policy.
                if (local is null && requested != ParserScope.Auto) continue;
                SetProperty(environment, "PlayerId", local?.EntityId ?? 0u);
                SetProperty(environment, "PartyCount", (byte)current.Count(member => member.PartyGroup == local?.PartyGroup));
                SetProperty(environment, "PartyMembers", current.Select(member => member.EntityId).ToList());
                var actors = current.Select(member => member.EntityId).Append(0x10000099u).Append(0x40000010u).ToArray();
                var combatants = (IDictionary)environment.GetType().GetProperty("Combatants")!.GetValue(environment)!;
                foreach (var id in actors)
                {
                    var actor = RuntimeHelpers.GetUninitializedObject(assembly.GetType("FFXIV_ACT_Plugin.Parse.CombatantState", true)!);
                    SetProperty(actor, "Id", id); SetProperty(actor, "ParentId", 0u);
                    SetProperty(actor, "Name", id.ToString("X8")); combatants.Add(id, actor);
                }
                foreach (var source in actors)
                foreach (var target in actors)
                {
                    var native = (bool)nativeCheck.Invoke(report, [environment, source, target, false])!;
                    Check(native == ParserScopePolicy.IncludesEvent(selected, source.ToString("X8"), target.ToString("X8"), current),
                        $"Auto/manual native filter drift: {requested}, {current.Length} members, {source:X8}/{target:X8}.");
                }
            }
        }
        finally { Set(runtime, "parser", null); }
    }

    private static void ValidateAutomaticFallback()
    {
        var alliance = Roster(2);
        var current = alliance[..1].Select(member => member with { PartyGroup = 0 }).ToArray();
        using var runtime = CreateRuntime(current, () => ParserScope.Auto, () => current);
        var snapshots = new List<ActEncounterSnapshot>();
        runtime.EncounterChanged += (snapshot, _) => snapshots.Add(snapshot);
        var hit = typeof(SelfHostedActRuntime).GetMethod("RecordFallbackDamageUnsafe", Fields)!;
        var step = 0;
        // Changing the roster must update fallback filtering without resetting a
        // fight or retrospectively adding previously excluded damage.
        void Observe(ActPlayerIdentity[] next, int expectedTotal, int? expectedVisibleTotal = null)
        {
            current = next;
            var timestamp = At.AddSeconds(step++);
            runtime.UpdateFrameworkState(timestamp);
            foreach (var member in alliance)
                hit.Invoke(runtime, [timestamp, member.Name, "Boss", 100L, "Attack", false, false, "Auto test"]);
            runtime.UpdateFrameworkState(timestamp.AddMilliseconds(250));
            var total = snapshots[^1].Combatants.Sum(member => member.TotalDamage);
            var stored = (Dictionary<string, long>)typeof(SelfHostedActRuntime).GetField("chatDamageTotals", Fields)!.GetValue(runtime)!;
            Check(stored.Values.Sum() == expectedTotal && total == (expectedVisibleTotal ?? expectedTotal),
                $"Auto roster transition {step} lost existing damage or admitted excluded fallback events.");
        }
        Observe(current, 100);
        Observe(alliance[..2].Select(member => member with { PartyGroup = 0 }).ToArray(), 300);
        Observe(alliance, 600);
        // The existing meter hides players who left; their accumulated totals
        // must still remain stored for the same encounter if they rejoin.
        Observe(current[..1].Select(member => member with { PartyGroup = 0 }).ToArray(), 700, 400);
        Observe(alliance, 1000);
    }

    private static void ValidateFallback()
    {
        var roster = Roster(2);
        var method = typeof(SelfHostedActRuntime).GetMethod("RecordFallbackDamageUnsafe", Fields)!;
        foreach (var scope in Enum.GetValues<ParserScope>())
        {
            var selected = scope;
            using var runtime = CreateRuntime(roster, () => selected);
            var snapshots = new List<ActEncounterSnapshot>();
            runtime.EncounterChanged += (snapshot, _) => snapshots.Add(snapshot);
            runtime.UpdateFrameworkState(At);
            void Hit(ActPlayerIdentity member, DateTimeOffset timestamp)
                => method.Invoke(runtime, [timestamp, member.Name, "Boss", 100L, "Attack", false, false, "Scope test"]);
            foreach (var member in roster) Hit(member, At);
            runtime.UpdateFrameworkState(At.AddMilliseconds(250));
            var count = scope switch { ParserScope.Self => 1, ParserScope.Party => 2, _ => 3 };
            Check(snapshots[^1].Combatants.Sum(member => member.TotalDamage) == count * 100,
                "Network/chat fallback reintroduced excluded party damage.");
            selected = ParserScope.All;
            Hit(roster[2], At.AddSeconds(1));
            runtime.UpdateFrameworkState(At.AddMilliseconds(1250));
            Check(snapshots[^1].Combatants.Sum(member => member.TotalDamage) == count * 100 + 100,
                "A live scope change cleared existing statistics or recovered previously excluded damage.");
        }
    }

    private static void ValidateLedger()
    {
        var roster = Roster(3);
        var selected = ParserScope.Self;
        var ledger = new EffectiveDamageLedger((source, target) => ParserScopePolicy.IncludesEvent(selected, source, target, roster));
        ledger.StartEncounter(roster);
        var packet = 0;
        void Action(string source, string target, int amount, int hp, int resultHp, bool healing = false)
        {
            var fields = Enumerable.Repeat("0", 47).ToArray();
            fields[0] = "21"; fields[1] = At.ToString("O"); fields[2] = source; fields[3] = source;
            fields[4] = "07"; fields[5] = "Attack"; fields[6] = target; fields[7] = target;
            fields[8] = healing ? "4" : "3"; fields[9] = ((uint)amount << 16).ToString("X8");
            fields[24] = hp.ToString(); fields[44] = (++packet).ToString("X8");
            ledger.ObserveRawLine(At, string.Join('|', fields));
            ledger.ObserveRawLine(At, $"37|{At:O}|{target}|Boss|{packet:X8}|{resultHp}|");
        }
        Action("10000002", "40000010", 400, 500, 100);
        Action("10000001", "40000010", 200, 100, 1);
        Check(ledger.GetSnapshot().OwnerDamage == 100, "Excluded damage changed attribution or broke the boss HP floor.");
        Action("10000002", "10000003", 100, 1000, 1000, healing: true);
        Check(ledger.TryResolveHealing(roster[1], out var excludedHealing) && excludedHealing == 0,
            "Unrelated healing bypassed self scope.");
        Action("10000002", "10000001", 50, 1000, 1000, healing: true);
        Check(ledger.TryResolveHealing(roster[1], out var healing) && healing == 50, "Incoming healing was filtered out.");
        ledger.ObserveRawLine(At, $"03|{At:O}|40000001|Pet|00|64|10000001|00||");
        Action("40000001", "40000011", 75, 500, 425);
        Check(ledger.GetSnapshot().OwnerDamage == 175, "Self scope dropped the local pet.");
        selected = ParserScope.All;
        Action("10000002", "40000012", 25, 500, 475);
        Check(ledger.GetSnapshot().OwnerDamage == 200, "Scope widening recovered previously excluded raw damage.");
    }

    private static unsafe void NativeUi()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI")!;
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(720, 480); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Scope UI font failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var config = new PluginConfiguration(); var text = new UiText(config); var saves = 0;
            var combo = Vector2.Zero;
            var optionHeight = 0f;
            void Frame()
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(640, 340));
                    ImGui.Begin("解析设置", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                    ImGui.TextUnformatted("基础设置"); ImGui.SetNextItemWidth(350);
                    // Theme spacing is popped after rendering; use the spacing
                    // that actually laid out the popup when targeting its rows.
                    optionHeight = ImGui.GetTextLineHeightWithSpacing();
                    if (ParserScopeSelector.Draw(text, config)) saves++;
                    combo = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
                    ImGui.End(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render();
            }
            void Click(Vector2 position)
            {
                io.AddMousePosEvent(position.X, position.Y); Frame();
                io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
            }
            foreach (var skin in new[] { SkinCatalog.Default, SkinCatalog.Eorzea, SkinCatalog.LiquidGlass, SkinCatalog.Obsidian })
            foreach (var scale in new[] { 1f, 1.4f })
            {
                config.Appearance.SelectedSkin = skin; config.Appearance.UnlockedEasterEggs.Add(skin);
                DactTheme.SetCurrent(config.Appearance, true, 3); io.FontGlobalScale = scale;
                foreach (var scope in new[] { ParserScope.Auto, ParserScope.Self, ParserScope.Party, ParserScope.Alliance, ParserScope.All })
                {
                    Frame(); Frame(); Click(combo); Frame();
                    ImGuiWindowPtr popup = default;
                    // Render has unwound BeginPopupStack, so locate the active combo window.
                    for (var i = 0; i < context.Windows.Size; i++)
                        if (context.Windows[i].Active && (context.Windows[i].Flags & ImGuiWindowFlags.Popup) != 0)
                            popup = context.Windows[i];
                    Check(popup.Handle != null, "Scope dropdown did not open.");
                    var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
                    if (output is not null && scope == ParserScope.Self && scale == 1)
                    {
                        Directory.CreateDirectory(output);
                        raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"parser-scope-{skin}.png"));
                    }
                    var prior = saves;
                    Click(new(popup.Pos.X + 45, popup.Pos.Y + popup.WindowPadding.Y +
                        ((scope == ParserScope.Auto ? 0 : (int)scope + 1) + .5f) * optionHeight));
                    Check(config.ParserScope == scope && saves == prior + 1,
                        $"Scope selection missed {scope} ({skin}, scale {scale}): selected {config.ParserScope}, saves {saves}/{prior + 1}.");
                }
            }
        }
        finally { DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }

    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Fields)!.SetValue(target, value);
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, Fields)!.SetValue(target, value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

public class ParserSettingsProxy : DispatchProxy
{
    private ParseSettings settings = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "get_ParseSettings") return settings;
        if (method.Name == "set_ParseSettings") { settings = (ParseSettings)args![0]!; return null; }
        return method.ReturnType == typeof(string) ? string.Empty : null;
    }
}
