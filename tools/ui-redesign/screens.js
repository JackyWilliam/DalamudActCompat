// DACT 1.0 control-center redesign, draft 1. Every control below exists in 0.4.5.20; the
// mapping table at the end records where each one moved ("原功能一个都不能少").
"use strict";
const {
  C, F, frame, row, col, spacer, vspacer, text, hint, icon, img, vector, divider,
  button, iconButton, toggle, checkbox, select, menuItem, dropdownMenu, disclosure, stepsPanel, identityName, input, slider, segmented, tabs,
  pill, badge, card, field, toggleRow, grid2,
} = require("./dsl");
const skins = require("./skins");

const WIDTH = 920, HEIGHT = 640;
const NAV = [
  ["home", "home", "主页"], ["meter", "meter", "战斗统计"], ["history", "history", "战斗记录"], ["overlays", "layers", "悬浮窗"],
  ["extensions", "plug", "扩展"], ["friends", "users", "好友"], ["ai", "sparkle", "AI 助手"],
  null,
  ["cloud", "cloud", "账号与云同步"], ["settings", "gear", "设置"],
];

function header(signedIn = true) {
  return row({ name: "顶栏", w: "fill", h: 48, pad: [0, 10, 0, 14], gap: 10 }, [
    img("logo", 26, 26, { radius: 6 }),
    // 1.0 renames the product to DACT (user decision, 2026-11-07); folder and config names
    // such as pluginConfigs\DalamudActCompat stay as they are so existing data keeps loading.
    text("DACT"),
    spacer(),
    // The search field sits centred on the whole window (user request), not between the
    // logo and the right-hand controls, so it stays put whatever the chip says.
    signedIn ? input("搜索设置和功能  Ctrl+K", { placeholder: true, trailing: "search", w: 196, abs: true, x: (WIDTH - 196) / 2, y: 9 }) : null,
    // Signed out there is nothing to sync, so the login screen shows no cloud chip (user edit).
    signedIn ? row({ name: "云同步状态", comp: "HeaderChip", h: 26, pad: [0, 10], gap: 6, radius: 13, bg: C.hover }, [
      frame({ name: "dot", w: 6, h: 6, radius: 3, bg: C.gold }),
      text("等待首次同步", { size: F.small, color: C.gold }),
    ]) : null,
    // No friends button here: the sidebar 好友 item already carries the unread badge.
    // Search (Ctrl+K) finds any setting, page or help section; 反馈 opens the manual form.
    signedIn ? iconButton("feedback") : null,
    signedIn ? iconButton("help") : null,
    iconButton("close"),
  ]);
}

function navItem(iconName, label, active, unread) {
  return row({
    name: `导航/${label}`, comp: `NavItem/${active ? "active" : "idle"}${unread ? "+badge" : ""}`, w: "fill", h: 34, pad: [0, 10], gap: 10,
    radius: 7, bg: active ? C.hover : null,
  }, [
    icon(iconName, { color: active ? C.accent : C.muted }),
    text(label, { w: "fill", color: active ? C.text : C.muted }),
    unread ? badge(unread) : null,
  ]);
}

function sidebar(active) {
  return col({ name: "侧栏", w: 176, h: "fill", pad: [12, 10], gap: 2 }, [
    ...NAV.map(item => item === null
      ? divider({ name: "分隔", margin: true, h: 1 })
      : navItem(item[1], item[2], item[0] === active, item[0] === "friends" ? 4 : 0)),
    vspacer(),
    col({ name: "侧栏底部", w: "fill", gap: 6, pad: [10, 8, 2, 8] }, [
      row({ name: "解析器状态", gap: 6, w: "fill" }, [
        frame({ name: "dot", w: 6, h: 6, radius: 3, bg: C.accent }), text("解析器运行中", { size: F.small, color: C.accent }),
      ]),
      row({ name: "版本", w: "fill", gap: 6 }, [
        text("v1.0.0.0", { size: F.small, color: C.muted }), spacer(), button("运行状态", "ghost"),
      ]),
    ]),
  ]);
}

function pageHeader(title, description, actions = []) {
  return row({ name: "页头", w: "fill", gap: 12 }, [
    col({ name: "标题", w: "fill", gap: 2 }, [text(title, { size: F.title }), hint(description)]),
    ...actions,
  ]);
}

// Full-length variants let the window grow with its content instead of scrolling.
// Overlays (open popups) float over the window at fixed x/y.
function shell(active, content, full = false, overlays = []) {
  return col({
    name: "DACT 控制中心", w: WIDTH, h: full ? "hug" : HEIGHT, bg: C.surface, radius: 10, clip: true,
    border: { c: C.border, w: 1 },
  }, [
    header(),
    divider(),
    row({ name: "主体", w: "fill", h: full ? "hug" : "fill", align: "stretch" }, [
      sidebar(active),
      frame({ name: "侧栏分隔", w: 1, h: "fill", bg: C.divider }),
      col({ name: "内容", w: "fill", h: full ? "hug" : "fill", pad: [18, 22], gap: 14, clip: !full }, content),
    ]),
    ...overlays,
  ]);
}

// opts.color / opts.bold: values with their own meaning colour, e.g. FFLogs parse colours.
function statTile(iconName, label, value, tone, sub, opts = {}) {
  const color = opts.color || { ok: C.accent, warn: C.gold, plain: C.text }[tone];
  return col({ name: `状态卡/${label}`, w: "fill", pad: 12, gap: 6, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
    row({ name: "标题", w: "fill", gap: 6 }, [icon(iconName, { color: C.muted, size: 14 }), text(label, { size: F.small, color: C.muted })]),
    text(value, { size: F.h2, color, bold: opts.bold }),
    hint(sub),
  ]);
}

// ---------------------------------------------------------------- 主页
// regionOpen draws the 游戏区域 dropdown expanded; the popup's x/y/w line it up under that
// select in the 920×640 layout (it floats like ImGui's Combo popup).
const homeContent = (regionOpen = false) => [
  pageHeader("主页", "常用状态和入口集中在这里。"),
  row({ name: "状态卡", w: "fill", gap: 12, align: "stretch" }, [
    statTile("pulse", "解析器", "运行中", "ok", "两个系统插件已加载"),
    statTile("plug", "扩展", "4 / 5 已启用", "plain", "独立 Host 运行中"),
    statTile("cloud", "云同步", "等待首次同步", "warn", "自动同步已开启"),
    statTile("users", "好友", "2 人在线", "plain", "1 条未读消息"),
  ]),
  card("快捷入口", [
    row({ name: "按钮", w: "fill", gap: 8 }, [
      button("关闭战斗统计", "primary", { icon: "meter" }), button("战斗记录", "secondary", { icon: "history" }),
      button("运行状态", "secondary", { icon: "pulse" }), button("打开 FFLogs 上传日志", "secondary", { icon: "folder" }),
    ]),
  ]),
  card("基础设置", [
    grid2([
      toggleRow("启用解析", true),
      field("游戏区域", select("自动检测（国服）", { open: regionOpen }), "已自动检测：国服（游戏客户端）· 语言：简体中文"),
      toggleRow("精简模式", false),
    ], [
      toggleRow("自动启动解析器", true),
      field("游戏解析范围", select("所有人"), "只解析选中范围内的玩家事件。"),
      toggleRow("游戏失焦时隐藏网页悬浮窗", false),
    ], { gap: 16 }),
  ]),
];
const homeScreen = (regionOpen = false) => shell("home", homeContent(regionOpen), false, regionOpen ? [
  dropdownMenu(["自动检测（国服）", "国服（手动）", "国际服（手动）"], { selected: 0, hover: 1, w: 326, abs: true, x: 215, y: 475 }),
] : []);
const home = homeScreen();

