using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DactRepair
{
    internal sealed class RepairResult
    {
        public string BackupDirectory;
        public string Message;
    }

    internal static class RepairEngine
    {
        internal const string PluginName = "DalamudActCompat";
        internal const string WrongVersion = "4.4.0.0";
        internal const string TargetVersion = "0.4.4.0";
        internal const string PackageHash = "17a6b205dad06e616c567cee343d29579c4980016c042719924b9a363ac6ace1";
        internal const string Repository = "https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json";

        internal static string[] Discover()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new[] { "XIVLauncherCN", "XIVLauncher" }.Select(name => Path.Combine(roaming, name))
                .Where(root => Directory.Exists(Path.Combine(root, "installedPlugins", PluginName))).ToArray();
        }

        internal static Stream OpenPackage()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("DACT.Core.zip");
        }

        internal static string RunningProcess()
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try { name = process.ProcessName; }
                    catch (InvalidOperationException) { continue; }
                    if (name.StartsWith("ffxiv", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("XIVLauncher", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("DalamudActCompat.Host", StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
            return null;
        }

        private static void RequireClosed(Func<string> runningProcess)
        {
            var name = runningProcess();
            if (name != null) throw new InvalidOperationException("请先关闭游戏、启动器和 DACT Host，再点击修复。仍在运行：" + name);
        }

        internal static string Hash(Stream stream)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // Refuse junctions at every ancestor, not just the final folder: otherwise
        // a superficially safe path could move another installation or user data.
        internal static string SafePath(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            for (var cursor = full; !String.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            {
                if ((Directory.Exists(cursor) || File.Exists(cursor)) &&
                    (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不处理符号链接或目录联接，请选择实际目录：" + cursor);
            }
            return full;
        }

        private static string Within(string root, string relative)
        {
            var full = SafePath(Path.Combine(root, relative));
            if (!full.StartsWith(SafePath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("路径超出修复目录，操作已停止。");
            return full;
        }

        private static Dictionary<string, object> ReadManifest(string versionDirectory, string version)
        {
            var manifestPath = Within(versionDirectory, PluginName + ".json");
            var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
            if (manifest == null || !manifest.ContainsKey("InternalName") ||
                Convert.ToString(manifest["InternalName"]) != PluginName ||
                !manifest.ContainsKey("AssemblyVersion") || Convert.ToString(manifest["AssemblyVersion"]) != version)
                throw new IOException("插件清单与预期版本不符，未修改安装：" + versionDirectory);
            var dll = Within(versionDirectory, PluginName + ".dll");
            if (AssemblyName.GetAssemblyName(dll).Version.ToString(4) != version)
                throw new IOException("插件 DLL 与清单版本不符，未修改安装：" + versionDirectory);
            return manifest;
        }

        internal static void Extract(Stream package, string stage)
        {
            using (var zip = new ZipArchive(package, ZipArchiveMode.Read, true))
            {
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(name) || name.Contains(":") || name.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
                        throw new IOException("安装包包含越界路径。");
                    var destination = Within(stage, name);
                    total += entry.Length;
                    if (total > 160L * 1024 * 1024) throw new IOException("安装包展开大小超出预期。");
                    if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
            }
        }

        internal static RepairResult Repair(string launcherRoot, Stream package, Action<string> progress,
            Func<string> runningProcess, Action<string> checkpoint = null)
        {
            RequireClosed(runningProcess);
            var root = SafePath(launcherRoot);
            var plugin = Within(root, Path.Combine("installedPlugins", PluginName));
            if (!Directory.Exists(plugin)) throw new IOException("没有找到 DACT。请选择包含 installedPlugins 的启动器数据目录。");
            if (File.Exists(Within(plugin, ".broken")))
                throw new IOException("检测到卫月安装损坏标记；这不是单纯的版本号错误，请保留配置并通过插件列表重新安装。");
            var wrong = Within(plugin, WrongVersion);
            var target = Within(plugin, TargetVersion);
            foreach (var directory in Directory.GetDirectories(plugin))
            {
                SafePath(directory);
                Version version;
                if (Version.TryParse(Path.GetFileName(directory), out version) &&
                    version > new Version(TargetVersion) && version != new Version(WrongVersion))
                    throw new IOException("检测到更新或未知版本，工具不会降级覆盖：" + version);
            }
            if (!Directory.Exists(wrong))
            {
                if (Directory.Exists(target))
                {
                    ReadManifest(target, TargetVersion);
                    return new RepairResult { Message = "当前已是 0.4.4.0，无需进行版本号修复。" };
                }
                throw new IOException("未找到错误编号 4.4.0.0。普通旧版本请在卫月插件列表正常更新。");
            }
            var oldManifest = ReadManifest(wrong, WrongVersion);
            if (package == null || !package.CanSeek) throw new IOException("修复工具缺少内置安装包，请重新下载工具。");
            package.Position = 0;
            if (Hash(package) != PackageHash) throw new IOException("内置安装包校验失败，未修改安装。请重新下载工具。");
            package.Position = 0;
            progress("安装包校验通过，正在准备 0.4.4.0…");
            var backups = Within(root, "DACTRepairBackups");
            var run = Within(backups, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var stage = Within(run, "staged-" + TargetVersion);
            Directory.CreateDirectory(stage);
            var journal = new StringBuilder();
            Action<string> log = message => { journal.AppendLine(DateTime.Now.ToString("HH:mm:ss") + " " + message); progress(message); };
            var undo = new Stack<Action>();
            // Every directory move stays on the same volume. Old code remains in
            // an external backup, since Dalamud deletes lower/non-version folders.
            Action<string, string> move = (source, destination) =>
            {
                var checkedSource = Within(root, source.Substring(root.Length + 1));
                var checkedDestination = Within(root, destination.Substring(root.Length + 1));
                Directory.Move(checkedSource, checkedDestination);
            };
            try
            {
                Extract(package, stage);
                var fresh = ReadManifest(stage, TargetVersion);
                object previous;
                foreach (var key in new[] { "WorkingPluginId", "InstalledFromUrl", "Disabled" })
                    if (oldManifest.TryGetValue(key, out previous)) fresh[key] = previous;
                Guid identity;
                if (!fresh.ContainsKey("WorkingPluginId") || !Guid.TryParse(Convert.ToString(fresh["WorkingPluginId"]), out identity) || identity == Guid.Empty)
                    fresh["WorkingPluginId"] = Guid.NewGuid().ToString();
                if (!fresh.ContainsKey("InstalledFromUrl") || String.IsNullOrEmpty(Convert.ToString(fresh["InstalledFromUrl"]))) fresh["InstalledFromUrl"] = Repository;
                fresh["Testing"] = false;
                fresh["ScheduledForDeletion"] = false;
                File.WriteAllText(Within(stage, PluginName + ".json"), new JavaScriptSerializer().Serialize(fresh), new UTF8Encoding(false));
                RequireClosed(runningProcess);
                using (File.Open(Within(wrong, PluginName + ".dll"), FileMode.Open, FileAccess.Read, FileShare.None)) { }
                if (Directory.Exists(target))
                    using (File.Open(Within(target, PluginName + ".dll"), FileMode.Open, FileAccess.Read, FileShare.None)) { }
                var oldBackup = Within(run, WrongVersion);
                move(wrong, oldBackup);
                undo.Push(() => move(oldBackup, wrong));
                if (checkpoint != null) checkpoint("old-moved");
                if (Directory.Exists(target))
                {
                    var targetBackup = Within(run, "previous-" + TargetVersion);
                    move(target, targetBackup);
                    undo.Push(() => move(targetBackup, target));
                    if (checkpoint != null) checkpoint("target-moved");
                }
                RequireClosed(runningProcess);
                move(stage, target);
                undo.Push(() => move(target, Within(run, "uncommitted-" + TargetVersion)));
                if (checkpoint != null) checkpoint("new-installed");
                ReadManifest(target, TargetVersion);
                log("修复完成：4.4.0.0 → 0.4.4.0。配置未更改，现在可以启动游戏。");
                log("原安装已备份：" + run);
                if (fresh.ContainsKey("Disabled") && Object.Equals(fresh["Disabled"], true))
                    log("插件原本处于禁用状态；进入游戏后请在插件列表启用 DACT。");
                undo.Clear();
                return new RepairResult { BackupDirectory = run, Message = "修复完成。现在可以正常启动游戏。" };
            }
            catch (Exception failure)
            {
                var rollbackFailures = new List<string>();
                while (undo.Count > 0)
                {
                    try { undo.Pop()(); }
                    catch (Exception error) { rollbackFailures.Add(error.Message); }
                }
                var message = rollbackFailures.Count == 0
                    ? "修复未完成，原安装已保留或恢复。原因：" + failure.Message
                    : "修复未完成，自动恢复也遇到问题。请勿删除备份：" + run + "。原因：" + failure.Message + "；" + String.Join("；", rollbackFailures);
                journal.AppendLine(message);
                throw new IOException(message, failure);
            }
            finally
            {
                // A full disk or blocked log file must not undo a successful install.
                try { File.WriteAllText(Within(run, "repair.log"), journal.ToString(), new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
