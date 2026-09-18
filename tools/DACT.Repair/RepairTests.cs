using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DactRepair
{
    internal static class Tests
    {
        private static string root;
        private static string fixture;
        private static int passed;
        private static readonly string Identity = "b0cbd257-7e9b-4e4e-a3d9-76203fcc7210";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                fixture = Path.GetFullPath(args[0]);
                root = Path.Combine(Path.GetTempPath(), "DACT-Repair-Tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                Console.WriteLine("Synthetic test installations only: " + root);
                Success();
                foreach (var point in new[] { "old-moved", "target-moved", "new-installed" }) Rollback(point);
                Reject("running-game", null, () => "ffxiv_dx11");
                Reject("future-version", dir => Directory.CreateDirectory(Path.Combine(dir, "installedPlugins", RepairEngine.PluginName, "0.4.5.0")), () => null);
                Reject("broken-marker", dir => File.WriteAllText(Path.Combine(dir, "installedPlugins", RepairEngine.PluginName, ".broken"), ""), () => null);
                Reject("bad-manifest", dir => File.WriteAllText(Path.Combine(Old(dir), RepairEngine.PluginName + ".json"), "{}"), () => null);
                Reject("corrupt-payload", null, () => null, true);
                Reject("locked-dll", null, () => null, false, true);
                var racing = Create("game-started-during-repair", true);
                var beforeRace = Snapshot(racing); var calls = 0;
                ExpectFailure(() => Run(racing, () => ++calls == 3 ? "ffxiv_dx11" : null), "race");
                Equal(beforeRace, Snapshot(racing), "Game startup during staging lost old files"); Pass("game startup rollback");
                ZipTraversal();
                if (args.Length > 2) { ExpectFailure(() => RepairEngine.SafePath(args[2]), "junction"); Pass("junction refusal"); }
                if (args.Length > 1 && args[1] != "-") Preview(args[1]);
                Console.WriteLine("PASS: " + passed + " repair checks; no real installation or configuration was touched.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static string Old(string dir) { return Path.Combine(dir, "installedPlugins", RepairEngine.PluginName, RepairEngine.WrongVersion); }
        private static string Target(string dir) { return Path.Combine(dir, "installedPlugins", RepairEngine.PluginName, RepairEngine.TargetVersion); }
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void Pass(string name) { passed++; Console.WriteLine("PASS " + name); }

        private static string Create(string name, bool existingTarget)
        {
            var dir = Path.Combine(root, name); Directory.CreateDirectory(Old(dir));
            File.Copy(fixture, Path.Combine(Old(dir), RepairEngine.PluginName + ".dll"));
            var manifest = new Dictionary<string, object>
            {
                { "InternalName", RepairEngine.PluginName }, { "AssemblyVersion", RepairEngine.WrongVersion },
                { "WorkingPluginId", Identity }, { "InstalledFromUrl", "https://example.invalid/preserved-repository.json" },
                { "Disabled", true }, { "Testing", true }, { "ScheduledForDeletion", true }
            };
            File.WriteAllText(Path.Combine(Old(dir), RepairEngine.PluginName + ".json"), new JavaScriptSerializer().Serialize(manifest));
            File.WriteAllText(Path.Combine(Old(dir), "old-resource.marker"), "retain full old installation");
            var config = Path.Combine(dir, "pluginConfigs", RepairEngine.PluginName); Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "private-fixture.json"), "synthetic account, triggers and skin settings");
            var other = Path.Combine(dir, "installedPlugins", "OtherPlugin", "9.0.0.0"); Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "keep.txt"), "other plugin must survive");
            if (existingTarget)
            {
                Directory.CreateDirectory(Target(dir));
                using (var package = RepairEngine.OpenPackage()) RepairEngine.Extract(package, Target(dir));
                File.WriteAllText(Path.Combine(Target(dir), "existing-target.marker"), "retain target too");
            }
            return dir;
        }

        private static string Snapshot(string dir)
        {
            // Backups/staging are deliberately excluded; active files and config
            // must match byte-for-byte after refusal or any rollback checkpoint.
            return String.Join("\n", new[] { "installedPlugins", "pluginConfigs" }.SelectMany(name =>
                Directory.GetFiles(Path.Combine(dir, name), "*", SearchOption.AllDirectories)).OrderBy(path => path).Select(path =>
                { using (var file = File.OpenRead(path)) return path.Substring(dir.Length) + " " + RepairEngine.Hash(file); }));
        }

        private static RepairResult Run(string dir, Func<string> guard = null, Action<string> checkpoint = null)
        {
            using (var package = RepairEngine.OpenPackage())
                return RepairEngine.Repair(dir, package, message => { }, guard ?? (() => null), checkpoint);
        }
        private static void Equal(string a, string b, string message) { Check(a == b, message); }
        private static void ExpectFailure(Action work, string reason)
        {
            try { work(); }
            catch (IOException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected refusal: " + reason);
        }

        private static void Success()
        {
            var dir = Create("success", false);
            var oldBytes = File.ReadAllBytes(Path.Combine(Old(dir), RepairEngine.PluginName + ".json"));
            var result = Run(dir);
            Check(!Directory.Exists(Old(dir)) && Directory.Exists(Target(dir)), "Version directories not migrated");
            Check(File.ReadAllBytes(Path.Combine(result.BackupDirectory, RepairEngine.WrongVersion, RepairEngine.PluginName + ".json")).SequenceEqual(oldBytes), "Backup manifest changed");
            Check(File.ReadAllText(Path.Combine(dir, "pluginConfigs", RepairEngine.PluginName, "private-fixture.json")) == "synthetic account, triggers and skin settings", "Configuration changed");
            Check(File.ReadAllText(Path.Combine(dir, "installedPlugins", "OtherPlugin", "9.0.0.0", "keep.txt")) == "other plugin must survive", "Other plugin changed");
            var fresh = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Target(dir), RepairEngine.PluginName + ".json")));
            Equal(Convert.ToString(fresh["WorkingPluginId"]), Identity, "Window persistence identity changed");
            Equal(Convert.ToString(fresh["InstalledFromUrl"]), "https://example.invalid/preserved-repository.json", "Subscription changed");
            Check(Object.Equals(fresh["Disabled"], true) && Object.Equals(fresh["Testing"], false) && Object.Equals(fresh["ScheduledForDeletion"], false), "Local installation flags incorrect");
            var before = Snapshot(dir); var repeated = Run(dir);
            Equal(before, Snapshot(dir), "Repeat changed installed bytes");
            Check(repeated.BackupDirectory == null, "Already repaired installation created another backup");
            Pass("offline migration, exact backup, config/other plugin/identity preservation and idempotence");
        }

        private static void Rollback(string point)
        {
            var dir = Create(point, true); var before = Snapshot(dir);
            ExpectFailure(() => Run(dir, null, at => { if (at == point) throw new IOException("Injected failure at " + point); }), point);
            Equal(before, Snapshot(dir), "Rollback changed files at " + point); Pass("rollback " + point);
        }

        private static void Reject(string name, Action<string> setup, Func<string> guard, bool corrupt = false, bool locked = false)
        {
            var dir = Create(name, false); if (setup != null) setup(dir); var before = Snapshot(dir);
            using (var payload = RepairEngine.OpenPackage())
            using (var bytes = new MemoryStream())
            {
                payload.CopyTo(bytes); if (corrupt) { bytes.Position = 10; bytes.WriteByte((byte)(bytes.GetBuffer()[10] ^ 1)); }
                using (var handle = locked ? File.Open(Path.Combine(Old(dir), RepairEngine.PluginName + ".dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                    ExpectFailure(() => RepairEngine.Repair(dir, bytes, message => { }, guard), name);
            }
            Equal(before, Snapshot(dir), "Refusal changed active files: " + name); Pass(name);
        }

        private static void ZipTraversal()
        {
            var stage = Path.Combine(root, "zip-stage"); Directory.CreateDirectory(stage);
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                using (var text = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) text.Write("must not escape");
                memory.Position = 0; ExpectFailure(() => RepairEngine.Extract(memory, stage), "zip traversal");
            }
            Check(!File.Exists(Path.Combine(root, "escape.txt")), "Archive escaped staging"); Pass("zip traversal refusal");
        }

        private static void Preview(string path)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new RepairForm(false))
            {
                var combo = (ComboBox)typeof(RepairForm).GetField("installations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
                combo.Items.Add(@"C:\Users\Example\AppData\Roaming\XIVLauncherCN"); combo.SelectedIndex = 0;
                // Force handles for a hidden form; never show a test window over
                // the user's running applications merely to capture a preview.
                Action<Control> prepare = null;
                prepare = control =>
                {
                    typeof(Control).GetMethod("CreateControl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { typeof(bool) }, null).Invoke(control, new object[] { true });
                    control.PerformLayout();
                    foreach (Control child in control.Controls) prepare(child);
                };
                prepare(form);
                combo.SelectedIndex = -1; combo.SelectedIndex = 0;
                Check(combo.Text.Contains("Example"), "Detected installation selection is not displayed");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path); }
            }
            Pass("standalone UI render without scanning real installations");
        }
    }
}