// ---------------------------------------------------------------- 战斗统计
// guideOpen expands 如何创建 FFLogs API Client: the right column grows past 640, so that
// variant is drawn full length (in game the content area scrolls).
const FFLOGS_STEPS = [
  "登录 FFLogs 网站。", "点击 Create Client。", "输入一个名称。",
  "输入一个网址；如果不知道填什么，请填写 https://example.com。", "点击创建。",
  "在下方找到 Client ID 和 Client Secret，并分别填入插件对应位置。",
];
const meterScreen = (guideOpen = false) => shell("meter", [
  pageHeader("战斗统计", "仅调整内置战斗统计的显示，不影响 Cactbot 或 HTML 悬浮窗。", [
    row({ name: "显示开关", gap: 8 }, [text("显示战斗统计", { color: C.muted }), toggle(true)]),
    button("定位到战斗统计", "secondary", { icon: "target" }),
  ]),
  grid2([
    card("榜单", [
      field("榜单模板", row({ name: "模板行", w: "fill", gap: 8 }, [
        segmented(["经典榜", "横版", "职能分栏"], 0, { w: "fill" }), button("自定义…", "secondary"),
      ]), "一次只显示一个榜单；切换时保留各模板的位置、大小、锁定和槽位配置。"),
      field("显示范围", select("按榜单设置"), "沿用各榜单的 8/24 人、队伍选择和仅自己等显示设置。"),
      field("统计重置方式", select("DACT 默认"), "保持原来的统计逻辑。"),
      field("刷新间隔", slider(0.32, "750 毫秒")),
      field("玩家 ID 显示", select("显示原始 ID"), "遮盖只影响界面显示，不修改解析数据和战斗日志。"),
    ], { actions: [button("重置当前战斗…", "danger", { icon: "reset" })] }),
  ], [
    card("FFLogs Parse 预估", [
      hint("用本场实际 DPS 对照 FFLogs 同职业、同副本、同分区的分布估算 Parse；缓存缺失时显示 “--”。"),
      row({ name: "元数据", w: "fill", gap: 6 }, [pill("区域 CN · 分区 9 · 指标 DPS", "off", { dot: false })]),
      toggleRow("启用 FFLogs 在线估算", false),
      // Same order as 0.4.5.20: the guide sits right above the fields it explains.
      disclosure("如何创建 FFLogs API Client", guideOpen),
      guideOpen ? stepsPanel(FFLOGS_STEPS) : null,
      field("Client ID", input("粘贴 Client ID", { placeholder: true })),
      field("Client Secret", input("粘贴 Client Secret", { placeholder: true, trailing: "eye" })),
      row({ name: "凭据按钮", w: "fill", gap: 8 }, [
        button("测试并刷新", "primary"), button("创建 / 管理 API Client", "secondary", { icon: "external" }),
      ]),
      hint("密钥只保存在本机插件配置中，可随时撤销；不会上传玩家 ID。"),
      divider(),
      row({ name: "副本识别", w: "fill", gap: 8, align: "start" }, [
        icon("target", { size: 14 }),
        col({ name: "说明", w: "fill", gap: 2 }, [
          text("当前副本自动识别", { size: F.small, color: C.muted }),
          hint("当前不在最新团队副本榜单中，不会加载历史榜单。榜单区域跟随主页的游戏区域。"),
        ]),
      ]),
    ], { titleExtra: [pill("未启用", "off")] }),
  ]),
], guideOpen);
const meter = meterScreen();

// ---------------------------------------------------------------- 悬浮窗
function overlayItem(name, status, selected) {
  return col({
    name: `悬浮窗/${name}`, w: "fill", pad: [9, 10], gap: 2, radius: 8, bg: selected ? C.hover : null,
    border: selected ? { c: "rgba(107,199,245,0.55)", w: 1 } : null,
  }, [text(name, { color: selected ? C.text : C.muted }), text(status, { size: F.small, color: C.muted })]);
}
const overlays = shell("overlays", [
  pageHeader("悬浮窗", "选择一个悬浮窗，调整它的显示与操作。", [button("添加悬浮窗", "primary", { icon: "plus" })]),
  row({ name: "Cactbot", w: "fill", pad: [12, 14], gap: 12, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
    col({ name: "说明", w: "fill", gap: 4 }, [
      row({ name: "标题", gap: 8 }, [text("Cactbot", { color: C.gold }), pill("资源已安装", "ok"), text("解析器：运行中", { size: F.small, color: C.muted })]),
      hint("提示中的玩家默认显示职业全称；可在 Cactbot 设置的“默认玩家代称”中修改。"),
    ]),
    button("Cactbot 设置", "secondary"), button("安装 / 更新", "secondary"), button("官方项目页", "ghost", { icon: "external" }),
  ]),
  row({ name: "主从", w: "fill", h: "fill", gap: 14, align: "stretch" }, [
    col({ name: "列表", w: 210, pad: 10, gap: 4, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
      text("我的悬浮窗", { size: F.small, color: C.muted }),
      overlayItem("文字提醒", "Cactbot · 已关闭", true),
      overlayItem("时间轴", "Cactbot · 已关闭", false),
      vspacer(),
      divider(),
      text("全部悬浮窗", { size: F.small, color: C.muted }),
      row({ name: "失焦", w: "fill", gap: 8 }, [text("游戏失焦时隐藏", { w: "fill" }), toggle(true)]),
    ]),
    col({ name: "详情", w: "fill", pad: 14, gap: 12, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
      row({ name: "标题", w: "fill", gap: 8 }, [
        text("文字提醒", { size: F.h2 }), pill("已关闭", "off"), text("来自 Cactbot", { size: F.small, color: C.muted }), spacer(),
        button("打开", "primary"), button("隐藏", "secondary", { disabled: true }), iconButton("more", { bg: C.hover }),
      ]),
      tabs(["常规", "快捷键", "来源"], 0),
      grid2([
        col({ name: "显示与启动", w: "fill", gap: 10 }, [
          text("显示与启动", { size: F.small, color: C.muted }), toggleRow("启动时自动打开", true), toggleRow("脱战隐藏", false),
        ]),
      ], [
        col({ name: "位置", w: "fill", gap: 10 }, [
          text("位置、缩放、穿透与锁定", { size: F.small, color: C.muted }),
          toggleRow("鼠标穿透", true), toggleRow("锁定位置和大小", true),
        ]),
      ], { gap: 20 }),
      field("页面缩放", slider(0.5, "1.00")),
      row({ name: "编辑位置", w: "fill", gap: 10 }, [
        button("编辑位置和大小", "secondary", { icon: "target" }), hint("编辑时会暂时关闭穿透与锁定，完成后恢复。"),
      ]),
    ]),
  ]),
]);

// ---------------------------------------------------------------- 扩展
// state (optional): { label, tone, note, own } for the run states the old full settings listed
// (初始化中 / 缺少依赖 / 已停止 / 版本不兼容 / 故障) and user-installed extensions (own).
function extensionRow(name, version, description, enabled, permissions, state) {
  const tone = state ? state.tone : enabled ? "ok" : "off";
  const noteColor = state && state.own ? null : { warn: C.gold, error: C.danger }[tone];
  return row({ name: `扩展/${name}`, w: "fill", pad: [8, 4], gap: 12 }, [
    toggle(enabled),
    col({ name: "名称", w: "fill", gap: 2 }, [
      row({ name: "标题", gap: 8 }, [text(name, { color: enabled ? C.text : C.muted }), text(version, { size: F.small, color: C.muted }),
        state && state.own ? pill("自行安装", "off", { dot: false }) : null]),
      state && state.note ? hint(state.note, noteColor ? { color: noteColor } : {}) : hint(description),
    ]),
    pill(state ? state.label : enabled ? "已启用" : "已禁用", tone),
    permissions ? button("权限", "secondary", { icon: "shield" }) : null,
    button("打开配置", "secondary", { disabled: !enabled }),
  ]);
}
// states draws the less common run states and a user-installed extension; that list is taller
// than 640, so the variant is full length (the content area scrolls in game).
const extensionsScreen = (states = false) => shell("extensions", [
  pageHeader("扩展", "系统插件与第三方 ACT 扩展保持原有运行方式。", [
    button("安装 DLL / ZIP", "primary", { icon: "upload" }), button("打开文件夹", "secondary", { icon: "folder" }),
    button("检查更新与来源", "secondary", { icon: "refresh" }),
  ]),
  card("系统插件", [
    row({ name: "插件", w: "fill", gap: 24 }, [
      row({ name: "FFXIV_ACT_Plugin", gap: 10 }, [toggle(true), text("FFXIV_ACT_Plugin")]),
      row({ name: "OverlayPlugin", gap: 10 }, [toggle(true), text("OverlayPlugin")]),
    ]),
    hint("改变系统插件状态后需要重启解析器；兼容扩展启停后会自动重启独立 Host。"),
  ]),
  card("兼容扩展", [
    extensionRow("ACT.FoxTTS", "v3.3.1.189", "TTS 语音合成与播报", true, false, states && { label: "初始化中", tone: "warn", note: "正在加载音频后端…" }),
    divider(),
    extensionRow("鲶鱼精邮差 / PostNamazu", "v1.3.6.6", "游戏命令、标点与本地桥接", true, true, states && { label: "缺少依赖", tone: "error", note: "缺少依赖：需要游戏内鲶鱼精邮差模块与签名扫描" }),
    divider(),
    extensionRow("Triggernometry", "v2.4.3.0", "触发器、时间轴、TTS 与绘图", true, true),
    divider(),
    extensionRow("银山雀儿 / SilverDasher", "v0.6.0.4", "狩猎、临危受命与跨区状态提醒", false, true, states && { label: "已停止", tone: "off" }),
    divider(),
    extensionRow("抹茶 / Cafe.Matcha", "v26.9.26.1753", "狩猎与临危受命提醒；默认自启动并独占专属 Host", true, true, states && { label: "版本不兼容", tone: "error", note: "版本不兼容：需要 v26.9 及以上" }),
    states ? divider() : null,
    states ? extensionRow("仿生石 / Simulant", "v0.3.2.0", "", true, true, { label: "故障", tone: "error", note: "自行安装的 ACT 扩展；装好后才显示，可单独设置权限", own: true }) : null,
  ], {
    gap: 4, actions: [
      text("启动时自动检查更新", { size: F.small, color: C.muted }), icon("help", { size: 13 }), toggle(true), button("三方扩展声明", "ghost"),
    ],
  }),
], states);
const extensions = extensionsScreen();

