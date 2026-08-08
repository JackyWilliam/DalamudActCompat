# 第三方 ACT 扩展兼容说明

核对日期：2026-07-31。

国服与国际服当前同步在 7.55 版本进度；区域网络操作码仍按各客户端实际值分别维护。
解析链已更新到 IINACT 2.10.3.4、OverlayPlugin Core 0.19.103、
Machina 7.55、Unscrambler.XIV 7.55.0 和 FFXIV_ACT_Plugin 3.0.2.7。

Triggernometry 中文维护版 2.1.1.2、ACT.FoxTTS 3.3.1.189 和
PostNamazu 1.3.6.6 随本插件发布。首次安装以及 DalamudActCompat 每次更新后，
游戏内都会弹出告知窗口，展示 DLL 作者、当前维护者、版本、许可证、项目网址、
源码网址、DLL 实际下载网址和 SHA-256；窗口不会自动打开网页，未确认时不会加载
这三项 DLL。上述信息仅说明组件来源，不代表原作者或维护者与本项目存在合作、
认可或联系。插件每次启动还会检查公开上游：Triggernometry 使用公开提供的
DLL/汉化文件地址，FoxTTS 和 PostNamazu 使用 GitHub 最新 Release；发现文件版本
或哈希变化后先停止旧扩展的后续加载并再次弹窗，确认后才安装下载缓存中的版本。
单个来源离线或检查失败时保留经过锁定校验的随包版本，不影响主体启动。

| 组件 | 当前维护来源 | 发布物/安装模型 | 当前兼容范围 |
| --- | --- | --- | --- |
| Cactbot | [OverlayPlugin/cactbot](https://github.com/OverlayPlugin/cactbot) | 官方 Release ZIP；OverlayPlugin addon，包含 DLL、UI、资源与时间轴 | 可独立安装完整资源；内置 Cactbot 事件源；HTML Overlay 编辑模式禁用 WebView2 输入，由宿主检测全局鼠标并让唯一目标窗口取得 Windows 鼠标捕获，同时只在可见编辑窗口下方启用完全透明的 Dalamud 鼠标输入层，阻止游戏重置光标且不切换外部窗口焦点；锁定后移除输入层并恢复网页交互，等待游戏内联动验收 |
| CactbotSelf / MoreLogLine | [tssailzz8/cacbotSelf](https://github.com/tssailzz8/cacbotSelf) | `CactbotSelf.dll` 或作者 ZIP；ACT + OverlayPlugin 扩展 | 可安装和加载；额外日志/摄像机/标点能力需游戏内逐项实测 |
| PostNamazu | [Natsukage/PostNamazu](https://github.com/Natsukage/PostNamazu) | `PostNamazu.dll` 或作者 ZIP；普通 ACT 插件，同时联动 FFXIV_ACT_Plugin、OverlayPlugin、Triggernometry | 已移至独立 Host；程序集、`InitPlugin`、ACT/FFXIV facade、日志、Clipboard 和语义命令 Broker 分阶段显示状态。“复制日志”只在 UI 线程快照，拼接和 Clipboard 重试转到有界 STA 队列。任意地址读写/`calli` 已禁止；游戏命令默认拒绝，等待游戏内逐动作验收 |
| ACT.FoxTTS | [Noisyfox/ACT.FoxTTS](https://github.com/Noisyfox/ACT.FoxTTS) | 当前官方 Release 为 7z，解压后选择 `ACT.FoxTTS.dll`；普通 ACT 插件 | 已移至独立 Host 并可加载；音频设备和所选网络语音后端等待游戏内实测 |
| Triggernometry 中文维护版 | [MnFeN/Triggernometry](https://github.com/MnFeN/Triggernometry) | `Triggernometry.dll` + `*.triglations.xml`；普通 ACT 插件 | 已移至独立 Host；离线真实 DLL 已验证配置读取、标准日志、网络等价事件、区域限制、战斗开始/结束、正则、游戏侧 TTS 授权和 Host 内输出调度闭环。管理员状态保持真实，普通权限能力改为说明；网络、外部程序和高风险脚本默认拒绝；实际音频设备输出仍待游戏内手测 |
| Triggernometry 原版 | [paissaheavyindustries/Triggernometry](https://github.com/paissaheavyindustries/Triggernometry) | Release ZIP；普通 ACT 插件 | 仅保留兼容基线，不作为中文用户首选来源 |

`quisquous/cactbot` 是旧上游，不再作为安装器的当前来源。CactbotSelf 也不是
Cactbot 本体，两者在设置页和安装目录中保持独立。

传统 ACT 插件由 `DalamudActCompat.Host.exe` 加载，不再由 FFXIV/Dalamud 进程直接
加载。Host 崩溃、断管和阻塞读线程已有自动检测/终止测试；这提供进程级故障隔离，
但同一个 Host 内的插件仍互相信任，不能视为 OS 沙箱。

“DLL 成功加载”只代表基础 ABI 通过，不代表插件所有功能已经兼容。完整联动依赖
ACT 的日志、战斗生命周期、插件列表、TTS/声音、WinForms 兼容对象，以及
OverlayPlugin 的事件源、WebSocket 和基于 WebView2 的 Cactbot HTML Overlay 窗口。
