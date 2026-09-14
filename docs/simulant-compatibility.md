# Simulant（仿生石）兼容适配

DACT 0.4.3.0 只提供用户自行导入的 Simulant 的运行时适配。未安装时不显示入口、推荐或专属权限项；产品不下载、不捆绑 Simulant。

## 用户手动导入

1. 用户自行取得原版 `Simulant.dll`，通过 DACT 现有「安装 DLL / ZIP」导入。
2. 手动导入后弹出授权提示，确认前保持禁用；确认后才进入对应 Host。扩展页提供启停、打开原版配置及垃圾桶图标删除按钮。删除前正常停止对应 Host，原文件移入备份目录。Simulant 在共享 Host 中排在 PostNamazu、MnFeN Triggernometry 两项依赖之后加载。
3. 在同页权限区启用 Simulant 的「原生游戏内存」，以及 PostNamazu 的「游戏命令」「原生游戏内存」。原有完整权限决定不会自动为 Simulant 授权。
4. 打开原版配置，CSV 加载完成后由用户手动初始化。安装、启用、打开配置均不会自动初始化或开启防火墙。
5. 签名扫描出现失败或空结果时，初始化保持失败，原版界面显示具体地址错误。

## 已验证版本与实现

离线验证使用上游 v0.0.4.2，DLL 大小 28,793,856 字节，SHA-256：
`12F31A446D6F137A7D0B39939F9E0361240D9209AC3677624FF95431C2578467`。
安装器保留用户原始 DLL，Host 在加载时转换旧版 WinForms NRBF 资源，继续禁用 BinaryFormatter；内嵌 CSV 与原版模拟逻辑保留。

FFXIV facade 同步绑定，替换 ACT 状态标签无限轮询及对应的无效退订。卸载仍执行原版防火墙恢复，最多等待 5 秒，再卸载 PostNamazu。
原生初始化和防火墙启用同时要求 Simulant 自身原生内存权限与 PostNamazu 的两项原生运行权限。

函数入口被已有 Hook 改写时，原版基于实时内存的扫描可能找不到 OnSendPacketFuncPtr / ActionManager.UpdateFuncPtr。仅对这两项失败增加原始游戏映像回查：PE 头和映像大小必须一致（ImageBase 只允许原值或实际 ASLR 基址），签名必须唯一，运行时函数体必须保持一致，入口只接受已知 E9 / FF25 / FF2425 跳转。其他不匹配仍中止初始化。
发包防火墙把已有跳转转换为指向原目标的绝对跳转后放入代码洞，保留 Hook 链；用于恢复入口的原始备份不变。代码洞分配在入口附近，入口只写入 5 字节 E9，避免原版 15 字节覆盖破坏已有 Hook 返回到 +7 / +10 的位置。开启前检查入口，先安装发包 Hook；后续失败时恢复已修改的收发包入口。
2026-09-14 实机确认修复后的地址扫描为 64/64，初始化成功。首次防火墙测试暴露了 15 字节覆盖与已有回跳位置冲突导致的崩溃；短入口修订通过测试进程内真实执行的心跳放行、普通包拦截和恢复回归。用户已确认修订版初始化和防火墙开启成功；只读检查也确认游戏仍在运行，5 字节发包入口及收包拦截补丁已生效。关闭后的实机恢复尚未验收。

地图列表开头是上游预留的空名称记录。勾选“仅显示含预设区域”可显示 755、1122、1238、1363 四个区域；已验证 1122 的两个模拟预设可选择。按用户要求保留原版默认筛选行为。
用户另反馈实机勾选筛选时窗口短暂无响应，随后明确确认已自行恢复正常；暂未确定卡顿原因，未据此修改筛选逻辑。用户仍在继续游戏内测试，防火墙关闭恢复待其完成操作后核对。

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
原生回归仅在测试进程内分配合成函数，复现 FF2425 Hook 返回到入口 +10 的情况，不修改游戏进程。游戏内地图切换、实体生成、机制模拟与修订后的防火墙恢复仍需实机验收。