// ---------------------------------------------------------------- 好友
// identity: { sponsor: "Lv.N", admin } for the server-authorized markers; official notices use gold.
function friendItem(name, line1, line2, status, unread, selected, identity, official) {
  return row({
    name: `好友/${name}`, w: "fill", pad: [9, 10], gap: 10, radius: 8, align: "start", bg: selected ? C.hover : null,
  }, [
    frame({ name: "状态", w: 8, h: 8, radius: 4, bg: status, margin: true }),
    col({ name: "信息", w: "fill", gap: 2 }, [
      row({ name: "名字", gap: 6 }, [official ? text(name, { color: C.gold }) : identityName(name, identity || {})]),
      text(line1, { size: F.small, color: C.muted, w: "fill" }),
      line2 ? text(line2, { size: F.small, color: C.accent, w: "fill" }) : null,
    ]),
    unread ? badge(unread) : null,
  ]);
}
// Incoming bubbles show the sender with markers; a quick question carries its reply options.
const replyChip = label => row({ name: "回复/" + label, h: 30, pad: [0, 14], radius: 6, bg: C.raised, border: { c: "rgba(107,199,245,0.45)", w: 1 }, justify: "center" },
  [text(label, { color: C.accent })]);
function bubble(message, time, mine, sender, replies) {
  return row({ name: mine ? "我的消息" : "对方消息", w: "fill", justify: mine ? "end" : "start" }, [
    col({ name: "气泡", w: 300, pad: [8, 12], gap: 4, radius: 10, bg: mine ? C.primary : C.hover }, [
      sender ? identityName(sender.name, { ...sender, size: F.small }) : null,
      text(message, { w: "fill" }), text(time, { size: F.tiny, color: C.muted }),
      replies ? row({ name: "快捷回复选项", gap: 6 }, replies.map(replyChip)) : null,
    ]),
  ]);
}
const DRAGON = { name: "雪原上的龙骑", sponsor: "Lv.10" };
const friends = shell("friends", [
  pageHeader("好友", "2 人在线 · 消息与申请会同步到你的 DACT 账号。", [button("来信提醒设置", "secondary", { icon: "gear" })]),
  row({ name: "主从", w: "fill", h: "fill", gap: 14, align: "stretch" }, [
    col({ name: "好友列表", w: 270, pad: 12, gap: 10, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
      row({ name: "我", w: "fill", gap: 10 }, [
        row({ name: "头像", w: 34, h: 34, radius: 17, bg: C.hover, justify: "center" }, [text("R", { color: C.gold })]),
        col({ name: "我的状态", w: "fill", gap: 2 }, [
          row({ name: "名字", gap: 6 }, [identityName("Raynor", { sponsor: "Lv.1" })]),
          row({ name: "状态", gap: 6 }, [frame({ name: "dot", w: 6, h: 6, radius: 3, bg: C.danger }), text("忙碌 · 今晚刷坐骑", { size: F.small, color: C.muted })]),
        ]),
        iconButton("edit"),
      ]),
      segmented(["好友", "申请", "添加"], 0, { w: "fill" }),
      col({ name: "列表", w: "fill", gap: 2 }, [
        friendItem("DACT 官方通知", "欢迎使用好友功能，祝你冒险愉快！", null, C.gold, 1, false, null, true),
        friendItem("雪原上的龙骑", "忙碌 · 今晚刷坐骑", "正在进行：阿卡狄亚零式登天斗技场 重量级3", C.danger, 3, true, { sponsor: "Lv.10" }),
        friendItem("远方的旅行者", "我：好，等你回来再一起打。", null, C.gold, 0, false, { admin: true }),
        friendItem("星光下的猫", "离线", null, C.muted, 0, false),
      ]),
      vspacer(),
      hint("请勿利用本功能从事洗钱、刷单、诈骗等违规违法活动；涉嫌违规的消息将由管理员核查处理。"),
    ]),
    col({ name: "聊天", w: "fill", pad: 14, gap: 12, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
      row({ name: "聊天标题", w: "fill", gap: 8 }, [
        col({ name: "对象", w: "fill", gap: 2 }, [
          row({ name: "名字", gap: 6 }, [identityName("雪原上的龙骑", { sponsor: "Lv.10", size: F.h2 })]),
          hint("忙碌 · 正在进行：阿卡狄亚零式登天斗技场 重量级3"),
        ]),
        iconButton("edit", { bg: C.hover }), iconButton("external", { bg: C.hover }), iconButton("more", { bg: C.hover }),
      ]),
      divider(),
      col({ name: "消息", w: "fill", h: "fill", gap: 10 }, [
        bubble("今晚一起刷坐骑吗？我已经准备好了。", "11-06 16:26", false, DRAGON),
        bubble("好啊，等我打完这把。", "11-06 16:27", true),
        bubble("你什么时候结束？（快捷消息）", "11-06 16:31", false, DRAGON, ["快结束了", "还要一会儿"]),
      ]),
      // The plugin sends two quick messages; replies to them appear on the incoming message.
      row({ name: "快捷消息", w: "fill", gap: 8 }, [hint("快捷消息", { w: "hug" }), button("下把邀我", "secondary"), button("你什么时候结束", "secondary")]),
      row({ name: "输入", w: "fill", gap: 8 }, [input("输入消息，按回车发送", { placeholder: true }), button("发送", "primary", { icon: "send" })]),
    ]),
  ]),
]);

// ---------------------------------------------------------------- 账号与云同步
function stat(label, value, color) {
  return col({ name: `统计/${label}`, w: "fill", gap: 2 }, [text(label, { size: F.small, color: C.muted }), text(value, { color })]);
}
const cloud = shell("cloud", [
  pageHeader("账号与云同步", "自动保护常用配置，管理最近两份备份，并在需要时安全恢复。"),
  grid2([
    card("账号", [
      row({ name: "账号", w: "fill", gap: 12 }, [
        img("logo", 40, 40, { radius: 8 }),
        col({ name: "信息", w: "fill", gap: 2 }, [text("WarriorOfLight", { size: F.h2 }), hint("登录有效期至 2026-12-06 16:25")]),
      ]),
      row({ name: "按钮", w: "fill", gap: 8 }, [button("修改密码", "secondary"), button("退出登录", "secondary")]),
    ]),
    card("同步", [
      row({ name: "统计", w: "fill", gap: 12 }, [stat("自动同步", "已开启", C.accent), stat("云端版本", "0 / 2", C.text), stat("最近备份", "暂无", C.muted)]),
      toggleRow("打开游戏后自动同步配置", true, "每次打开游戏并登录后检查一次；配置没变化就不上传，退出游戏不会被阻塞。"),
      row({ name: "按钮", w: "fill", gap: 8 }, [button("上传当前配置", "primary", { icon: "upload" }), button("刷新状态", "secondary", { icon: "refresh" })]),
    ], { titleExtra: [pill("等待首次同步", "warn")] }),
  ], [
    card("备份与恢复", [
      hint("配置在本机加密后上传，服务器无法读取；最多保留最近 2 个不同内容的版本，恢复前始终先预览。"),
      field("云端版本", select("暂无备份", { placeholder: true, disabled: true })),
      row({ name: "按钮", w: "fill" }, [button("预览恢复内容", "secondary", { disabled: true })]),
    ]),
    card("Discord 账号找回", [
      hint("恢复保护已开启：改密后仍可读取原备份。忘记密码时，私聊 DACT 机器人输入 /dact 找回。"),
      row({ name: "按钮", w: "fill", gap: 8 }, [
        button("进入 DACT Discord 服务器", "secondary", { icon: "external" }),
      ]),
    ], { titleExtra: [pill("已绑定 · 1/3", "ok")] }),
  ]),
  card("邀请好友", [hint("默认 3 个邀请名额。"), row({ name: "按钮", w: "fill" }, [button("生成邀请码", "secondary")])]),
]);

