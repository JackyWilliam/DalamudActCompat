using System.Collections.Concurrent;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudActCompatRepair;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly WindowSystem windows = new("DalamudActCompatRepair");
    private readonly RepairWindow window;
    private readonly ConcurrentQueue<string> messages = new();
    private Task<RepairResult>? operation;
    private bool resultShown;
    private readonly string? launcherRoot;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.log = log;
        // Resolve only this launcher's sibling plugin tree. A dev-loaded copy must
        // not guess a real installation from unrelated profiles on the machine.
        var ownDirectory = pluginInterface.AssemblyLocation.Directory?.Parent;
        var pluginsDirectory = ownDirectory?.Parent;
        if (ownDirectory?.Name == "DalamudActCompatRepair" && pluginsDirectory?.Name == "installedPlugins")
            launcherRoot = pluginsDirectory.Parent?.FullName;
        window = new RepairWindow(OpenInstaller, StartRepair) { IsOpen = true };
        windows.AddWindow(window);
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        commands.AddHandler("/dactrepair", new CommandInfo((_, _) => Open()) { HelpMessage = "打开 DACT 版本修复工具" });
    }

    private void Open() => window.IsOpen = true;
    private void OpenInstaller() => pluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, "Dalamud ACT Compat");
    private IExposedPlugin? Dact => pluginInterface.InstalledPlugins.FirstOrDefault(item => item.InternalName == RepairEngine.PluginName && !item.IsDev);

    private void StartRepair()
    {
        if (operation is { IsCompleted: false } || launcherRoot is null) return;
        var dact = Dact;
        if (dact?.IsLoaded == true) { messages.Enqueue("请先在插件列表停用 DACT，再返回修复。"); return; }
        resultShown = false;
        // Public plugin state plus an exclusive DLL open guard against a reload
        // while preparing files. No private Dalamud APIs or forced unloads.
        operation = Task.Run(() =>
        {
            using var package = RepairEngine.OpenPackage();
            return RepairEngine.Repair(launcherRoot, package, messages.Enqueue,
                () => dact?.IsLoaded == true ? "DACT 主插件" : RepairEngine.RunningHost());
        });
    }

    private void Draw()
    {
        window.Loaded = Dact?.IsLoaded == true;
        window.Busy = operation is { IsCompleted: false };
        window.WrongVersionPresent = launcherRoot is not null && Directory.Exists(Path.Combine(launcherRoot, "installedPlugins", RepairEngine.PluginName, RepairEngine.WrongVersion));
        window.Detection = launcherRoot is null ? "请从插件仓库安装本工具；开发加载不会修改真实安装。" :
            window.WrongVersionPresent ? "检测到错误版本：4.4.0.0 → 0.4.4.0" : "没有检测到 4.4.0.0，无需进行此次修复。";
        while (messages.TryDequeue(out var message)) window.Status = message;
        if (operation is { IsCompleted: true } && !resultShown)
        {
            resultShown = true;
            if (operation.IsCompletedSuccessfully)
            {
                window.Status = operation.Result.Message;
                window.Backup = operation.Result.BackupDirectory ?? "";
            }
            else
            {
                var error = operation.Exception?.GetBaseException();
                window.Status = error?.Message ?? "修复没有完成。";
                if (error is not null) log.Error(error, "DACT version repair failed");
            }
        }
        windows.Draw();
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler("/dactrepair");
        windows.RemoveAllWindows();
        // Finish or roll back the short file transaction before this assembly is
        // unloaded. The worker never waits for the UI/framework thread.
        try { operation?.GetAwaiter().GetResult(); }
        catch (Exception error) { log.Error(error, "DACT repair ended without committing"); }
    }
}
