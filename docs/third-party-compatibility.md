# 第三方 ACT 扩展兼容说明

核对日期：2026-07-27。

| 组件 | 当前维护来源 | 发布物/安装模型 | 当前兼容范围 |
| --- | --- | --- | --- |
| Cactbot | [OverlayPlugin/cactbot](https://github.com/OverlayPlugin/cactbot) | 官方 Release ZIP；OverlayPlugin addon，包含 DLL、UI、资源与时间轴 | 可独立安装完整资源；内置 Cactbot 事件源；HTML Overlay 编辑模式禁用 WebView2 输入，由宿主检测全局鼠标按键和窗口范围，并在操作期间由唯一目标窗口取得 Windows 鼠标捕获，不依赖 Chromium 子窗口转发消息，也不会与游戏的鼠标捕获争用；锁定后恢复网页交互，等待游戏内联动验收 |
| CactbotSelf / MoreLogLine | [tssailzz8/cacbotSelf](https://github.com/tssailzz8/cacbotSelf) | `CactbotSelf.dll` 或作者 ZIP；ACT + OverlayPlugin 扩展 | 可安装和加载；额外日志/摄像机/标点能力需游戏内逐项实测 |
| PostNamazu | [Natsukage/PostNamazu](https://github.com/Natsukage/PostNamazu) | `PostNamazu.dll` 或作者 ZIP；普通 ACT 插件，同时联动 FFXIV_ACT_Plugin、OverlayPlugin、Triggernometry | 可安装；.NET 10 下改用 Dalamud 同进程原生调用桥并保留 HTTP、Triggernometry、OverlayPlugin 入口，等待游戏内写入验收 |
| ACT.FoxTTS | [Noisyfox/ACT.FoxTTS](https://github.com/Noisyfox/ACT.FoxTTS) | 当前官方 Release 为 7z，解压后选择 `ACT.FoxTTS.dll`；普通 ACT 插件 | 可安装和加载；在 OverlayPlugin/Cactbot 启动前将 ACT `PlayTtsMethod` 桥接到 FoxTTS `Speak`，避免启动期提示丢失；音频设备和所选网络语音后端等待游戏内实测 |
| Triggernometry 中文维护版 | [MnFeN/Triggernometry](https://github.com/MnFeN/Triggernometry) | `Triggernometry.dll` + `*.triglations.xml`；普通 ACT 插件 | 支持 DLL、汉化文件及托管依赖安装；日志行、战斗开始/结束、TTS/声音 API 已接入，动作类型需逐项实测 |
| Triggernometry 原版 | [paissaheavyindustries/Triggernometry](https://github.com/paissaheavyindustries/Triggernometry) | Release ZIP；普通 ACT 插件 | 仅保留兼容基线，不作为中文用户首选来源 |

`quisquous/cactbot` 是旧上游，不再作为安装器的当前来源。CactbotSelf 也不是
Cactbot 本体，两者在设置页和安装目录中保持独立。

“DLL 成功加载”只代表基础 ABI 通过，不代表插件所有功能已经兼容。完整联动依赖
ACT 的日志、战斗生命周期、插件列表、TTS/声音、WinForms 兼容对象，以及
OverlayPlugin 的事件源、WebSocket 和基于 WebView2 的 Cactbot HTML Overlay 窗口。