// ---------------------------------------------------------------- 设置
// Two columns of equal height: the left column's last card stretches to the right one.
const settingsColumns = (left, right) => row({ name: "两栏", w: "fill", gap: 14, align: "stretch" }, [
  col({ name: "左栏", w: "fill", gap: 14 }, left), col({ name: "右栏", w: "fill", gap: 14 }, right),
]);
const settings = shell("settings", [
  pageHeader("设置", "外观、语言、提醒、日志与恢复选项。"),
  settingsColumns([
    card("外观与皮肤", [
      row({ name: "皮肤", w: "fill", gap: 14 }, [
        col({ name: "缩略图", w: 118, h: 74, pad: 6, gap: 4, radius: 6, bg: C.surface, border: { c: C.cardBorder, w: 1 } }, [
          frame({ name: "a", w: "fill", h: 8, radius: 2, bg: C.hover }),
          row({ name: "b", w: "fill", h: "fill", gap: 4, align: "stretch" }, [
            frame({ name: "nav", w: 26, h: "fill", radius: 2, bg: C.raised }),
            col({ name: "c", w: "fill", gap: 3 }, [frame({ name: "l1", w: "fill", h: 4, radius: 2, bg: C.accent }), frame({ name: "l2", w: 40, h: 4, radius: 2, bg: C.hover }), frame({ name: "l3", w: 30, h: 4, radius: 2, bg: C.hover })]),
          ]),
        ]),
        col({ name: "说明", w: "fill", gap: 6 }, [
          text("当前：深海原色"), hint("预览皮肤、查看权益与探索配色。"), button("管理皮肤", "secondary", { icon: "palette" }),
        ]),
      ]),
    ]),
    card("通用", [
      field("界面语言", select("简体中文")),
      toggleRow("显示 ACT 快捷按钮", true, "左键打开控制中心、右键显示或隐藏战斗统计、按住中键拖动。"),
      field("快捷按钮尺寸", slider(0.4, "80 像素")),
      row({ name: "重置", w: "fill" }, [button("重置快捷按钮大小与位置", "secondary", { icon: "reset" })]),
      toggleRow("调试模式", false),
    ]),
  ], [
    // 来信提醒 moved to its own page under 好友 (user decision), like AI 设置 under AI 助手.
    card("数据与日志", [
      field("历史记录上限", slider(0.2, "20 场"), "战斗历史、战斗 JSON 与 Network 日志分别保留此数量。"),
      field("FFLogs 上传日志目录", input("…\\pluginConfigs\\DalamudActCompat\\logs")),
      row({ name: "目录按钮", w: "fill", gap: 8 }, [button("更改目录…", "secondary"), button("恢复默认", "secondary")]),
      row({ name: "诊断", w: "fill", gap: 8 }, [button("复制诊断日志", "secondary"), button("打开日志文件夹", "secondary", { icon: "folder" })]),
      disclosure("文件位置（配置、调试日志、战斗日志）"),
    ]),
    card("支持 DACT", [hint("用于服务器运行与后续开发。"), row({ name: "按钮", w: "fill" }, [button("爱发电支持", "secondary", { icon: "heart" })])], { h: "fill" }),
  ]),
  card("解析器与诊断", [
    row({ name: "解析器", w: "fill", gap: 10 }, [
      pill("解析器运行中", "ok"), hint("FFXIV_ACT_Plugin and OverlayPlugin are running in DACT."),
    ]),
    row({ name: "按钮", w: "fill", gap: 8 }, [
      button("重启解析器", "secondary", { icon: "reset" }), button("详细运行状态", "secondary", { icon: "pulse" }),
      button("三方扩展声明", "secondary"),
    ]),
    // 我的反馈 shows replies and status; 「让 AI 诊断」 joins this row only while AI 助手 is on.
    row({ name: "求助", w: "fill", gap: 8 }, [
      button("我的反馈", "secondary", { icon: "feedback" }), hint("开发者的回复也会出现在 好友 ·「官方通知」。"),
    ]),
  ]),
  // 场景方案 lives here (the home-page status pill was dropped as noise, user decision).
  card("场景方案", [
    row({ name: "开关", w: "fill", gap: 10 }, [
      col({ name: "说明", w: "fill", gap: 2 }, [text("按内容类型自动切换"), hint("进入高难、日随、野外或 PvP 时，自动换成对应的战斗统计和悬浮窗组合。")]),
      toggle(true),
    ]),
  ], { actions: [button("管理场景", "ghost", { icon: "gear" })] }),
  row({ name: "恢复出厂", w: "fill", pad: 14, gap: 12, radius: 10, bg: C.dangerSoft, border: { c: "rgba(224,108,108,0.35)", w: 1 } }, [
    col({ name: "说明", w: "fill", gap: 2 }, [
      text("恢复出厂设置", { color: C.danger }),
      hint("停止 ACT 宿主、备份所有可变数据，并恢复两个系统插件和默认设置。"),
      hint("最近一次出厂备份：2026-10-30 21:14 · …\\DalamudActCompat\\backups\\factory-20261030-2114"),
    ]),
    button("恢复出厂设置…", "danger"),
  ]),
], true);

// ---------------------------------------------------------------- 登录（保持横版与原风格）
// Signed-out frame shared by 登录 / 创建账号 / 找回密码 / 绑定 Discord: the brand panel as the user
// redrew it in Figma (greeting, name, logo, slogan) and a form panel on the right.
function authShell(name, form) {
  return col({ name, w: WIDTH, h: HEIGHT, bg: C.surface, radius: 10, clip: true, border: { c: C.border, w: 1 } }, [
    header(false),
    divider(),
    row({ name: "主体", w: "fill", h: "fill", pad: 18, gap: 18, align: "stretch" }, [
      col({ name: "品牌区", w: 300, pad: [28, 24], gap: 10, radius: 10, bg: C.raised, border: { c: C.border, w: 1 }, align: "center", justify: "center" }, [
        text("欢迎使用", { size: F.title }),
        text("DACT", { size: F.title, color: C.accent }),  // ice blue, as edited in Figma
        img("logo", 184, 184, { radius: 8 }),
        text("你的战斗工具，尽在一处", { color: C.accent }),  // ice blue (user edit in Figma, 2026-11-07)
      ]),
      col({ name: "表单", w: "fill", pad: [26, 64], gap: 14, justify: "center", radius: 10, bg: C.raised, border: { c: "rgba(107,199,245,0.45)", w: 1 } }, form),
    ]),
  ]);
}
const authTitle = (title, sub) => col({ name: "欢迎", w: "fill", gap: 4 }, [text(title, { size: F.h2, color: C.accent }), hint(sub)]);
const login = authShell("DACT 控制中心（未登录）", [
  segmented(["登录账号", "创建账号"], 0, { w: "fill" }),
  authTitle("欢迎回来", "登录后继续使用你的解析器、悬浮窗和配置。"),
  field("用户名", input("请输入账号名", { placeholder: true })),
  field("密码", input("请输入密码", { placeholder: true, trailing: "eye" })),
  row({ name: "选项", w: "fill", gap: 8 }, [checkbox(true), text("记住登录状态（下次自动登录）", { size: F.small }), spacer(), button("忘记密码？", "ghost")]),
  button("登录 DACT", "primary", { w: "fill" }),
  hint("登录时会把这台电脑的名称发给 DACT 服务器，只用于在你的账号里区分设备；每个账号最多同时登录 5 台。"),
  row({ name: "旧账号恢复", w: "fill", gap: 6 }, [icon("chevronRight", { size: 14 }), text("旧账号恢复选项", { size: F.small, color: C.muted })]),
]);
// 创建账号: the four fields keep the original two-column pairing; activation keys are free and the
// QQ / Discord channels stay next to the form because new users cannot reach the help yet.
const register = authShell("DACT 控制中心（创建账号）", [
  segmented(["登录账号", "创建账号"], 1, { w: "fill" }),
  authTitle("创建 DACT 账号", "使用免费激活码注册，随后绑定 Discord。"),
  grid2([
    field("用户名", input("请输入账号名", { placeholder: true })),
    field("密码", input("至少 10 位", { placeholder: true, trailing: "eye" })),
  ], [
    field("免费激活码", input("输入一次性激活码", { placeholder: true })),
    field("确认密码", input("再次输入密码", { placeholder: true, trailing: "eye" })),
  ], { gap: 12 }),
  hint("两次密码须相同，且至少 10 位。"),
  row({ name: "选项", w: "fill", gap: 8 }, [checkbox(true), text("记住登录状态（下次自动登录）", { size: F.small })]),
  button("创建账号", "primary", { w: "fill" }),
  col({ name: "激活码说明", w: "fill", pad: [10, 12], gap: 6, radius: 8, bg: C.goldSoft }, [
    text("DACT 和激活码均免费，请勿购买或转售。", { size: F.small, color: C.gold, w: "fill" }),
    hint("加入 QQ 群 1098561701 或 Discord，联系群主或频道主获取激活码。"),
    row({ name: "渠道", gap: 8 }, [button("复制 QQ 群号", "secondary"), button("打开 Discord", "secondary", { icon: "external" })]),
  ]),
]);
// 找回密码 step 1 (code from the Discord bot); step 2 is the same password pair as 创建账号.
const resetStep = (n, label, active) => row({ name: `步骤/${label}`, gap: 6 }, [
  row({ name: "序号", w: 20, h: 20, radius: 10, bg: active ? C.accent : C.hover, justify: "center" }, [text(n, { size: F.tiny, color: active ? C.surface : C.muted })]),
  text(label, { color: active ? C.text : C.muted }),
]);
const resetPassword = authShell("DACT 控制中心（找回密码）", [
  row({ name: "返回", w: "fill" }, [button("‹ 返回登录", "ghost")]),
  authTitle("找回账号密码", "通过 Discord 机器人找回，保留原有云备份。"),
  row({ name: "步骤", w: "fill", gap: 10 }, [resetStep("1", "获取重置码", true), frame({ name: "连线", w: 40, h: 1, bg: C.divider }), resetStep("2", "设置新密码", false)]),
  row({ name: "Discord 入口", w: "fill", pad: [10, 12], gap: 10, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
    col({ name: "说明", w: "fill", gap: 2 }, [text("Discord 机器人 / 重置码"), hint("在 /dact 菜单点「找回密码」，选择账号后复制重置码。")]),
    button("打开 Discord", "secondary", { icon: "external" }),
  ]),
  field("用户名", input("请输入账号名", { placeholder: true })),
  field("一次性重置码", input("粘贴机器人提供的 DACT-RESET 码", { placeholder: true }), "重置码会在确认修改时验证。"),
  disclosure("其他找回方式（旧账号恢复密钥、管理员重置码）"),
  button("继续设置密码", "primary", { w: "fill" }),
]);
// 绑定 Discord 后继续: shown after sign-in until the account is linked; the steps carry their own
// buttons, and the status line says what the client is waiting for.
const bindStep = (n, title, children) => row({ name: `第 ${n} 步`, w: "fill", gap: 10, align: "start" }, [
  row({ name: "序号", w: 22, h: 22, radius: 11, bg: C.accentSoft, justify: "center" }, [text(n, { size: F.small, color: C.accent })]),
  col({ name: "内容", w: "fill", gap: 6 }, [text(title, { w: "fill" }), ...children]),
]);
const discordGate = authShell("DACT 控制中心（绑定 Discord）", [
  authTitle("绑定 Discord 后继续", "一个 Discord 账号可绑定 3 个 DACT 账号。完成后自动进入。"),
  row({ name: "当前账号", w: "fill", gap: 6 }, [text("当前账号", { size: F.small, color: C.muted }), identityName("WarriorOfLight"), spacer(), button("退出登录 / 更换账号", "ghost")]),
  col({ name: "步骤", w: "fill", pad: [12, 14], gap: 14, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
    bindStep("1", "复制绑定码，进入我们的 Discord 服务器。", [
      row({ name: "按钮", gap: 8 }, [button("复制绑定码并前往 Discord", "primary", { icon: "external" }), button("只复制绑定码", "secondary")]),
      hint("有效期至 16:17，请勿转发。"),
    ]),
    bindStep("2", "在右侧成员列表的「Bot」分组找到 DACT，右键选择「发消息」。", []),
    bindStep("3", "在私聊输入 /dact 并选择 DACT 命令，点「绑定账号」，粘贴后提交。", [hint("机器人确认后回到游戏即可，无需再点确认。")]),
  ]),
  row({ name: "状态", w: "fill", pad: [8, 12], gap: 8, radius: 8, bg: C.accentSoft }, [
    icon("refresh", { size: 14, color: C.accent }), text("正在确认绑定并建立云备份恢复保护，请稍候。", { size: F.small, color: C.accent, w: "fill" }),
    button("重新检查", "ghost"),
  ]),
]);

