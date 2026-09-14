# Simulant（仿生石）兼容适配

DACT 0.4.3.0 只提供用户自行导入的 Simulant 的运行时适配。未安装时不显示入口、推荐或专属权限项；产品不下载、不捆绑 Simulant。

## 用户手动导入

1. 用户自行取得原版 `Simulant.dll`，通过 DACT 现有「安装 DLL / ZIP」导入。
2. 导入后，扩展页显示已安装的 Simulant，提供启停和打开原版配置。它在共享 Host 中排在 PostNamazu、MnFeN Triggernometry 两项依赖之后加载。
3. 在同页权限区启用 Simulant 的「原生游戏内存」，以及 PostNamazu 的「游戏命令」「原生游戏内存」。原有完整权限决定不会自动为 Simulant 授权。
4. 打开原版配置，CSV 加载完成后由用户手动初始化。安装、启用、打开配置均不会自动初始化或开启防火墙。
5. 签名扫描出现失败或空结果时，初始化保持失败，原版界面显示具体地址错误。

## 已验证版本与实现

离线验证使用上游 v0.0.4.2，DLL 大小 28,793,856 字节，SHA-256：
`12F31A446D6F137A7D0B39939F9E0361240D9209AC3677624FF95431C2578467`。
安装器保留用户原始 DLL，Host 在加载时转换旧版 WinForms NRBF 资源，继续禁用 BinaryFormatter；内嵌 CSV 与原版模拟逻辑保留。

FFXIV facade 同步绑定，替换 ACT 状态标签无限轮询及对应的无效退订。卸载仍执行原版防火墙恢复，最多等待 5 秒，再卸载 PostNamazu。
原生初始化和防火墙启用同时要求 Simulant 自身原生内存权限与 PostNamazu 的两项原生运行权限。

上游发布说明标注 7.55。本机国服 `2026.09.01.0000.0000`（7.56）的磁盘 `.text` 节中，原版 DLL 的 64 组签名全部存在唯一匹配；这是静态检查，不验证运行时内存布局、地图切换、实体生成或防火墙恢复。
上游目前没有声明许可证，未确认作者对适配的授权；DACT 仅提供兼容代码，不重新分发其源码或 DLL。

## 维护者验证

常规 Package / Host smoke 覆盖手动导入前无安装记录、专用分类、默认权限、全部 8 种权限组合与签名检查。
使用维护者自行提供的固定原版 DLL 运行集成测试：

```powershell
$env:ACTCOMPAT_SIMULANT_DLL = '绝对路径\Simulant.dll'
& 'tests/DalamudActCompat.PackageSmokeTests/bin/Release/net10.0-windows10.0.17763.0/DalamudActCompat.PackageSmokeTests.exe'
& 'tests/DalamudActCompat.HostSmokeTests/bin/Release/net10.0-windows10.0.17763.0/DalamudActCompat.HostSmokeTests.exe' `
  --simulant $env:ACTCOMPAT_SIMULANT_DLL 'vendor/BundledActPlugins'
```

测试加载真实 PostNamazu、Triggernometry 和 Simulant，验证原版界面、CSV、依赖顺序、缺失授权/游戏进程时的错误、Triggernometry 回调、ACT 日志、实体地址与正常卸载。CI 中下载的固定 DLL 仅作为临时测试输入，不进入产品包。
游戏内地图切换、实体生成、机制模拟与防火墙恢复仍需实机验收。
