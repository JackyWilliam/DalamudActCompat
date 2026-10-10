# 手动 ACT 插件与第三个 Host

2026-10-10：兼容用户提供的 ACTLogsUploader 0.3.3 与繁华的绝神兵泰坦工具。
二者均由第三个通用 ACT Host 加载，需要手动导入和确认信任；未加入随包清单、在线下载或自动安装。
本次为开发分支改动，尚未发布新版本。

## 导入

- ACTLogsUploader：在 DACT 的 ACT 扩展导入入口选择完整 `ACTLogsUploader-0.3.3-loose.zip`。
  也可解压后选择 `ACTLogsUploader.dll`，同目录须保留原包依赖；导入器会补拷贝动态加载的 V8 原生库和 ICU 数据。
  缺少这两项会拒绝安装并保留原有安装。
- 绝神兵工具：先解压用户自己的 RAR，再导入 `神兵科技.dll`；自动识别为“繁华的绝神兵泰坦工具”。
  原工具的队伍宏、优先级、F5–F12 标记宏设置仍按其说明使用。
- 启用后确认该插件自己的信任及能力提示，可从扩展入口打开设置。
  第三个 Host 的停止、重启、卸载和权限变更重启继续使用现有管理入口。

这些是完整信任的第三方插件。进程隔离保护游戏免受 Host 崩溃影响，不是文件/网络系统调用沙箱。
上传器的 FFLogs 登录及自动上传、归档、删除设置仍由用户自行设置；DACT 不接管另一份 ACT 的凭据或清理配置。

## 修复的实际缺口

1. 第三个 Host 发出 `Source=generic` 的 TTS，游戏端原本只接受 `matcha`，请求会被丢弃。
   现在每个连接校验自己的来源，保留长度/空消息检查；权限撤回时拒绝旧会话排队的语音。
2. NotACT 的日志分发要求存在 `BeforeLogLineRead` 订阅者。通用 Host 没有本地解析器，只有
   `OnLogLineRead` 订阅者时会完全收不到日志。现在为已经由游戏端解析的日志提供前置入口。
   TTS 入口也提前到插件初始化之前安装。
3. 上传器原本从 ACT `LogFilePath` 推断上传目录；通用 Host 的该字段是诊断日志路径。
   现在握手传入 DACT 当前网络日志目录，后续目录变更通过有界状态消息同步；有效的手动指定目录优先。
   缺少路径时不猜测其他 ACT 的目录。
4. 上传器的 .NET Framework ClearScript 绑定引用现代 .NET 已移除的 `IExpando`，无法创建 V8 引擎。
   Host 使用官方同版本 7.5.0 的现代托管绑定，V8 原生库/ICU 仍来自用户导入的完整包。
   修正内存加载后的依赖路径、FFLogs 链接启动方式，并把配置和解析器缓存归入通用 Host 配置目录。
5. 绝神兵工具是标记为 x86 的纯托管程序集。仅对已校验版本清除内存副本的 32 位限制；原文件不变。
   标记按键要求该插件自己的 `GameCommand` 和 `NativeSystemAccess`，且仅允许 F5–F12、当前握手 PID 的窗口；
   TTS 要求该插件自己的 `TextToSpeech`。

适配入口按 SHA-256 固定；未知构建明确拒绝改写，避免对新版本猜测内部结构：

| 文件 | 入口 | SHA-256 |
| --- | --- | --- |
| ACTLogsUploader.dll | ACTLogsUploader.Plugin | A2364FCCE5C2CAA9F48134E9D7E5D402A660DD1B393D120B464B77D50E4AA1E7 |
| 神兵科技.dll | JJJSBTT（程序集 ACT.JSB_5） | 0DCFD7F79FA62CB6DAB985FCA44DF93A414D607FFF255CBB2B4D991F37486FE6 |

## 验证

- 原版 ZIP 和 DLL 实际导入：通用 Host 路由、独立能力声明、动态依赖保留、不完整包回滚。
- 两个原版插件在隔离配置目录初始化、卸载和保存；入口 DLL 哈希前后不变。
- 原版上传器 V8 执行、解析器启动/JS 与 C# 消息回传、合成密码的 DPAPI 保存/读取、配置/缓存隔离、日志目录变更。
  解析器使用合成缓存和拒绝联网的 HTTP handler，无真实登录、上传或日志访问。
- 原版绝神兵工具未打开设置窗口时接收队伍宏和三条合成牢狱日志，按顺序发出 TTS 请求；导入日志不触发。
  未授权及撤权后的 TTS/按键拒绝，无游戏 PID 时不查找同名窗口。
- 真实子进程 IPC 覆盖第三个 Host 的日志、导入标记、战斗先于区域的顺序、战斗结束、握手/动态日志目录、TTS、未授权插件排除和退出。
- 完整 Release 构建、Package smoke、Host smoke 通过；发布检查拒绝把两个插件本体、原生 V8/ICU 加入发布包。

仍需真实会话验收：FFLogs 服务登录/上传及现场游戏按键、音频输出。离线测试不代表这些外部行为已验证。

可复现命令：

```powershell
# 使用用户自行提供的文件，不在仓库保存测试 DLL。
$env:ACTCOMPAT_UPLOADER_PACKAGE = '<完整上传器 ZIP>'
$env:ACTCOMPAT_TITAN_DLL = '<解压后的神兵科技.dll>'
& '<PackageSmokeTests.exe>' --manual-plugins-only
& '<HostSmokeTests.exe>' --manual-plugins '<上传器解压目录>' $env:ACTCOMPAT_TITAN_DLL
& '<HostSmokeTests.exe>' '<DalamudActCompat.Host.exe>'
```

设计依据：RCloud 知识《为第三方 ACT 插件建立最小兼容面与独立权限边界》
（e99bbe61-fc6a-4adb-999e-1715f89ee5d8）及《第三方扩展权限应独立告知、单独授权并保持安全默认》
（fc345948-d9dd-48c7-8be8-1a3435a5f5ab）。两项 evidence 的 2026-08-10 原记录未标记过期；
本次仅沿用精确 ABI、最小桥接、专属权限和完整信任边界，实际调用与结果重新用当前 DLL 核对。

ClearScript 托管依赖来自 [Microsoft 官方 7.5.0 包](https://www.nuget.org/packages/Microsoft.ClearScript.V8/7.5.0)，
许可证随 DACT 保存于 `LICENSES/ClearScript-LICENSE.txt`。
