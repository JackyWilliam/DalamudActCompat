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
        RunVersionGuard();
    }

    // A build newer than the newest adapted one does not match the rewriter and used to fail
    // with "Sequence contains no matching element" (v0.1.0.0 did, before it was adapted). It
    // is refused with the reason before anything of it is loaded; adapted versions are not.
    private static void RunVersionGuard()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dact-Simulant-Version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Write(Version version)
            {
                var path = Path.Combine(root, $"Simulant-{version}.dll");
                using var definition = Mono.Cecil.AssemblyDefinition.CreateAssembly(
                    new Mono.Cecil.AssemblyNameDefinition("Simulant", version), "Simulant.dll", Mono.Cecil.ModuleKind.Dll);
                definition.Write(path);
                return path;
            }
            var context = new System.Runtime.Loader.AssemblyLoadContext("simulant-version-guard", isCollectible: true);
            foreach (var newer in new[] { new Version(0, 1, 0, 1), new Version(0, 2, 0, 0), new Version(1, 0, 0, 0) })
            {
                var newerPath = Write(newer);
                try
                {
                    LegacyAssemblyRewriter.LoadSimulant(newerPath, context);
                    throw new InvalidOperationException($"Simulant {newer} was not refused.");
                }
                catch (NotSupportedException refused)
                {
                    Assert(refused.Message.Contains($"v{newer}", StringComparison.Ordinal) &&
                           refused.Message.Contains("v0.1.0.0", StringComparison.Ordinal) &&
                           refused.Message.Contains("尚未适配", StringComparison.Ordinal),
                        "The refusal must name the imported version, the adapted version and the reason.");
                }
            }
            Assert(!context.Assemblies.Any(), "A refused Simulant build must not be loaded.");
            // The adapted version and older ones still reach the rewriter itself: these empty
            // fixtures stop at its first type lookup, not at the version message.
            foreach (var adapted in new[] { new Version(0, 1, 0, 0), new Version(0, 0, 4, 2), new Version(0, 0, 4, 1) })
            {
                try
                {
                    LegacyAssemblyRewriter.LoadSimulant(Write(adapted), context);
                    throw new InvalidOperationException("An empty Simulant fixture cannot be rewritten.");
                }
                catch (TypeLoadException missing)
                {
                    Assert(missing.Message.Contains("Simulant.Core.PluginHost", StringComparison.Ordinal),
                        $"Simulant {adapted} did not reach the rewriter.");
                }
            }
            context.Unload();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Rewrites an upstream build and inspects the result without loading it, so a new build
    /// can be checked on a machine that must not run it. Covers every adapted layout.
    /// </summary>
    public static void RunStatic(string dllPath)
    {
        using var rewritten = LegacyAssemblyRewriter.RewriteSimulant(dllPath);
        using var definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(rewritten);
        var module = definition.MainModule;
        var version = definition.Name.Version;
        var hookRecords = version >= new Version(0, 1, 0, 0);
        var checks = 0;
        Mono.Cecil.MethodDefinition Method(string type, string name)
            => module.GetType(type).Methods.Single(method => method.Name == name);
        int Calls(Mono.Cecil.MethodDefinition method, string name)
            => method.Body.Instructions.Count(instruction =>
                instruction.Operand is Mono.Cecil.MethodReference called && called.Name == name);
        void Check(bool condition, string message)
        {
            Assert(condition, $"Simulant {version}: {message}");
            checks++;
        }

        var attach = Method("Simulant.Core.PluginHost", "Attach");
        Check(Calls(attach, nameof(SimulantCompatibility.GetFfxivPlugin)) == 1 &&
              Calls(attach, nameof(SimulantCompatibility.ObserveZoneChanges)) == (hookRecords ? 1 : 0) &&
              attach.Body.Instructions.Count == (hookRecords ? 6 : 4),
            "Attach was not replaced by the direct binding.");
        var init = Method("Simulant.ACT.NamazuInterop", "Init");
        Check(init.Body.Instructions[0].Operand is Mono.Cecil.MethodReference { Name: nameof(SimulantCompatibility.EnsureNativeAccess) },
            "NamazuInterop.Init does not start with the permission check.");
        var scan = Method("Simulant.Game.SigAddressScanner", "Scan");
        Check(scan.Body.Instructions.Where(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ret)
                .All(instruction => instruction.Previous.Operand is Mono.Cecil.MethodReference { Name: nameof(SimulantCompatibility.ValidateSignatures) }),
            "A signature scan can return without validation.");
        var send = Method("Simulant.Core.Firewall.FirewallService", hookRecords ? "SendHookEnableCore" : "SendHookEnable");
        Check(Calls(send, nameof(SimulantCompatibility.PrepareSendHookInstructions)) == 1 &&
              Calls(send, nameof(SimulantCompatibility.AllocateSendHookMemory)) == 1 &&
              Calls(send, nameof(SimulantCompatibility.PrepareSendEntryPatch)) == 1 &&
              Calls(send, "AllocateMemory") == 0,
            "The send hook is missing one of its three entry patches.");
        var enable = module.GetType("Simulant.Core.Firewall.FirewallService").Methods
            .Single(method => method.Name == "Enable" && method.Parameters.Count == 0);
        var dispose = Method("Simulant.Core.PluginHost", "Dispose");
        if (hookRecords)
        {
            Check(enable.Body.Instructions[0].Operand is Mono.Cecil.MethodReference { Name: nameof(SimulantCompatibility.ValidateSendHookEntry) } &&
                  Calls(enable, "EnableWithRollback") == 1,
                "Enable lost the entry check or the rollback upstream provides.");
            var minimum = init.Body.Instructions.Single(instruction =>
                instruction.OpCode == Mono.Cecil.Cil.OpCodes.Newobj &&
                instruction.Operand is Mono.Cecil.MethodReference { DeclaringType.FullName: "System.Version" });
            Check(minimum.Previous.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_6 &&
                  minimum.Previous.Previous.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_6,
                "The PostNamazu requirement still asks for 1.3.6.7.");
            var updater = module.GetType("Simulant.Core.PluginUpdater").Methods.Single(method => method.Name == "RunAsync");
            Check(updater.Body.Instructions.Count == 2 && Calls(updater, "get_CompletedTask") == 1,
                "The self-updater can still run.");
            var constructors = module.GetType("Simulant.Core.PluginHost").Methods
                .Where(method => method.IsConstructor && method.HasBody).ToArray();
            Check(constructors.Sum(method => Calls(method, "get_StartupPath")) == 0 &&
                  constructors.Sum(method => Calls(method, nameof(SimulantCompatibility.GetConfigurationRoot))) == 1,
                "The configuration file is still under the application folder.");
            // Upstream unsubscribes only what Attach recorded, which is now nothing.
            Check(Calls(dispose, "remove_ProcessChanged") == 0,
                "Dispose unsubscribes an event Attach never subscribed.");
        }
        else
        {
            Check(Calls(enable, nameof(SimulantCompatibility.EnableFirewall)) == 1 && enable.Body.Instructions.Count == 3,
                "Enable was not replaced by the ordered enable with rollback.");
            Check(Calls(dispose, "remove_ProcessChanged") == 0,
                "Dispose still unsubscribes the event Attach no longer subscribes.");
        }
        Check(Calls(Method("Simulant.UI.PresetControl", "btnStart_Click"), nameof(SimulantSimulationCompatibility.CanStart)) == 1,
            "Starting a preset skips the readiness check.");
        Check(Calls(Method("Simulant.Core.Zone.ZoneService", "TryEnterTerritory"), nameof(SimulantSimulationCompatibility.TraceLoad)) == 1,
            "Loading a territory is not traced.");
        Check(Calls(Method("Simulant.ACT.TriggernometryInterop", "GetEntityPtrs"), nameof(SimulantEntityCompatibility.UseLiveEntityPointers)) >= 1,
            "Entity pointers are not read live.");
        Console.WriteLine($"Simulant {version} static rewrite: {checks} checks passed; the assembly was not loaded.");
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
            ValidateImmediateEntityLookup(assembly, tn, namazu);

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

    private static void ValidateImmediateEntityLookup(Assembly assembly, Type tn, Type namazu)
    {
        var manager = assembly.GetType("Simulant.Game.FFCS.Client.Game.Object.GameObjectManager")!;
        var instance = manager.GetProperty("InstancePtr")!;
        var pluginProperty = namazu.GetProperty("Plugin")!;
        var originalInstance = instance.GetValue(null);
        var originalPlugin = pluginProperty.GetValue(null);
        var fixture = new SimulantEntityMemoryFixture();
        var plugin = Activator.CreateInstance(assembly.GetType("Simulant.ACT.NamazuPlugin")!,
            [new SimulantNamazuEntityFixture(fixture)])!;
        try
        {
            pluginProperty.SetValue(null, plugin);
            instance.SetValue(null, new IntPtr(0x10000));
            IntPtr[] Read() => ((IEnumerable)tn.GetMethod("GetEntityPtrs")!.Invoke(null, null)!)
                .Cast<IntPtr>().ToArray();
            fixture.SetPointer(0, 0x234000);
            Assert(Read().SequenceEqual(new[] { new IntPtr(0x234000) }),
                "Initialized Simulant still used the older Host combatant snapshot.");
            // Simulate a synchronous spawn without publishing a new Host snapshot. This
            // traverses the real rewritten iterator and upstream GameObjectPtrs reader.
            fixture.SetPointer(449, 0x345000);
            Assert(Read().SequenceEqual(new[] { new IntPtr(0x234000), new IntPtr(0x345000) }),
                "A newly spawned EObj must be visible before the next IPC snapshot.");
            var reads = fixture.Reads;
            SetPermissions(0);
            Throws(() => Read(), "授权");
            Assert(fixture.Reads == reads, "Denied native access still read the game object table.");
            Console.WriteLine("PASS: immediate EObj lookup through the upstream table before Host snapshot refresh; revoked permission blocks reads.");
        }
        finally
        {
            instance.SetValue(null, originalInstance);
            pluginProperty.SetValue(null, originalPlugin);
            SetPermissions(7);
        }
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

// Public fixture types let the upstream dynamic wrapper bind its real read path.
// Their only memory is a managed byte array; no game process is opened by this test.
public sealed class SimulantEntityMemoryFixture
{
    private readonly byte[] pointers = new byte[819 * 8];
    public int Reads { get; private set; }
    public void SetPointer(int index, long address) => BitConverter.TryWriteBytes(pointers.AsSpan(index * 8, 8), address);
    public byte[] ReadBytes(IntPtr address, int count)
    {
        if (address != new IntPtr(0x10020) || count != pointers.Length)
            throw new InvalidOperationException("Unexpected upstream object table read.");
        Reads++;
        return (byte[])pointers.Clone();
    }
}

public sealed class SimulantNamazuEntityFixture(SimulantEntityMemoryFixture memory)
{
    private readonly object PluginUi = new();
    public object Ui => PluginUi;
    public SimulantEntityMemoryFixture Memory => memory;
}