// ---------------------------------------------------------------- 弹窗：扩展权限
const CAPS = [
  ["读取战斗日志", false, true], ["读取本地配置", false, true], ["文本转语音", false, true], ["访问剪贴板", false, false],
  ["网络请求", false, true], ["写入文件", false, true], ["发送游戏指令", false, true], ["启动外部程序", true, false],
  ["调用 Windows 原生接口", true, false], ["访问游戏原生内存", true, false], ["运行高风险脚本", true, false],
];
const permissions = col({ name: "扩展权限弹窗", w: 420, pad: 16, gap: 12, radius: 12, bg: C.raised, border: { c: C.border, w: 1 } }, [
  row({ name: "标题", w: "fill", gap: 8 }, [icon("shield", { color: C.gold }), text("Triggernometry 的权限", { size: F.h2 }), spacer(), iconButton("close")]),
  hint("高风险能力默认关闭；授权按扩展和能力分别保存，保存后会自动重启 Host 一次。第三方 DLL 的直接系统调用仍由独立 Host 的进程边界承担。"),
  hint("示意：实际列出的能力和默认值以各扩展的声明为准。", { color: C.gold }),
  col({ name: "能力", w: "fill", gap: 2 }, CAPS.map(([label, risky, on]) => row({ name: label, w: "fill", h: 32, gap: 8 }, [
    text(label, { w: "hug" }), risky ? pill("高风险", "error", { dot: false }) : null, spacer(), toggle(on),
  ]))),
  row({ name: "按钮", w: "fill", gap: 8, justify: "end" }, [button("取消", "secondary"), button("保存并重启 Host", "primary")]),
]);

// ---------------------------------------------------------------- 设计规范
function swatch(name, slot, color) {
  return col({ name: `色板/${name}`, w: 120, gap: 6 }, [
    frame({ name: "色块", w: "fill", h: 48, radius: 8, bg: color, border: { c: "rgba(255,255,255,0.12)", w: 1 } }),
    text(name, { size: F.small }), text(`${slot} · ${color}`, { size: F.tiny, color: C.muted }),
  ]);
}
const designSystem = col({ name: "设计规范", w: 1180, pad: 28, gap: 24, bg: C.surface, radius: 12 }, [
  col({ name: "标题", gap: 4 }, [
    text("DACT 1.0 设计规范（深海原色）", { size: F.title }),
    hint("颜色只用 DactTheme.SkinPalette 的 8 个槽位（及其透明度/混合），其他皮肤自动套用；字体 Noto Sans SC Medium（与卫月游戏内 Noto Sans CJK SC 同族），Figma 13 px ≈ 游戏内 ImGui 17 px 默认字号。", { w: "hug" }),
  ]),
  text("调色板", { color: C.gold }),
  row({ name: "调色板", gap: 16, align: "start" }, [
    swatch("底色", "Surface", "#090C11"), swatch("卡片", "Raised", "#12181F"), swatch("控件/悬停", "Hover", "#1B252F"),
    swatch("金色", "Gold", "#C7A85C"), swatch("冰蓝", "Accent", "#6BC7F5"), swatch("正文", "Text", "#FFFFFF"),
    swatch("次要文字", "Muted", "#A6B3C2"), swatch("边框", "Border", "#35406B"),
  ]),
  hint("这些颜色是「DACT 皮肤配色」变量：深海原色、艾欧泽亚、森之青玉、月下紫晶、暮光琥珀、绯梦霓光、澄光流璃、无相玄墨、听雨窗、巴哈姆特绝境战共 10 个模式，切换模式即可预览皮肤；其余 5 款绝本皮肤受 Figma 每个集合 10 个模式的上限，暂未加入。", { w: "fill" }),
  text("字号", { color: C.gold }),
  row({ name: "字号", gap: 28, align: "end" }, [
    col({ gap: 4 }, [text("页面标题 18", { size: F.title }), hint("ImGui ≈ 23 px", { w: "hug" })]),
    col({ gap: 4 }, [text("区块强调 15", { size: F.h2 }), hint("ImGui ≈ 20 px", { w: "hug" })]),
    col({ gap: 4 }, [text("正文 13"), hint("ImGui 17 px（默认）", { w: "hug" })]),
    col({ gap: 4 }, [text("说明 12", { size: F.small, color: C.muted }), hint("ImGui ≈ 16 px", { w: "hug" })]),
    col({ gap: 4 }, [text("角标 11", { size: F.tiny }), hint("ImGui ≈ 14 px", { w: "hug" })]),
  ]),
  text("组件", { color: C.gold }),
  row({ name: "按钮", gap: 12 }, [
    button("主要操作", "primary"), button("次要操作", "secondary"), button("带图标", "secondary", { icon: "refresh" }),
    button("主要带图标", "primary", { icon: "upload" }), button("危险操作", "danger"), button("文字链接", "ghost"),
    button("禁用", "secondary", { disabled: true }), iconButton("more", { bg: C.hover }),
  ]),
  row({ name: "开关与状态", gap: 16 }, [
    toggle(true), toggle(false), checkbox(true), checkbox(false),
    pill("运行中", "ok"), pill("等待同步", "warn"), pill("已禁用", "off"), pill("连接失败", "error"), pill("高风险", "error", { dot: false }), badge(3),
  ]),
  row({ name: "表单", gap: 16, align: "start" }, [
    col({ name: "c1", w: 240, gap: 12 }, [field("下拉选择", select("自动检测（国服）")), field("输入框", input("请输入账号名", { placeholder: true }))]),
    col({ name: "c2", w: 240, gap: 12 }, [field("滑块", slider(0.32, "750 毫秒")), field("分段选择", segmented(["经典榜", "横版", "职能分栏"], 0, { w: "fill" }))]),
    col({ name: "c3", w: 260, gap: 12 }, [toggleRow("开关行", true, "说明文字放在控件下方，统一 12 px 次要色。"), tabs(["常规", "快捷键", "来源"], 0)]),
    col({ name: "c4", w: 180, gap: 2, pad: 8, radius: 10, bg: C.raised }, [navItem("home", "当前页面", true, 0), navItem("users", "带未读", false, 2), navItem("gear", "普通导航", false, 0)]),
  ]),
  row({ name: "卡片", w: "fill", gap: 16, align: "start" }, [
    card("卡片标题（金色）", [hint("卡片：Raised 底、Border 75% 描边、10 px 圆角、14 px 内边距；页面内容 22 px 左右边距。"), toggleRow("示例开关", true)], { w: 360 }),
    col({ name: "规则", w: "fill", gap: 6 }, [
      text("布局规则", { color: C.gold }),
      hint("1. 左侧导航固定 176 px：主页、战斗统计、悬浮窗、扩展、好友｜账号与云同步、设置；运行状态和版本放在侧栏底部。"),
      hint("2. 窗口仍是横版控制中心（默认 920×640，最小 760×520），使用 DACT 新标志、深色底与冰蓝层次，外框和分隔线为与标志一致的靛蓝色。"),
      hint("3. 表单统一“标签在上、控件占满、说明在下”；开关行“标签在左、开关在右”。两栏网格避免长滚动。"),
      hint("4. 金色只用于卡片标题和提醒；冰蓝表示当前项、开启与链接；红色只用于危险操作、未读和高风险。"),
      hint("5. 低频与高级内容折叠或放进弹窗（FFLogs 凭据、扩展权限、文件位置）；旧版完整设置已全部并入各页。"),
    ]),
  ]),
]);

