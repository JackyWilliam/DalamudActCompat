using System.Collections;
using System.Reflection;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Advanced_Combat_Tracker;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static class MeterDisplayScopeSmokeTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    internal static void Run(bool native = false)
    {
        var legacy = JsonConvert.DeserializeObject<PluginConfiguration>("{\"Version\":17,\"Meter\":{\"CompactMode\":true,\"ClassicAllianceView\":true}}")!;
        legacy.ApplyMigrations();
        Check(legacy.Meter.DisplayScope == MeterDisplayScope.MeterSettings && legacy.Meter.CompactMode && legacy.Meter.ClassicAllianceView,
            "Legacy display preferences changed.");
        legacy.Meter.DisplayScope = (MeterDisplayScope)99;
        Check(legacy.ApplyMigrations() && legacy.Meter.DisplayScope == MeterDisplayScope.MeterSettings, "Invalid display mode did not return to the legacy policy.");
        foreach (var mode in Enum.GetValues<MeterDisplayScope>())
        {
            legacy.Meter.DisplayScope = mode;
            var copy = legacy.CreateSnapshot();
            legacy.ResetToDefaults("logs");
            Check(legacy.Meter.DisplayScope == MeterDisplayScope.MeterSettings, "Reset retained follow mode.");
            legacy.RestoreFrom(copy);
            Check(legacy.Meter.DisplayScope == mode && legacy.Meter.CompactMode, "Snapshot restore lost the display mode or compact preference.");
        }

        var select = typeof(MeterWindow).GetMethod("SelectClassicRows", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var localGroup in new[] { 0, 1, 2, 3 })
        {
            var encounter = Encounter(localGroup);
            var config = new PluginConfiguration { ParserScope = ParserScope.All };
            var store = new EncounterStateStore(); store.UpdateCurrent(encounter);
            var meter = new MeterService(store, config.Meter, () => config.ParserScope);
            IReadOnlyList<CombatantRow> Classic()
                => (IReadOnlyList<CombatantRow>)select.Invoke(null, [meter.GetRows(), config.Meter, false])!;
            Check(Classic().Count == 8, "Legacy classic view no longer shows its own eight-player party.");
            config.Meter.CompactMode = true;
            Check(Classic().Count == 1, "Legacy self-only selection changed.");
            config.Meter.HorizontalPartyGroup = 3;
            config.Meter.RoleSplitDamageCompact = config.Meter.RoleSplitHealerCompact = true;
            config.Meter.DisplayScope = MeterDisplayScope.ParserScope;
            foreach (var (scope, expected) in new[] { (ParserScope.All, 32), (ParserScope.Self, 1),
                         (ParserScope.Party, 8), (ParserScope.Alliance, localGroup == 0 ? 8 : 24),
                         (ParserScope.Auto, localGroup == 0 ? 8 : 24), (ParserScope.All, 32) })
            {
                config.ParserScope = scope;
                var rows = Classic();
                Check(rows.Count == expected, $"{scope} group {localGroup}: meter still limits the selected scope ({rows.Count}/{expected}).");
                Check(Math.Abs(meter.GetRows().Sum(row => row.DamagePercent) - 100) < .001,
                    "Damage shares include hidden players.");
                Check(meter.DisplayEncounter!.TotalDamage == expected * 100,
                    "Team totals disagree with the scoped rows.");
                Check(meter.GetRows().Select(row => row.Rank).SequenceEqual(Enumerable.Range(1, expected).Select(rank => (int?)rank)),
                    "Scoped ranks retained gaps from the unfiltered list.");
            }
            config.Meter.DisplayScope = MeterDisplayScope.MeterSettings;
            Check(Classic().Count == 1 && config.Meter.CompactMode && config.Meter.HorizontalPartyGroup == 3 &&
                  config.Meter.RoleSplitDamageCompact && config.Meter.RoleSplitHealerCompact,
                "Switching display scope overwrote template settings.");
            Check(encounter.Combatants.Count == 24 && encounter.ParsedPlayers!.Count == 32 && encounter.TotalDamage == 2400,
                "A display change mutated the stored encounter.");

            var accumulator = new DutyEncounterAccumulator();
            accumulator.Update(encounter, true, At, encounter.ParserContext!.PartyMemberIds, 24);
            var active = encounter with { Id = Guid.NewGuid(), StartTime = At, EndTime = null };
            var merged = accumulator.Update(active, false, At.AddSeconds(10), active.ParserContext!.PartyMemberIds, 24);
            Check(merged.TotalDamage == 4800 && merged.ParsedPlayers!.Sum(player => player.TotalDamage) == 6400,
                "Duty aggregation trimmed non-party players or double-counted parsed data.");
            var finalized = accumulator.Complete(At.AddSeconds(10))!;
            Check(finalized.ParsedPlayers!.Sum(player => player.TotalDamage) == 6400 && finalized.SegmentRecords.Count == 2,
                "Finishing a duty lost parser-view totals.");
            var reloaded = System.Text.Json.JsonSerializer.Deserialize<Encounter>(System.Text.Json.JsonSerializer.Serialize(finalized))!;
            Check(reloaded.ParsedPlayers!.Count == 32 && reloaded.ParserContext!.LocalPartyGroup == localGroup,
                "History serialization lost the alternate data or roster context.");
        }
        VerifyObservedPlayers();
        VerifyRetainedViews();
        VerifyActPublication();
        if (native) NativeUi();
        Console.WriteLine("Meter display scope: old preferences, live five-scope switching, 32-player view, A/B/C, totals/ranks/shares, duty/history retention and player-only identity passed.");
    }

    private static Encounter Encounter(int localGroup)
    {
        var players = Enumerable.Range(0, 32).Select(index => new ActCombatantSnapshot($"Player {index}", $"Player {index}",
            index % 4 == 0 ? "WHM" : "PLD", index == 0, 100, 0, 0,
            PartyGroup: index < 8 ? localGroup : index < 24 ? ((localGroup + (index / 8) - 1) % 3 + 3) % 3 + 1 : 0)).ToArray();
        // A normal party has eight members, but the fixture retains sixteen past
        // allies in its legacy snapshot to catch incorrect group-zero matching.
        var ids = players.Take(localGroup == 0 ? 8 : 24).Select(player => player.Id).ToArray();
        return ActEncounterMapper.Map(new(Guid.NewGuid(), At.AddSeconds(-10), At, "Test", "Boss", players[..24])
        {
            ParsedPlayers = players,
            ParserContext = new(localGroup == 0 ? ParserScope.Party : ParserScope.Alliance, localGroup, ids),
            PartyCapacity = 24,
            CombatDuration = TimeSpan.FromSeconds(10),
        });
    }

    private static void VerifyObservedPlayers()
    {
        var player = EntityServiceProxy.Create<IPlayerCharacter>(new()
        {
            ["Name"] = new SeString(new TextPayload("Outside")), ["EntityId"] = 0x10000099u,
        });
        var npc = EntityServiceProxy.Create<IBattleNpc>(new()
        {
            ["Name"] = new SeString(new TextPayload("Friendly NPC")), ["EntityId"] = 0x40000001u,
        });
        var table = DispatchProxy.Create<IObjectTable, MeterObjectTableProxy>();
        ((MeterObjectTableProxy)(object)table).Objects = [player, npc];
        var observed = Plugin.BuildObservedPlayerIdentities(table);
        Check(observed.Count == 1 && observed[0].Name == "Outside", "A job-bearing NPC entered the player roster.");
        var native = new EncounterData("Self", "Test", false, null!);
        foreach (var name in new[] { "Outside", "Friendly NPC", "Pet", "Boss" }) native.Items.Add(name, new CombatantData(name, native));
        var resolved = SelfHostedActRuntime.ResolveParsedPlayers(native, observed);
        Check(resolved.Count == 1 && resolved[0].Identity.EntityId == 0x10000099u,
            "ACT all-player resolution admitted an NPC/pet or lost the non-party player.");
    }

    private static void VerifyRetainedViews()
    {
        var party = Encounter(2);
        var outside = party with { Id = Guid.NewGuid(), Combatants = [], ParsedPlayers = party.ParsedPlayers!.Skip(24).ToArray() };
        var store = new EncounterStateStore(); store.UpdateCurrent(party); store.UpdateCurrent(outside);
        Check(store.GetDisplayEncounter()!.Id == party.Id && store.GetDisplayEncounter(true)!.Id == outside.Id,
            "An outside-only encounter replaced the retained legacy party view.");
        Check(IinactAdapter.HasMeaningfulActivity(outside), "Outside-only parsed activity was discarded by the adapter.");
        store.ResetCurrent();
        Check(store.GetDisplayEncounter() is null && store.GetDisplayEncounter(true) is null, "Reset left a stale parser view.");
    }

    private static void VerifyActPublication()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = ActGlobals.oFormActMain;
        var form = (FormActMain)RuntimeHelpers.GetUninitializedObject(typeof(FormActMain));
        GC.SuppressFinalize(form);
        ActGlobals.oFormActMain = form;
        Advanced_Combat_Tracker.Resources.NotActMainFormatter.SetupEnvironment();
        ActPlayerIdentity[] roster = [new("Self", "", "PLD", true, false) { EntityId = 0x10000001 }];
        ActPlayerIdentity[] observed = [roster[0], new("Outside", "", "WHM", false, false) { EntityId = 0x10000002 }];
        try
        {
            using var runtime = new SelfHostedActRuntime(null!, DispatchProxy.Create<IPluginLog, MeterRuntimeLogProxy>(),
                null!, () => true, () => "Self", () => roster, () => null, null!,
                DispatchProxy.Create<IFramework, NoOpPluginLogProxy>(), null!,
                () => new(134, 0, EncounterMode.OpenWorld, true, false, false), null!, null!, null!,
                _ => null, () => false, _ => new(), () => new Dictionary<string, HtmlOverlayWindowSettings>(),
                () => { }, () => false, () => false, (_, _) => false, () => ParserScope.All, () => observed);
            var snapshots = new List<(ActEncounterSnapshot Data, bool Finished)>();
            runtime.EncounterChanged += (data, finished) => snapshots.Add((data, finished));
            runtime.UpdateFrameworkState(At);
            var ledger = (EffectiveDamageLedger)typeof(SelfHostedActRuntime).GetField("effectiveDamageLedger", flags)!.GetValue(runtime)!;
            // Raw network actions are authoritative for effective damage. ACT's
            // ordinary MasterSwings intentionally do not add a second damage total.
            var fields = Enumerable.Repeat("0", 47).ToArray();
            fields[0] = "21"; fields[1] = At.ToString("O"); fields[2] = "10000002";
            fields[3] = "Outside"; fields[4] = "7"; fields[5] = "Hit";
            fields[6] = "40000010"; fields[7] = "Boss"; fields[8] = "3";
            fields[9] = "00640000"; fields[24] = "1000"; fields[25] = "1000";
            fields[44] = "00000001";
            ledger.ObserveRawLine(At, string.Join('|', fields));
            ledger.ObserveRawLine(At, $"37|{At:O}|40000010|Boss|00000001|900|");
            var encounter = new EncounterData("Self", "Offline", null!);
            // Feed actual ACT aggregation and the runtime callback, so a helper-only
            // identity test cannot hide lost publication or finish-state propagation.
            foreach (var name in new[] { "Outside", "Friendly NPC", "Pet" })
            {
                var swing = new MasterSwing(2, false, 100, At.LocalDateTime, 1, "Hit", name, "damage", "Boss");
                encounter.AddCombatAction(swing);
                typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatAction", flags)!.Invoke(runtime, [false, new CombatActionEventArgs(swing)]);
            }
            Check(snapshots.Count > 0 && snapshots[^1].Data.ParsedPlayers is { Count: 1 } players &&
                  players[0].Name == "Outside" && players[0].TotalDamage == 100 && snapshots[^1].Data.Combatants.Count == 0,
                $"Native ACT publication lost an outside-only player or admitted NPC/pet rows: {JsonConvert.SerializeObject(snapshots)}.");
            Check((bool)typeof(SelfHostedActRuntime).GetField("activeEncounterPublished", flags)!.GetValue(runtime)!,
                "Outside-only activity did not keep the runtime encounter alive.");
            observed = roster;
            runtime.UpdateFrameworkState(At.AddSeconds(1));
            typeof(SelfHostedActRuntime).GetMethod("OnAfterCombatEnd", flags)!.Invoke(runtime, [encounter]);
            Check(snapshots[^1].Finished && snapshots[^1].Data.ParsedPlayers is { Count: 1 },
                "A player leaving the object table erased the finished encounter.");
        }
        finally { ActGlobals.oFormActMain = previous; }
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
            io.DisplaySize = new(800, 480); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Meter scope UI font failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var config = new PluginConfiguration(); var text = new UiText(config); var saves = 0;
            var combo = Vector2.Zero; var optionHeight = 0f;
            void Frame()
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(730, 330));
                    ImGui.Begin("战斗统计", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                    ImGui.TextUnformatted("榜单模板：经典榜"); ImGui.SetNextItemWidth(300);
                    combo = ImGui.GetCursorScreenPos() + new Vector2(150, ImGui.GetFrameHeight() / 2);
                    optionHeight = ImGui.GetTextLineHeightWithSpacing();
                    if (MeterDisplayScopeSelector.Draw(text, config)) saves++;
                    ImGui.End(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render();
                Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "Scope selector leaked theme state.");
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
                DactTheme.SetCurrent(config.Appearance, true, 1); io.FontGlobalScale = scale;
                foreach (var mode in new[] { MeterDisplayScope.ParserScope, MeterDisplayScope.MeterSettings })
                {
                    Frame(); Frame(); Click(combo); Frame();
                    ImGuiWindowPtr popup = default;
                    for (var i = 0; i < context.Windows.Size; i++)
                        if (context.Windows[i].Active && (context.Windows[i].Flags & ImGuiWindowFlags.Popup) != 0)
                            popup = context.Windows[i];
                    Check(popup.Handle != null, "Meter scope dropdown did not open.");
                    var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
                    if (output is not null && mode == MeterDisplayScope.ParserScope && scale == 1)
                    {
                        Directory.CreateDirectory(output);
                        raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-scope-{skin}.png"));
                    }
                    var prior = saves;
                    Click(new(popup.Pos.X + 45, popup.Pos.Y + popup.WindowPadding.Y + ((int)mode + .5f) * optionHeight));
                    Check(config.Meter.DisplayScope == mode && saves == prior + 1,
                        $"Meter scope click missed {mode} ({skin}, {scale}).");
                }
            }
            VerifyNativeMeters(raster);
        }
        finally { DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }

    private static unsafe void VerifyNativeMeters(NativeUiRasterizer raster)
    {
        var config = new PluginConfiguration(); config.Fflogs.Enabled = false;
        config.Meter.CompactMode = config.Meter.RoleSplitDamageCompact = config.Meter.RoleSplitHealerCompact = true;
        config.Meter.HorizontalPartyGroup = 2;
        var store = new EncounterStateStore(); store.UpdateCurrent(Encounter(2));
        var service = new MeterService(store, config.Meter, () => config.ParserScope);
        var text = new UiText(config);
        var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-scope-icons"));
        using var logo = new SkinSmokeTests.EmptyTexture();
        var classic = new MeterWindow(service, null!, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
        var horizontal = new HorizontalMeterWindow(service, config, text, icons, () => { });
        Window[] windows = [classic, horizontal,
            new RoleSplitMeterWindow(service, config, text, classic, () => { }, RoleSplitGroup.DamageTank),
            new RoleSplitMeterWindow(service, config, text, classic, () => { }, RoleSplitGroup.Healer)];
        var io = ImGui.GetIO(); io.FontGlobalScale = 1; io.DisplaySize = new(1120, 840); io.AddMousePosEvent(-100, -100);
        DactTheme.SetCurrent(new(), false, 0);
        var context = ImGui.GetCurrentContext();
        var offset = typeof(HorizontalMeterWindow).GetField("scrollOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var legacyOffset = 0f;
        foreach (var mode in new[] { MeterDisplayScope.MeterSettings, MeterDisplayScope.ParserScope, MeterDisplayScope.MeterSettings })
        {
            config.Meter.DisplayScope = mode;
            foreach (var window in windows)
            {
                if (window == horizontal) offset.SetValue(horizontal, 1_000_000f);
                // Let live expand/collapse animations settle before checking geometry.
                for (var frame = 0; frame < 30; frame++)
                {
                    ImGui.NewFrame(); window.PreDraw();
                    ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(1050, 740));
                    ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                    window.Draw(); ImGui.End(); window.PostDraw(); ImGui.Render();
                    Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "Scoped meter leaked style state.");
                }
                Check(ImGui.GetDrawData().TotalVtxCount > 100, "Scoped meter rendered no content.");
                if (window == horizontal)
                {
                    var current = (float)offset.GetValue(horizontal)!;
                    if (mode == MeterDisplayScope.MeterSettings) legacyOffset = current;
                    else Check(current > legacyOffset + 1000, "Horizontal meter still truncated the 32-player carousel to its selected party.");
                }
                if (window is RoleSplitMeterWindow role)
                    Check((bool)typeof(RoleSplitMeterWindow).GetProperty("Compact", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(role)!
                        == (mode == MeterDisplayScope.MeterSettings), "Role meter failed to suspend/restore its compact preference.");
                var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
                if (output is not null && mode == MeterDisplayScope.ParserScope)
                    raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-follow-{Array.IndexOf(windows, window)}.png"));
            }
            Check(config.Meter.CompactMode && config.Meter.RoleSplitDamageCompact && config.Meter.RoleSplitHealerCompact &&
                  config.Meter.HorizontalPartyGroup == 2, "Live rendering overwrote saved template audience preferences.");
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public class MeterObjectTableProxy : DispatchProxy
{
    public IGameObject[] Objects = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.Name switch
        {
            "GetEnumerator" when method.ReturnType == typeof(IEnumerator) => Objects.GetEnumerator(),
            "GetEnumerator" => ((IEnumerable<IGameObject>)Objects).GetEnumerator(),
            "get_LocalPlayer" => null,
            _ => throw new NotSupportedException(method.Name),
        };
}

public class MeterRuntimeLogProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "Error") throw new InvalidOperationException("Runtime publication logged an error.", args?.OfType<Exception>().FirstOrDefault());
        return null;
    }
}
