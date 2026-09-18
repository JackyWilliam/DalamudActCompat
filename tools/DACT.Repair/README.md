# DACT 修复工具

独立的 Windows 工具，仅处理错误编号 **4.4.0.0 → 0.4.4.0**，不需要先进入游戏或打开 DACT 插件。

[下载 DACT-Repair-1.0.0.exe](https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.4.4.0/DACT-Repair-1.0.0.exe) · [SHA-256](https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.4.4.0/DACT-Repair-1.0.0.sha256.txt)

## 用户操作

1. 关闭 FF14、启动器和 DACT Host。
2. 双击下载的 `DACT-Repair-1.0.0.exe`。
3. 确认启动器数据目录，点击 **一键修复到 0.4.4.0**。
4. 显示修复完成后，正常启动游戏。原先禁用的插件需要在卫月列表中重新启用。

默认识别当前用户的 `%APPDATA%\XIVLauncherCN` 和 `%APPDATA%\XIVLauncher`。有多个安装时手动选择一个；自定义目录请选择包含 `installedPlugins` 的启动器**数据目录**，不是游戏目录。橙月使用相同数据目录时会被识别。

工具内置已公开发布并通过 SHA-256 校验的核心包，修复过程无需联网，不需要安装额外的 .NET 10 运行库；Windows 10/11 自带的 .NET Framework 即可运行。

## 修改范围

- 同步修正安装目录、插件清单和真实 DLL，不会仅改显示编号。0.4.4.0 含换肤影响战斗统计背景的修复。
- 原 4.4.0.0 目录和冲突的已有 0.4.4.0 目录移动至所选数据目录的 `DACTRepairBackups\时间-随机编号\`，不删除它们。日志 `repair.log` 也在这里。
- 保留 `pluginConfigs` 下的账号、皮肤、触发器等配置，不读取或上传其内容；其他插件保持原样。
- 保留原 `WorkingPluginId`、订阅地址和禁用状态，切回稳定版并取消旧安装的删除标记。
- 已修复时不重复修改；发现较新版本、游戏进程、占用的 DLL、损坏标记或目录联接会停止。安装失败自动回滚；若回滚也受文件锁等阻碍，错误中会显示备份位置，此时保留备份并联系维护者。

本工具不修改 FF14 游戏文件，不启动游戏、不申请管理员权限，也不保证修复其他插件故障。0.4.3.1 等普通旧版在卫月中正常更新即可。

## 开发与验证

在 Windows PowerShell / PowerShell 中运行：

```powershell
./tools/DACT.Repair/build.ps1
```

也可通过 `-CorePackage` 指定已下载的 0.4.4.0 原始核心包，仍会核对固定哈希；`-PreviewPath` 输出隐藏窗口的界面预览。产物在 `artifacts/repair`，只分发 `DACT-Repair-1.0.0.exe` 和 `.sha256.txt`，不分发测试夹具或测试程序。

编译器使用 Windows 自带的 .NET Framework `csc.exe`。隔离测试覆盖真实正式包安装、备份、配置和其他插件不变、持久化身份、重复执行、三处事务失败恢复、进程检查、启动竞态、坏包、坏清单、较新版本、文件占用、目录联接与 ZIP 越界；不在开发者实际安装上试跑修复。CI 独立构建并保存可分发资产。