// ---------------------------------------------------------------- 功能对照表
const MAP = [
  ["顶栏", "徽标 / 名称", "顶栏左侧", "名称改为 DACT，不再叫 Dalamud ACT Compat"],
  ["顶栏", "解析器状态（● 运行中）", "侧栏底部 + 主页状态卡", "不再占顶栏中间"],
  ["顶栏", "版本号", "侧栏底部", ""],
  ["顶栏", "云同步状态", "顶栏右侧胶囊", "保留"],
  ["顶栏", "好友按钮 / 在线数 / 未读点", "侧栏「好友」+ 未读角标；在线数见主页状态卡和好友页", "好友抽屉改为页面；去掉顶栏重复入口"],
  ["顶栏", "帮助 ? / 关闭 ×", "顶栏右侧", "保留"],
  ["概览", "解析器卡片（状态与说明）", "主页 · 状态卡「解析器」", ""],
  ["概览", "关闭战斗统计 / 战斗历史 / 运行状态 / FFLogs 上传日志", "主页 · 快捷入口", ""],
  ["概览", "启用解析、自动启动解析器", "主页 · 基础设置", "改为开关"],
  ["概览", "游戏区域（含自动检测说明）", "主页 · 基础设置", ""],
  ["概览", "游戏解析范围", "主页 · 基础设置", ""],
  ["概览", "精简模式、失焦时隐藏网页悬浮窗", "主页 · 基础设置", ""],
  ["概览", "显示 ACT 快捷按钮（与设置页重复）", "设置 · 通用", "去重，只保留一处"],
  ["战斗统计", "显示战斗统计 / 定位到战斗统计", "战斗统计 · 页头", ""],
  ["战斗统计", "榜单模板 + 自定义", "战斗统计 · 榜单（分段选择）", ""],
  ["战斗统计", "显示范围、统计重置方式、刷新间隔、玩家 ID 显示", "战斗统计 · 榜单", ""],
  ["战斗统计", "重置当前战斗…", "战斗统计 · 榜单卡片标题右侧（危险按钮）", ""],
  ["战斗统计", "FFLogs Parse 预估说明与元数据", "战斗统计 · FFLogs 卡片", ""],
  ["战斗统计", "启用在线估算、Client ID/Secret、测试并刷新、创建/管理、创建指南", "战斗统计 · FFLogs 卡片", ""],
  ["战斗统计", "当前副本自动识别", "战斗统计 · FFLogs 卡片底部", ""],
  ["悬浮窗", "添加悬浮窗", "悬浮窗 · 页头", ""],
  ["悬浮窗", "Cactbot 状态 / 设置 / 安装更新 / 官方项目页 / 代称提示", "悬浮窗 · Cactbot 条", ""],
  ["悬浮窗", "我的悬浮窗列表、全部悬浮窗失焦隐藏", "悬浮窗 · 左侧列表", ""],
  ["悬浮窗", "打开 / 隐藏 / 更多，常规·快捷键·来源", "悬浮窗 · 右侧详情", "沿用 v3 已确认结构"],
  ["悬浮窗", "启动时自动打开、脱战隐藏、编辑位置和大小、穿透、锁定、页面缩放", "悬浮窗 · 常规标签", ""],
  ["扩展", "FFXIV_ACT_Plugin / OverlayPlugin 开关", "扩展 · 系统插件", ""],
  ["扩展", "五个兼容扩展的启停、状态、版本、打开配置", "扩展 · 兼容扩展列表", ""],
  ["扩展", "安装 DLL / ZIP、打开扩展文件夹、检查更新与来源", "扩展 · 页头按钮", ""],
  ["扩展", "启动时自动检查第三方扩展更新", "扩展 · 兼容扩展卡片标题右侧", "说明收进 ? 提示"],
  ["扩展", "ACT 插件权限边界（按扩展折叠）", "扩展 · 每行「权限」按钮 → 权限弹窗", "不再整段平铺"],
  ["云同步", "同步概览（状态、自动同步、云端版本、最近备份）", "账号与云同步 · 同步", ""],
  ["云同步", "打开游戏后自动同步、刷新状态、上传当前配置", "账号与云同步 · 同步", ""],
  ["云同步", "选择云端版本、预览恢复内容", "账号与云同步 · 备份与恢复", ""],
  ["云同步", "Discord 账号找回（进入服务器、绑定额度、恢复保护、找回说明）", "账号与云同步 · Discord 账号找回", ""],
  ["云同步", "邀请好友", "账号与云同步 · 邀请好友", ""],
  ["云同步", "支持 DACT / 爱发电", "设置 · 支持 DACT", "从云同步移到设置"],
  ["设置&账号", "外观与皮肤 / 管理皮肤", "设置 · 外观与皮肤", ""],
  ["设置&账号", "账号名、登录有效期、退出登录、修改密码", "账号与云同步 · 账号", "账号与云同步合并"],
  ["设置&账号", "解析器状态、重启解析器、详细运行状态、三方扩展声明", "设置 · 解析器与诊断", ""],
  ["设置&账号", "FFLogs 上传日志目录（更改 / 恢复默认）", "设置 · 数据与日志", ""],
  ["设置&账号", "复制诊断日志、打开日志文件夹", "设置 · 数据与日志", ""],
  ["设置&账号", "界面语言、调试模式", "设置 · 通用", ""],
  ["设置&账号", "显示 ACT 快捷按钮、尺寸、重置大小与位置", "设置 · 通用", ""],
  ["设置&账号", "历史记录上限", "设置 · 数据与日志", ""],
  ["设置&账号", "来信气泡（显示、位置、透明度、提示音、试听）", "好友 ·「来信提醒设置」独立页面，左上返回好友", "从设置页移到好友"],
  ["设置&账号", "恢复出厂设置…", "设置 · 页面底部危险区", ""],
  ["独立窗口", "高级设置（旧版完整设置）", "已全部并入：扩展（运行状态、自行安装扩展与权限）、设置 · 数据与日志（文件位置、历史上限确认）、设置 · 恢复出厂（确认与最近备份）", "移除「旧版完整设置」入口"],
  ["独立窗口", "三方扩展声明（来源说明、作者、许可证、项目/源码/下载、哈希、安装确认、权限选择、TTS Pro）", "13 三方扩展声明（独立窗口）", "内容不变，按 1.0 样式重画"],
  ["独立窗口", "运行状态", "侧栏底部「运行状态」/ 主页快捷入口", "窗口本身第二轮再改"],
  ["独立窗口", "好友抽屉（好友 / 申请 / 添加、我的状态、备注）", "好友页 · 左侧列表", ""],
  ["独立窗口", "赞助等级标（王冠 + Lv.N，红名）与管理员标志", "好友页各处名字后（身份名组件）：我的状态、列表、会话标题、消息、查找结果、申请", "沿用现版规则，不随皮肤变色"],
  ["独立窗口", "好友聊天窗口", "好友页 · 右侧会话 +「弹出为独立窗口」", "游戏中仍可单独浮动"],
  ["账号", "登录 / 注册 / 重置密码 / 旧账号恢复", "未登录页（保持横版与原风格）", ""],
  ["账号", "Discord 绑定门槛（绑定码、有效期、三步说明、重新检查、退出登录）", "03b「绑定 Discord 后继续」，沿用登录页框架", "步骤自带按钮，状态单独一条"],
  // The combat meter overlays themselves are out of scope for 1.0 (user decision, 2026-11-07).
  ["其他", "战斗统计悬浮窗本体（竖版 / 横版 / 职能分栏）", "保持现状", "不重做；样式编辑器见 07「自定义布局」"],
  ["独立窗口", "使用帮助（9 页、搜索、配图、复制命令、运行状态 / 日志目录 / 第三方声明 / 源代码与许可证入口）", "09 使用帮助：左侧导航 + 搜索框，内容为卡片", "横排页签改左侧导航；文案改指 1.0 新位置；配图换新界面"],
  ["独立窗口", "战斗记录（近期战斗、日志文件、队伍表现、技能统计、原始 JSON）", "侧栏「战斗记录」页，新增开荒进度与 AI 复盘入口", "独立窗口改为页面"],
  ["其他", "快捷按钮、来信气泡", "本稿不改", "第二轮"],
];
function mappingTable() {
  const cell = (value, width, color = C.text) => text(value, { size: F.small, w: width, color });
  return col({ name: "功能对照表", w: 1180, pad: 28, gap: 0, bg: C.surface, radius: 12 }, [
    col({ name: "标题", gap: 4, pad: [0, 0, 16, 0] }, [
      text("功能对照表：0.4.5.20 → 1.0 草稿", { size: F.title }),
      hint(`共 ${MAP.length} 项，原有功能全部保留，只调整位置；“说明”列标出去重、合并或折叠。`, { w: "hug" }),
    ]),
    row({ name: "表头", w: "fill", pad: [8, 10], gap: 12, bg: C.hover, radius: 6 }, [
      cell("原页面", 80, C.gold), cell("功能", 390, C.gold), cell("新位置", 370, C.gold), cell("说明", 220, C.gold),
    ]),
    ...MAP.map((item, i) => row({ name: `行${i + 1}`, w: "fill", pad: [7, 10], gap: 12, align: "start", bg: i % 2 ? C.raised : null }, [
      cell(item[0], 80, C.muted), cell(item[1], 390), cell(item[2], 370, C.accent), cell(item[3], 220, C.muted),
    ])),
  ]);
}

