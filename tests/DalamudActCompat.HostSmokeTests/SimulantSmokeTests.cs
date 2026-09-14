using System.Collections;
using System.Reflection;
using System.Text.Json;
using Advanced_Combat_Tracker;
using DalamudActCompat.Host;
using DalamudActCompat.Protocol;

internal static class SimulantSmokeTests
{
    private const BindingFlags StaticInternal = BindingFlags.Static | BindingFlags.NonPublic;

    public static void RunGuards()
    {
        SimulantEntryPointSmokeTests.Run();
        var field = typeof(HostPluginBridge).GetField("permissions", StaticInternal)!;
        var original = field.GetValue(null);
        try { RunGuardCases(); }
        finally { field.SetValue(null, original); }
    }

    private static void RunGuardCases()
    {
        Assert(SimulantSimulationCompatibility.GetStartError(true, true, false, true, 1122, 1122) is null,
            "A loaded, matching simulation territory must remain startable.");
        Assert(SimulantSimulationCompatibility.GetStartError(true, true, false, false, 1122, 177)!.Contains("加载区域"),
            "Starting back in the real inn must explain the missing zone load.");
        Assert(SimulantSimulationCompatibility.GetStartError(true, true, true, true, 1122, 1122)!.Contains("正在加载"),
            "Starting before the phase is ready must be rejected.");
        Assert(SimulantSimulationCompatibility.GetStartError(true, true, false, true, 1122, 177)!.Contains("不一致"),
            "A stale zone-loaded flag must not start the wrong territory.");
        Assert(SimulantSimulationCompatibility.GetStartError(true, false, false, true, 1122, 1122)!.Contains("防火墙"),
            "A loaded map without the firewall must not start simulation.");
        for (var mask = 0; mask < 8; mask++)
        {
            SetPermissions(mask);
            Assert(HostPluginBridge.IsSimulantNativeRuntimeAllowed() == (mask == 7),
                "Simulant must require its own memory grant and both PostNamazu native grants.");
            if (mask != 7)
            {
                Throws(SimulantCompatibility.EnsureNativeAccess, "授权");
            }
        }
        var success = new Dictionary<string, string> { ["Resolved"] = null! };
        Assert(ReferenceEquals(SimulantCompatibility.ValidateSignatures(success), success),
            "A complete scan must preserve upstream results.");
        Throws(() => SimulantCompatibility.ValidateSignatures([]), "扫描");
        Throws(() => SimulantCompatibility.ValidateSignatures(new() { ["MissingFunction"] = "not found" }),
            "MissingFunction");
        SetPermissions(0);
    }

