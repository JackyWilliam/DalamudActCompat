# 2026-09-27 插件核查、抹茶更新与 FoxTTS 布局修正

## 上游核查

| 组件 | 已核对版本或提交 | 处理 |
| --- | --- | --- |
| 抹茶 | [d4fffc6，2026-09-26](https://github.com/thewakingsands/matcha/commit/d4fffc60399549b9e9b8af9d6ca9bfec7378c7ff) | 从此源码构建 26.9.26.1753 DACT5，补齐 Host 的五项钓鱼操作码 |
| Triggernometry 中文维护版 | [2.2.0.1 / b795485](https://github.com/MnFeN/Triggernometry/commit/b795485b280bc7acf5ece8139fad54870f7eea46) | 维护者 DLL、汉化与许可证重新下载后校验一致 |
| 鲶鱼精邮差 | [1.3.6.6](https://github.com/Natsukage/PostNamazu/releases/tag/1.3.6.6) | 最新正式包校验一致，现有 1.3.6.6/1.3.6.7 接口兼容保留 |
| 仿生石 | [0.0.4.2](https://github.com/MnFeN/Simulant/releases/tag/v0.0.4.2) | 最新 DLL SHA-256 与现有测试锁定值一致，原版兼容测试通过；仍为用户安装扩展 |
| FoxTTS | [3.3.1.189](https://github.com/Noisyfox/ACT.FoxTTS/releases/tag/3.3.1) | 最新 DLL 校验一致，在 DACT Host 中修正窗口布局 |
| 银山雀儿 | 0.6.0.4 | 已有二进制和 Host 回归通过；公开入口仍不能证明最新版，不宣称没有新包 |
| Cactbot | [0.37.5](https://github.com/OverlayPlugin/cactbot/releases/tag/v0.37.5) | 最新正式包与锁文件一致；main 的八个未发布提交仍作为可选后续项 |

IINACT 2.10.3.8、OverlayPlugin 0.19.108、FFXIV ACT Plugin 3.0.3.1 的
官方 latest API 均与当前依赖一致。本次没有更换解析核心。

## 抹茶

上游新增 Fishing 事件涵盖抛竿、咬钩、提竿、收线、捕获、结束、退出和
换区重置，并携带鱼饵、钓场、钩型、状态和时间。现有网络桥已传递原始
epoch，本次保留这条路径。Host 重新构造事件表时补入 EventPlay4、
SystemLogMessage、FishCaught、StatusEffectList 和 ClientTrigger，后者保留
客户端方向位。旧版 DLL 缺少新增枚举时，仍可加载原有事件映射。

DACT5 只替换 DACT4 包中的入口 DLL。数据、manifest、上游伴随 DLL 和
加密运行时常量逐字节保持。兼容补丁继续限制配置文件访问并提供原有
非阻塞通知桥。源码、版本与 DLL/ZIP 哈希在锁文件和
`vendor/BundledActPlugins/matcha/BUILD.md` 中同步记录。

## FoxTTS 窗口

原版一般设置页把引擎面板锚定在固定左栏右侧。控件缩放后，窄窗口中
右侧滑条会被裁切。DACT 默认 960 像素宽的承载窗口及其 WinForms 环境
使该布局限制更容易出现；原版控件的隔离测试能够复现。

Host 初始化 FoxTTS 后，将同一批控件放进可重排的布局：空间足够时保留
双栏，空间不足时先显示引擎和滑条、再向下排列其他设置。窄窗口使用
纵向滚动。控件和绑定保留，随包 FoxTTS DLL、TTS 引擎、声音设置和播报
路径均未修改。其他扩展的窗口不应用此布局。

## 验证范围

- 真实 FoxTTS 控件：中英文，100%/125%/150%/200% 控件缩放，700/944/1400
  像素客户区，反复缩放窗口和更换 CafePro/Cafe/Edge 设置面板。检查滑条
  横向完整可见、宽度可操作、数值不变和无横向滚动，并检查截图。
- 上述缩放是离线控件缩放测试，不等同于各物理显示器 DPI 的现场验收。
- 真实抹茶 DLL：国服和国际服事件表、钓鱼完整生命周期、重复包、错误
  方向、截断包、未知鱼饵与服务端确认以小钓大、原 FishBite 事件；另回归
  FATE、跨服排队、Host 双向网络、通知、权限与卸载。
- 全套 Package/FFXIV、Host（含真实 Triggernometry/PostNamazu/FoxTTS/
  SilverDasher/Matcha）、仿生石原版、LegacyResource 及 U7b 时序测试。
- 按现有发布参数构建和收集完整包，检查程序集/依赖版本、资源身份、
  包哈希和无本机路径/配置/调试符号。未启动游戏或触发真实游戏命令。

本次为本地适配候选，DACT 版本号维持 0.4.5.1。游戏内钓鱼事件、FoxTTS
在用户屏幕上的验收以及正式发布另行执行。