// ---------------------------------------------------------------- 战斗统计 · 自定义布局
// MeterStyleEditorWindow (its own 1100×760 window, opened by 战斗统计 →「自定义…」). Every
// control of 0.4.5.20 is kept; slots and the selected slot's options share the left pane,
// window appearance gets the right pane, and the live preview stays in the middle.
// The Figma side reads colours as #RRGGBB or rgba(), so translucent role tints use rgba.
const alpha = (hex, a) => `rgba(${[1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16)).join(",")},${a})`;
const JOB = { tank: "#3E5C8F", healer: "#3C7A5A", melee: "#8E3B3B", ranged: "#7E6E30", caster: "#62479A" };
const PREVIEW = [
  ["PLD", "tank", "自己", "15,800", "5.9%", "爆发击 128k", 1.0, true], ["WAR", "tank", "队友 01", "15,390", "5.8%", "强力技能 126.3k", .97],
  ["WHM", "healer", "队友 02", "14,980", "5.6%", "爆发击 124.6k", .95], ["SCH", "healer", "队友 03", "14,570", "5.5%", "强力技能 122.9k", .92],
  ["SAM", "melee", "队友 04", "14,160", "5.3%", "爆发击 121.2k", .9], ["NIN", "melee", "队友 05", "13,750", "5.2%", "强力技能 119.5k", .87],
  ["BRD", "ranged", "队友 06", "13,340", "5.0%", "爆发击 117.8k", .84], ["BLM", "caster", "队友 07", "12,930", "4.9%", "强力技能 116.1k", .82],
];
function previewRow([job, role, name, dps, share, top, pct, self], i) {
  return row({
    name: `行/${name}`, w: "fill", h: 30, pad: [0, 10], gap: 8, radius: 4, bg: "rgba(18,24,31,0.85)", clip: true,
    border: self ? { c: "rgba(199,168,92,0.85)", w: 1 } : null,
  }, [
    // Rows are 406 px wide in the 1100 px window (282 + 300 side panes, paddings and gaps).
    frame({ name: "数据条", abs: true, x: 0, y: 0, w: Math.round(406 * pct), h: 30, radius: 4, bg: self ? "rgba(199,120,60,0.30)" : alpha(JOB[role], .33) }),
    text(String(i + 1), { size: F.small, color: C.muted, w: 14, align: "right" }),
    row({ name: "职业", w: 38, h: 20, radius: 4, bg: JOB[role], justify: "center" }, [text(job, { size: F.small })]),
    text(name, { w: "fill", color: self ? C.gold : C.text }),
    text(share, { size: F.small, color: C.muted, w: 40, align: "right" }),
    text(dps, { color: C.accent, w: 52, align: "right" }),
    text(top, { size: F.small, color: "#E8A15A", w: 104, align: "right" }),
  ]);
}
// Only state words go on a slot row (排序 on the template's DPS ranking metric, 未启用 on FFLogs
// while online estimates are off). No icon marks slots with their own options: an icon there reads
// as a clickable settings button, and every slot opens in 所选槽位 anyway.
function slotRow(no, label, on, selected) {
  const off = label === "FFLogs";
  return row({ name: `槽位/${label}`, w: "fill", h: 30, pad: [0, 8], gap: 8, radius: 6, bg: selected ? C.hover : null,
    border: selected ? { c: "rgba(107,199,245,0.55)", w: 1 } : null }, [
    checkbox(on), text(no, { size: F.small, color: C.muted, w: 18 }),
    text(label, { w: "fill", color: off ? "rgba(166,179,194,0.55)" : on || selected ? C.text : C.muted }),
    label === "DPS" ? pill("排序", "warn", { dot: false }) : null,
    off ? pill("未启用", "off", { dot: false }) : null,
  ]);
}
const SLOTS = [["01", "排名", false], ["02", "职业 / ID", true], ["03", "FFLogs", false], ["04", "DPS", true], ["05", "rDPS", true],
  ["06", "EncDPS", false], ["07", "ExtDPS", false], ["08", "HPS", false], ["09", "伤害占比", false], ["10", "全队总伤害", true],
  ["11", "最高伤害", true], ["12", "死亡", false], ["13", "暴击率", false], ["14", "直击率", false], ["15", "直暴率", false],
  ["16", "全队总治疗", false], ["17", "最高治疗", false]];
const chip = (label, on) => row({ name: `开关/${label}`, gap: 8, h: 28, pad: [0, 10], radius: 14, bg: C.raised, border: { c: C.cardBorder, w: 1 } },
  [toggle(on), text(label, { color: on ? C.text : C.muted })]);
const colorField = (label, swatch, value, actions) => field(label, row({ name: "颜色", w: "fill", gap: 8 }, [
  frame({ name: "色块", w: 30, h: 30, radius: 6, bg: swatch, border: { c: "rgba(255,255,255,0.18)", w: 1 } }),
  input(value, { w: "fill" }), ...(actions || []),
]));
const meterEditor = col({ name: "布局编辑器", w: 1100, h: 760, bg: C.surface, radius: 10, clip: true, border: { c: C.border, w: 1 } }, [
  row({ name: "顶栏", w: "fill", h: 48, pad: [0, 10, 0, 14], gap: 10 }, [
    img("logo", 26, 26, { radius: 6 }), text("战斗统计 · 自定义布局"), hint("改动实时预览", { w: "hug" }), spacer(), iconButton("help"), iconButton("close"),
  ]),
  divider(),
  col({ name: "模板栏", w: "fill", pad: [14, 18, 10], gap: 10 }, [
    row({ name: "模板", w: "fill", gap: 12 }, [
      segmented(["经典榜", "横版", "职能分栏"], 0), pill("当前模板已启用", "ok"), hint("启用后会自动停用另外两个模板。", { w: "fill" }),
    ]),
    row({ name: "窗口行为", w: "fill", gap: 8 }, [chip("锁定", false), chip("锁定时鼠标穿透", false), chip("脱战隐藏", false), chip("只显示自己", false)]),
  ]),
  row({ name: "编辑区", w: "fill", h: "fill", pad: [0, 18], gap: 14, align: "stretch" }, [
    card("槽位", [
      // Selecting a slot (here or a preview column) switches the right pane to 所选槽位, where the
      // slot's content, its own options, order and display rules are edited.
      hint("勾选要显示的槽位；点中一行（或预览里的那一列）后在右栏编辑。拖动可改顺序。"),
      col({ name: "槽位列表（独立滚动）", w: "fill", h: "fill", gap: 2, clip: true }, SLOTS.map(([no, label, on], i) => slotRow(no, label, on, i === 0))),
      row({ name: "槽位操作", w: "fill", gap: 6 }, [button("添加槽位", "secondary", { icon: "plus", w: "fill" }), button("恢复默认槽位", "ghost")]),
    ], { w: 282, h: "fill" }),
    card("页面预览", [
      hint("8 人本按槽位顺序显示表格列；24 人本使用固定紧凑条。点栏目选择，拖动交换顺序。"),
      col({ name: "战斗统计预览", w: "fill", h: "fill", pad: 10, gap: 4, radius: 8, bg: "#0B1016", border: { c: C.cardBorder, w: 1 } }, [
        row({ name: "预览标题", w: "fill", gap: 8, h: 36 }, [
          icon("meter", { color: C.gold }), col({ name: "战斗", w: "fill", gap: 0 }, [text("预览战斗", { color: C.gold }), hint("预览副本 · 03:00 · 战斗中")]),
          pill("DPS 榜", "ok"), pill("8 人本", "warn", { dot: false }),
        ]),
        row({ name: "表头", w: "fill", h: 22, pad: [0, 10], gap: 8 }, [
          text("#", { size: F.small, color: C.muted, w: 14, align: "right" }), text("职业 / ID", { size: F.small, color: C.muted, w: "fill" }),
          text("占比", { size: F.small, color: C.muted, w: 40, align: "right" }), text("DPS", { size: F.small, color: C.muted, w: 52, align: "right" }),
          text("最高伤害", { size: F.small, color: C.muted, w: 104, align: "right" }),
        ]),
        ...PREVIEW.map(previewRow),
        vspacer(),
        row({ name: "合计", w: "fill", pad: [6, 10, 0], gap: 8 }, [hint("全队总伤害", { w: "fill" }), text("47.89m", { color: C.gold })]),
      ]),
    ], { w: "fill", h: "fill" }),
    card("属性", [
      segmented(["窗口外观", "所选槽位"], 0, { w: "fill" }),
      toggleRow("显示标题区", true),
      field("字号", slider(0.5, "1.00", { trackWidth: 170 })),
      colorField("背景颜色", "#091017", "9, 14, 23"),
      row({ name: "背景按钮", w: "fill", gap: 6 }, [button("恢复默认背景", "secondary"), button("底板全透明", "secondary")]),
      field("底板不透明度", slider(0.85, "85%", { trackWidth: 170 })),
      field("彩色数据条不透明度", slider(0.17, "17%", { trackWidth: 170 })),
      colorField("自己的强调色", "#8B5733", "139, 87, 51, 115"),
      field("收起方向", select("向上")),
    ], { w: 300, h: "fill" }),
  ]),
  row({ name: "底栏", w: "fill", h: 60, pad: [0, 18], gap: 8 }, [
    hint("预览随设置实时变化；保存后写入配置，取消恢复打开时的样式。", { w: "fill" }), button("取消", "secondary"), button("保存", "primary"),
  ]),
]);