    public static void RunOriginal(string dllPath, string bundledRoot)
    {
        // This probe never supplies a game PID, grants native permissions only to exercise
        // readiness errors, and never enables the firewall or invokes game functions.
        var root = Path.Combine(Path.GetTempPath(), "Dact-Simulant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGuards();
        var pluginRoot = Path.Combine(root, "plugins");
        foreach (var id in new[] { "postnamazu", "triggernometry" })
        {
            var source = Path.Combine(bundledRoot, id);
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(pluginRoot, id, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
            }
        }
        var simulantRoot = Path.Combine(pluginRoot, "simulant");
        Directory.CreateDirectory(simulantRoot);
        File.Copy(dllPath, Path.Combine(simulantRoot, "Simulant.dll"));
        File.WriteAllText(Path.Combine(simulantRoot, "actcompat.plugin.json"),
            JsonSerializer.Serialize(new { Id = "simulant", EntryAssembly = "Simulant.dll", EntryType = "Simulant.PluginMain" }));
        var runtimeType = typeof(HostPluginBridge).Assembly.GetType("DalamudActCompat.Host.LegacyPluginRuntime")!;
        using var runtime = (IDisposable)Activator.CreateInstance(runtimeType,
            [pluginRoot, Path.Combine(root, "config"), new[] { "postnamazu", "triggernometry", "simulant" }, true])!;
        var errors = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(TextWriter.Synchronized(errors));
        try
        {
            runtimeType.GetMethod("Start")!.Invoke(runtime, null);
            var loaded = ((IEnumerable<string>)runtimeType.GetProperty("LoadedPluginIds")!.GetValue(runtime)!).ToArray();
            Assert(loaded.SequenceEqual(new[] { "triggernometry", "postnamazu", "simulant" }),
                "Simulant must load after both upstream dependencies in the shared Host.");
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Simulant");
            var hostType = assembly.GetType("Simulant.Core.PluginHost")!;
            var host = hostType.GetProperty("Instance")!.GetValue(null)!;
            var internalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var csvReady = hostType.GetProperty("CsvReady", internalInstance)!;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(bool)csvReady.GetValue(host)! && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert((bool)csvReady.GetValue(host)!, "Original embedded CSVs did not finish loading.");
            Assert(!(bool)hostType.GetProperty("PluginReady", internalInstance)!.GetValue(host)!,
                "Loading the extension must not initialize native simulation.");
            Assert(!(bool)hostType.GetProperty("FirewallEnabled", internalInstance)!.GetValue(host)!,
                "Loading the extension must not enable its firewall.");
            ValidateTerritoryUi(assembly, host);

            var namazu = assembly.GetType("Simulant.ACT.NamazuInterop")!;
            Throws(() => namazu.GetMethod("Init")!.Invoke(null, null), "授权");
            SetPermissions(7);
            SimulantCompatibility.EnsureNativeAccess();
            Throws(() => namazu.GetMethod("Init")!.Invoke(null, null), "未准备就绪");
            var tn = assembly.GetType("Simulant.ACT.TriggernometryInterop")!;
            tn.GetMethod("Init")!.Invoke(null, null);
            tn.GetMethod("InvokeNamedCallback")!.Invoke(null, ["DACT_SIMULANT_SMOKE", "offline"]);
            tn.GetMethod("QueueACTLogEvent")!.Invoke(null, ["00|DACT Simulant offline test"]);
            var entities = ((IEnumerable)tn.GetMethod("GetEntityPtrs")!.Invoke(null, null)!).Cast<object>().ToArray();
            Assert(entities.Length == 0, "The offline facade unexpectedly exposes a game entity.");
            var combatant = JsonSerializer.Deserialize<HostFfxivCombatant>(
                """{"Id":268435457,"Type":1,"Name":"Simulant fixture","Address":305419896,"Statuses":[]}""")!;
            typeof(HostPluginBridge).GetMethod("ApplyFfxivEntitySnapshot", StaticInternal)!.Invoke(null,
                [new HostFfxivEntitySnapshot(1, combatant.Id, DateTimeOffset.UtcNow, [combatant])]);
            entities = ((IEnumerable)tn.GetMethod("GetEntityPtrs")!.Invoke(null, null)!).Cast<object>().ToArray();
            Assert(entities.Length == 1 && (IntPtr)entities[0] == new IntPtr(305419896),
                "Simulant did not receive the entity address from DACT through real Triggernometry.");

            // No process subscription is registered against the facade. Unloading must still
            // run the rest of upstream Dispose without a null DataSubscription exception.
            runtime.Dispose();
            Assert((bool)hostType.GetProperty("Disposed")!.GetValue(host)!, "Original Dispose was skipped.");
            Assert(hostType.GetProperty("Instance")!.GetValue(null) is null, "Original plugin instance survived unload.");
            Assert(!errors.ToString().Contains("DeInit failed", StringComparison.Ordinal), errors.ToString());
            Console.WriteLine("PASS: Simulant original DLL, CSVs, shared dependencies, permissions, TN callbacks/log/entities, and unload.");
        }
        finally
        {
            SetPermissions(0);
            Console.SetError(originalError);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException)
            {
                // Default-context upstream assemblies remain mapped until this probe exits.
                Console.WriteLine($"Simulant fixture retained until process exit: {root}");
            }
        }
    }

