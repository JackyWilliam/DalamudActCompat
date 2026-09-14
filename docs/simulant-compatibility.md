# Simulant（仿生石）接入

本适配针对 [MnFeN/Simulant v0.0.4.2](https://github.com/MnFeN/Simulant/releases/tag/v0.0.4.2)。
上游发布说明标注游戏 7.55；DACT 的解析器支持 7.56，并不代表 Simulant 的所有内存布局已经通过 7.56 实机验收。

## 使用

1. 打开 DACT「控制中心 → 扩展」，在「仿生石 / Simulant」处点击「下载并安装」。
2. 安装并启用现有的 PostNamazu、MnFeN Triggernometry。三者在同一个共享 Host 中运行；Simulant 在这两个依赖之后加载。
3. 在同页权限区启用 Simulant 的「原生游戏内存」，以及 PostNamazu 的「游戏命令」「原生游戏内存」。保存后由现有流程重启 Host。Simulant 不沿用其他扩展的默认完整授权。
4. 点击 Simulant「打开配置」。CSV 加载完成后，由用户点击原版界面的初始化按钮。安装、启用或打开界面均不会自动初始化或启用防火墙。
5. 全部签名通过才会开放模拟功能。缺少签名时查看原版日志中的地址名称，核对上游支持的游戏版本；不要把「DLL 已加载」等同于「游戏模拟已验证」。

支持通过现有「安装 DLL / ZIP」导入原版 `Simulant.dll`；已知发布版内置所需 CSV，无需额外下载表格。
专用按钮固定下载经过离线验证的 v0.0.4.2，不会自动切换到尚未适配的上游版本。

## 下载与运行时

- 下载地址：`https://github.com/MnFeN/Simulant/releases/download/v0.0.4.2/Simulant.dll`
- 大小：28,793,856 字节。
- SHA-256：`12F31A446D6F137A7D0B39939F9E0361240D9209AC3677624FF95431C2578467`。
- DLL 直接从上游下载，验证大小和 SHA-256 后才交给现有安装器。未将上游二进制重新打入随包扩展组件；安装文件保持原样，适配仅在 Host 加载时进行。
- 将旧版 WinForms 的 NRBF 界面资源转换为 .NET 10 可读取的资源，继续禁用 BinaryFormatter。
- 同步绑定现有 FFXIV facade，移除对 ACT 状态标签的无限轮询，以及对应的无效退订；保留上游其余卸载逻辑，包括防火墙恢复。
- 原生初始化和防火墙启用均要求 Simulant 自身与 PostNamazu 的权限。签名扫描结果出现缺失或为空时抛出明确错误，使原版初始化保持失败状态。
- Host 按相反顺序卸载扩展，为 Simulant 的恢复操作留出最多 5 秒，再卸载 PostNamazu。

## 验证

常规 Package / Host smoke 包含专用分类、默认权限、全部 8 种权限组合、扫描结果、下载大小与哈希拒绝路径。

使用原版 DLL 的集成测试（不会提供游戏 PID，不会启用防火墙）：

```powershell
$env:ACTCOMPAT_SIMULANT_DLL = '绝对路径\Simulant.dll'
& 'tests/DalamudActCompat.PackageSmokeTests/bin/Release/net10.0-windows10.0.17763.0/DalamudActCompat.PackageSmokeTests.exe'
& 'tests/DalamudActCompat.HostSmokeTests/bin/Release/net10.0-windows10.0.17763.0/DalamudActCompat.HostSmokeTests.exe' `
  --simulant $env:ACTCOMPAT_SIMULANT_DLL 'vendor/BundledActPlugins'
```

集成测试加载真实 PostNamazu、Triggernometry 和 Simulant，检查原版界面、CSV、依赖顺序、缺失授权/游戏进程时的错误、Triggernometry 回调、ACT 日志、实体地址与正常卸载。
游戏内地图切换、实体生成、机制模拟及防火墙恢复仍需在用户客户端中验证；离线测试不替代这些验收。
