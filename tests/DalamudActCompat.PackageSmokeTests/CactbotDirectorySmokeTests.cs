using DalamudActCompat.ActRuntime;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.EventSources;
using System.Drawing;
using System.Windows.Forms;
using Dalamud.Plugin.Services;

internal static class CactbotDirectorySmokeTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "cactbot-directory"); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        var fresh = Path.Combine(root, "fresh");
        var user = Path.Combine(root, "中文 空格 source");
        var externalRoot = Environment.GetEnvironmentVariable("DACT_TEST_EXTERNAL_USER_ROOT");
        var destination = externalRoot is null ? Path.Combine(root, "中文 空格 target")
            : Path.Combine(Path.GetFullPath(externalRoot), "cactbot-user-" + Guid.NewGuid().ToString("N"));
        foreach (var folder in new[] { source, target, fresh, user, destination }) Directory.CreateDirectory(folder);
        Setup(source, user); Setup(target, destination); Setup(fresh, string.Empty);
        File.WriteAllText(Path.Combine(user, "raidboss.js"), "// original trigger");
        Directory.CreateDirectory(Path.Combine(user, "raidboss", "resources"));
        File.WriteAllText(Path.Combine(user, "raidboss", "phase.txt"), "0.0 'start'");
        File.WriteAllBytes(Path.Combine(user, "raidboss", "resources", "sound.wav"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(destination, "old.js"), "old local script");
        var backups = new PortableConfigurationBackupService();
        var key = backups.GenerateRecoveryKey();
        var archive = await backups.ExportEncryptedAsync(Path.Combine(source, "DalamudActCompat"), Path.Combine(root, "first.enc"), key, default);
        Check(backups.IsIncludedPath(Path.Combine(source, "DalamudActCompat"), Path.Combine(user, "raidboss.js")), "External changes are outside sync scope.");
        Check(CactbotDirectoryPicker.Normalize(new Uri(user).AbsoluteUri) == user && CactbotDirectoryPicker.Normalize(" ") == string.Empty,
            "Directory input rejected a file URI or default path.");
        var preview = await backups.PreviewRestoreAsync(archive.ArchivePath, Path.Combine(target, "DalamudActCompat"), key, default);
        Check(preview.Scopes.Single(scope => scope.RelativePath == CactbotUserDirectoryStorage.UserScope).AddedFiles == 3,
            "Preview did not compare the actual external directory.");
        var rollback = Path.Combine(root, "undo.enc");
        await backups.RestoreEncryptedAsync(archive.ArchivePath, Path.Combine(target, "DalamudActCompat"), rollback, key, default);
        Check(CactbotUserDirectoryStorage.Read(target) == destination && File.ReadAllText(Path.Combine(destination, "raidboss.js")) == "// original trigger" &&
              File.ReadAllBytes(Path.Combine(destination, "raidboss", "resources", "sound.wav")).SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
              !File.Exists(Path.Combine(destination, "old.js")), "Custom restore lost the local path, scripts, resources or snapshot replacement semantics.");
        Check(File.ReadAllText(Path.Combine(target, "DalamudActCompat", "Config", "Triggernometry.config.xml")) == "<trigger />" &&
              File.ReadAllText(Path.Combine(target, "DalamudActCompat", "Config", "PostNamazu.config.xml")) == "<postnamazu />",
            "Cactbot mapping damaged another plugin's backup scope.");
        await backups.RestoreEncryptedAsync(rollback, Path.Combine(target, "DalamudActCompat"), Path.Combine(root, "undo-undo.enc"), key, default);
        Check(File.ReadAllText(Path.Combine(destination, "old.js")) == "old local script" && !File.Exists(Path.Combine(destination, "raidboss.js")), "External rollback did not recover the previous directory.");
        await backups.RestoreEncryptedAsync(archive.ArchivePath, Path.Combine(fresh, "DalamudActCompat"), Path.Combine(root, "fresh-undo.enc"), key, default);
        Check(CactbotUserDirectoryStorage.Read(fresh) == string.Empty && File.Exists(Path.Combine(fresh, CactbotUserDirectoryStorage.UserScope, "raidboss.js")),
            "A fresh computer used the source computer's absolute path.");
        var failingArchive = new PortableConfigurationArchiveService { BeforeScopeCommit = (_, scope) =>
        { if (scope == CactbotUserDirectoryStorage.OverlayScope) throw new IOException("Injected failure after the custom directory commit."); } };
        var failing = new PortableConfigurationBackupService(failingArchive, new PortableConfigurationEncryptionService());
        try { await failing.RestoreEncryptedAsync(archive.ArchivePath, Path.Combine(target, "DalamudActCompat"), Path.Combine(root, "failed-undo.enc"), key, default); throw new Exception("Injected failure was ignored."); }
        catch (IOException ex) when (ex.Message.StartsWith("Injected", StringComparison.Ordinal)) { }
        Check(File.ReadAllText(Path.Combine(destination, "old.js")) == "old local script" && !File.Exists(Path.Combine(destination, "raidboss.js")), "Failed restore left the external scope changed.");
        File.AppendAllText(Path.Combine(user, "raidboss.js"), "\n// edited");
        var edited = await backups.ExportEncryptedAsync(Path.Combine(source, "DalamudActCompat"), Path.Combine(root, "edited.enc"), key, default);
        Check(edited.ContentId != archive.ContentId, "External script edits did not change the cloud content fingerprint.");
        VerifyPickerAndLoading(root, user, destination);
        await VerifyNativePickerAsync(user, destination);
        try { await backups.ExportEncryptedAsync(Path.Combine(source, "DalamudActCompat"), Path.Combine(user, "inside.enc"), key, default); throw new Exception("Backup output inside the selected directory was accepted."); }
        catch (InvalidOperationException) { }
        if (externalRoot is not null) Directory.Delete(destination, recursive: true);
        Setup(source, root);
        try { await backups.ExportEncryptedAsync(Path.Combine(source, "DalamudActCompat"), Path.Combine(testRoot, "overlap.enc"), key, default); throw new Exception("Overlapping source escaped validation."); }
        catch (InvalidDataException) { }
        Console.WriteLine("Cactbot directory: custom/default cross-machine encrypted restore, preview, resources, rollback, injected failure, other plugin scopes and change detection passed.");
    }

    private static void VerifyPickerAndLoading(string root, string user, string destination)
    {
        using var container = new TinyIoCContainer();
        container.Register<ILogger>(DispatchProxy.Create<ILogger, NoOpPluginLogProxy>());
        var dispatcherType = typeof(CactbotEventSource).Assembly.GetType("RainbowMage.OverlayPlugin.EventDispatcher")!;
        var dispatcher = Activator.CreateInstance(dispatcherType, [container])!;
        container.Register(dispatcherType, dispatcher);
        var chosen = destination;
        container.Register<ICactbotDirectoryPicker>(new CactbotDirectoryPicker(current => chosen.Length == 0 ? current : chosen));
        using var source = new CactbotEventSource(container);
        var config = new PluginConfig(Path.Combine(root, "picker.config.json"), container);
        config.EventSourceConfigs["CactbotESConfig"] = JObject.Parse("""{"OverlayData":{"options":{"general":{"ReloadOnFileChange":true}}}}""");
        config.EventSourceConfigs["CactbotESConfig"]["OverlayData"]!["options"]!["general"]!["CactbotUserDirectory"] = user;
        source.LoadConfig(config);
        JToken Call(JObject request) => (JToken)dispatcherType.GetMethod("CallHandler")!.Invoke(dispatcher, [request])!;
        Check(Call(new() { ["call"] = "cactbotChooseDirectory" })["data"]!.Value<string>() == destination,
            "The actual OverlayPlugin handler did not use the host picker.");
        chosen = string.Empty;
        Check(Call(new() { ["call"] = "cactbotChooseDirectory" })["data"]!.Value<string>() == user,
            "Cancelling the picker cleared the configured path.");
        var loaded = Call(new() { ["call"] = "cactbotLoadUser", ["overlayName"] = "raidboss" });
        Check(loaded["detail"]!["localUserFiles"]!["raidboss.js"]!.Value<string>()!.Contains("original trigger") &&
              ((JObject)loaded["detail"]!["localUserFiles"]!).Properties().Any(property => property.Name.Replace('\\', '/') == "raidboss/phase.txt"),
            "Cactbot did not read JS/timeline files from the selected directory.");
        var options = (JObject)source.Config.OverlayData["options"].DeepClone();
        options["general"]!["CactbotUserDirectory"] = destination;
        Call(new() { ["call"] = "cactbotSaveData", ["overlay"] = "options", ["data"] = options });
        var watchers = (List<FileSystemWatcher>)typeof(CactbotEventSource).GetField("watchers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        Check(watchers.Count == 1 && watchers[0].Path == destination, "Changing directories left the file watcher on the old path or parent directory.");
        typeof(CactbotEventSource).GetMethod("StopFileWatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, null);
        Console.WriteLine("Cactbot bridge: actual choose/cancel/save/load handlers, JS and timeline loading, and directory watcher rebinding passed.");
    }

    private static void Setup(string root, string user)
    {
        File.WriteAllText(Path.Combine(root, "DalamudActCompat.json"), JsonConvert.SerializeObject(new PluginConfiguration()));
        var config = Path.Combine(root, "DalamudActCompat", "Config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "Triggernometry.config.xml"), "<trigger />");
        File.WriteAllText(Path.Combine(config, "PostNamazu.config.xml"), "<postnamazu />");
        var overlay = JObject.Parse("""{"EventSourceConfigs":{"CactbotESConfig":{"OverlayData":{"options":{"general":{}}}}}}""");
        overlay["EventSourceConfigs"]!["CactbotESConfig"]!["OverlayData"]!["options"]!["general"]!["CactbotUserDirectory"] = user;
        File.WriteAllText(Path.Combine(root, CactbotUserDirectoryStorage.OverlayScope), overlay.ToString());
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    private static async Task VerifyNativePickerAsync(string current, string selected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                using var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000),
                    Size = new(620, 220), ShowInTaskbar = false };
                _ = owner.Handle;
                using var overlay = new HtmlOverlayForm(new Uri("about:blank"), "", "", "Picker test", true, new(), new Size(620, 220), false,
                    DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());
                var field = typeof(HtmlOverlayForm).GetField("form", BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(overlay, owner);
                try
                {
                    foreach (var action in new[] { "select", "cancel", "default", "invalid" })
                    {
                        Exception? failure = null;
                        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
                        timer.Tick += (_, _) =>
                        {
                            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Text.StartsWith("Cactbot user", StringComparison.Ordinal));
                            if (dialog is null) return;
                            timer.Stop(); dialog.Location = new(-30000, -30000);
                            try
                            {
                                var input = dialog.Controls.OfType<TextBox>().Single();
                                Button Button(string text) => dialog.Controls.OfType<Button>().Single(button => button.Text == text);
                                if (action == "cancel") { input.Text = selected; Button("取消").PerformClick(); }
                                else if (action == "default") { Button("使用默认").PerformClick(); Button("确定").PerformClick(); }
                                else if (action == "invalid")
                                {
                                    input.Text = "relative/path"; Button("确定").PerformClick();
                                    Check(dialog.DialogResult == DialogResult.None && dialog.Controls.OfType<Label>().Any(label => label.ForeColor == Color.Firebrick && label.Text.Length > 0),
                                        "Invalid picker input closed the dialog or hid its validation error.");
                                    Button("取消").PerformClick();
                                }
                                else { input.Text = selected; Button("确定").PerformClick(); }
                            }
                            catch (Exception ex) { failure = ex; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
                        };
                        timer.Start();
                        // A real worker invokes the real STA bridge, as the OverlayPlugin
                        // WebSocket handler does; the native dialog pumps its own events.
                        var request = Task.Run(() => overlay.ChooseCactbotDirectory(current));
                        var deadline = DateTime.UtcNow.AddSeconds(8);
                        while (!request.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
                        Check(request.IsCompleted, "The native picker blocked its worker indefinitely.");
                        if (failure is not null) throw failure;
                        Check(request.GetAwaiter().GetResult() == (action == "select" ? selected : action == "default" ? "" : current),
                            $"Native picker failed: {action}.");
                    }
                }
                finally { field.SetValue(overlay, null); }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Cactbot native picker: worker-to-STA dispatch, typed path, cancel, default and invalid input passed.");
    }
}