    private static void ValidateTerritoryUi(Assembly assembly, object host)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var ui = (System.Windows.Forms.Control)host.GetType().GetField("_ui", fields)!.GetValue(host)!;
        // CsvReady alone misses failures in deferred row getters and UI event handlers.
        // Exercise the upstream selector on its owning thread without starting simulation.
        ui.Invoke((Action)(() =>
        {
            using var form = (System.Windows.Forms.Form)Activator.CreateInstance(
                assembly.GetType("Simulant.UI.TerritoryForm")!)!;
            var grid = (System.Windows.Forms.DataGridView)form.GetType()
                .GetField("dgvTerritory", fields)!.GetValue(form)!;
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            System.Windows.Forms.Application.DoEvents();
            Console.WriteLine($"Simulant displayed grid: {grid.Bounds}, visible={grid.Visible}, rows={grid.RowCount}, displayed={grid.DisplayedRowCount(false)}");
            var capture = Environment.GetEnvironmentVariable("ACTCOMPAT_SIMULANT_UI_CAPTURE");
            if (!string.IsNullOrEmpty(capture))
            {
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(capture);
            }
            Console.WriteLine($"Simulant territory rows: {grid.RowCount}");
            Assert(grid.RowCount > 1122, "Original territory selector is empty.");
            Assert(Convert.ToString(grid.Rows[1122].Cells[0].Value) == "1122",
                "Virtual territory cells did not return map IDs.");
            Assert(!string.IsNullOrWhiteSpace(Convert.ToString(grid.Rows[1122].Cells[2].Value)),
                "The preset territory has no instance name.");
            var presetOnly = (System.Windows.Forms.CheckBox)form.GetType()
                .GetField("chkPresetOnly", fields)!.GetValue(form)!;
            presetOnly.Checked = true;
            Assert(grid.RowCount > 0 && grid.RowCount < 100, "Preset-only territory filter is empty.");
            foreach (System.Windows.Forms.DataGridViewRow row in grid.Rows)
                Console.WriteLine($"Simulant preset map: {row.Cells[0].Value} | {row.Cells[1].Value} | {row.Cells[2].Value}");
            var search = (System.Windows.Forms.TextBox)form.GetType()
                .GetField("txtFilter", fields)!.GetValue(form)!;
            search.Text = "^1122$";
            Assert(grid.RowCount == 1 && Convert.ToString(grid.Rows[0].Cells[0].Value) == "1122",
                "Map search did not find the known preset territory.");
            var map = (System.Windows.Forms.NumericUpDown)ui.GetType()
                .GetField("numTerritoryId", fields)!.GetValue(ui)!;
            map.Value = 1122;
            var phases = (System.Windows.Forms.ComboBox)ui.GetType().GetField("cbxPhase", fields)!.GetValue(ui)!;
            var presets = phases.Items.Cast<object>().Where(item => item.ToString()!.StartsWith("[模拟]", StringComparison.Ordinal)).ToArray();
            Assert(presets.Length >= 2, "Known territory did not populate its simulation presets.");
            foreach (var preset in presets) phases.SelectedItem = preset;
            var presetControl = (System.Windows.Forms.Control)ui.GetType().GetField("presetControl", fields)!.GetValue(ui)!;
            // Exercise the rewritten real click handler, not only the helper: browsing a
            // preset before initialization must not create a session or invoke native code.
            presetControl.GetType().GetMethod("btnStart_Click", fields)!.Invoke(presetControl, [null, EventArgs.Empty]);
            Assert(presetControl.GetType().GetField("_session", fields)!.GetValue(presetControl) is null,
                "Original start handler created a session despite missing readiness.");
            Console.WriteLine($"PASS: Simulant map names, preset-only filter, search, and {presets.Length} selectable presets for 1122.");
        }));
    }

    private static void SetPermissions(int mask)
    {
        var postNamazu = new List<string>();
        if ((mask & 2) != 0) postNamazu.Add("GameCommand");
        if ((mask & 4) != 0) postNamazu.Add("NativeGameMemory");
        typeof(HostPluginBridge).GetMethod("ConfigurePermissions", StaticInternal)!.Invoke(null,
        [new HostPermissionSnapshot(new Dictionary<string, IReadOnlyList<string>>
        {
            ["simulant"] = (mask & 1) != 0 ? ["NativeGameMemory"] : [],
            ["postnamazu"] = postNamazu,
            ["triggernometry"] = ["ReadCombatLogs", "ReadLocalConfiguration"],
        }, ["simulant", "postnamazu", "triggernometry"])]);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception.GetBaseException().Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Expected failure containing: {message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