// ---------------------------------------------------------------- 设置 · 管理皮肤
// SkinSettingsPanel in the new layout: a grouped thumbnail grid (scrolls on its own) and a
// detail card for the previewed skin; applying stays explicit. Same states as the original:
// in use, unlocked, sponsor tier, ultimate clear, and undiscovered (神秘配色).
function skinTile(id, status, tone, opts = {}) {
  const k = skins.skin(id), selected = !!opts.selected;
  return col({
    name: `皮肤卡/${opts.mystery ? "神秘配色" : k.name}`, w: "fill", pad: 6, gap: 3, radius: 8,
    bg: selected ? C.hover : C.raised, border: { c: selected ? C.accent : C.cardBorder, w: 1 },
  }, [
    vector(skins.thumb(id, 98, 58, opts.mystery), 98, 58, { name: "缩略图", ref: `thumb|${id}|98|58|${opts.mystery ? 1 : 0}` }),
    text(opts.mystery ? "神秘配色" : k.name, { size: F.small, w: "fill" }),
    text(status, { size: F.tiny, color: tone, w: "fill" }),
  ]);
}
function skinGroup(label, tiles) {
  const rows = [];
  for (let i = 0; i < tiles.length; i += 3) {
    const slice = tiles.slice(i, i + 3);
    while (slice.length < 3) slice.push(frame({ name: "占位", w: "fill", h: 1 }));
    rows.push(row({ name: "行", w: "fill", gap: 8, align: "start" }, slice));
  }
  return col({ name: `分组/${label}`, w: "fill", gap: 8 }, [text(label, { size: F.small, color: C.muted }), ...rows]);
}
const OK = C.accent, MUTED = C.muted;
const skinManager = shell("settings", [
  pageHeader("外观与皮肤", "设置 / 外观与皮肤 · 点击卡片预览，应用后自动保存。", [button("返回设置", "secondary")]),
  row({ name: "皮肤浏览", w: "fill", h: "fill", gap: 14, align: "stretch" }, [
    row({ name: "皮肤收藏", w: 372, h: "fill", gap: 4, align: "stretch" }, [
      col({ name: "列表（独立滚动）", w: "fill", h: "fill", gap: 14, clip: true }, [
        skinGroup("基础", [skinTile("default", "● 正在使用", OK), skinTile("eorzea", "赞助 3 级解锁", MUTED)]),
        skinGroup("探索配色", [
          skinTile("jade", "已解锁", MUTED), skinTile("amethyst", "已解锁", MUTED), skinTile("amber", "已解锁", MUTED),
          skinTile("neon-pink", "已解锁", MUTED), skinTile("liquid-glass", "已解锁", MUTED), skinTile("obsidian", "已解锁", MUTED),
          skinTile("rainy-window", "等待发现", MUTED, { mystery: true }),
        ]),
        skinGroup("绝境战", skins.ORDER.filter(id => id.startsWith("ultimate")).map(id =>
          skinTile(id, "已解锁", MUTED, { selected: id === "ultimate-ucob" }))),
      ]),
      col({ name: "滚动条", w: 4, h: "fill", radius: 2, bg: C.raised }, [frame({ name: "滑块", w: 4, h: 190, radius: 2, bg: "rgba(166,179,194,0.45)" })]),
    ]),
    card("巴哈姆特绝境战", [
      vector(skins.thumb("ultimate-ucob", 283, 164), 283, 164, { name: "大预览", ref: "thumb|ultimate-ucob|283|164|0" }),
      hint("取自这一绝境战的冒险者铭牌：配色、徽记与按钮，控制中心背后显示铭牌背景。"),
      text("当前账号已永久解锁全部六款绝本皮肤。", { size: F.small, color: C.accent, w: "fill" }),
      vspacer(),
      divider(),
      row({ name: "应用", w: "fill", gap: 8 }, [hint("当前仅预览；点击应用后保存。"), button("应用皮肤", "primary")]),
    ], { h: "fill", titleExtra: [pill("已解锁", "ok")] }),
  ]),
]);

const helpBoards = require("./help")({ navItem, pageHeader });
const featureBoards = require("./features")({ shell, pageHeader, statTile, homeContent });
const noticeBoards = require("./notices")();

module.exports = {
  title: "DACT 1.0 控制中心改版 · 草稿 v1",
  // Library-only samples (no board shows them as-is): every MenuItem state, and the
  // DropdownMenu main component with the longest option list (游戏解析范围).
  components: [
    menuItem("所有人", "normal"), menuItem("所有人", "hover"), menuItem("所有人", "selected"),
    dropdownMenu(["自动（按实际组队）", "仅自己", "小队成员", "团队成员", "所有人"], { selected: 4, hover: 1 }),
  ],
  sections: [
    { name: "00 设计规范与组件", screens: [{ name: "设计规范", tree: designSystem }] },
    {
      name: "01 控制中心（默认 920×640）", screens: [
        { name: "主页", tree: home },
        { name: "战斗统计", tree: meter },
        { name: "悬浮窗", tree: overlays },
        { name: "扩展", tree: extensions },
        { name: "好友", tree: friends },
        { name: "账号与云同步", tree: cloud },
      ],
    },
    {
      name: "02 设置（长页，游戏内滚动）", screens: [
        { name: "设置", tree: settings, note: "高于 640 时内容区滚动" },
        { name: "设置 · 管理皮肤", tree: skinManager, note: "皮肤列表区独立滚动；返回与应用始终可见" },
      ],
    },
    { name: "03 未登录与弹窗", screens: [{ name: "登录（横版）", tree: login }, { name: "扩展权限弹窗", tree: permissions }] },
    {
      name: "03b 账号流程", screens: [
        { name: "创建账号", tree: register, note: "免费激活码说明与 QQ / Discord 入口留在表单旁" },
        { name: "找回密码", tree: resetPassword, note: "第 2 步「设置新密码」与创建账号的密码字段相同" },
        { name: "绑定 Discord 后继续", tree: discordGate, note: "登录后未绑定时显示；绑定码过期或服务不可用时状态条变红" },
      ],
    },
    { name: "04 功能对照", screens: [{ name: "功能对照表", tree: mappingTable() }] },
    // 05 皮肤预览 is made in Figma by switching variable modes on board copies, not from here.
    {
      name: "06 交互状态", screens: [
        { name: "主页 · 下拉展开", tree: homeScreen(true) },
        { name: "战斗统计 · 展开 FFLogs 创建指南", tree: meterScreen(true), note: "高于 640 时内容区滚动" },
      ],
    },
    { name: "06b 扩展状态", screens: [{ name: "扩展 · 运行状态与自行安装的扩展", tree: extensionsScreen(true), note: "列表变长时内容区滚动" }] },
    { name: "07 独立窗口", screens: [{ name: "战斗统计 · 自定义布局", tree: meterEditor, note: "1100×760 独立窗口，由战斗统计「自定义…」打开" }] },
    // 08 好友弹层 was drawn straight in Figma; 09 is the help window opened from the header ?.
    { name: "09 使用帮助", screens: helpBoards },
    { name: "12 1.0 新功能", screens: featureBoards },
    { name: "13 三方扩展声明", screens: noticeBoards },
  ],
};
