// 使用帮助 (HelpWindow.cs) in the 1.0 layout. Same nine pages, search and actions as 0.4.5.20;
// the one-row page tabs become the control center's left navigation (full labels, room for
// longer languages), and every sentence that points at a control now names its 1.0 location.
// Usage notice wording is the user's own and stays verbatim.
"use strict";
const {
  C, F, frame, row, col, spacer, text, hint, img, vector, divider, button, iconButton, input, pill, card, stepsPanel,
} = require("./dsl");

const HELP_W = 1100, HELP_H = 760;
// Content column = 1100 − sidebar 196 − divider 1 − padding 44; a card adds 2 × 14 padding.
// Control-center screenshots (920 × 640 boards) are scaled to that inner width.
const SHOT_W = 831, SHOT_H = 578;

const NAV = [
  ["notice", "shield", "使用须知"], ["start", "play", "快速上手"],
  null,
  ["meter", "meter", "战斗统计"], ["overlays", "layers", "悬浮窗"], ["extensions", "plug", "扩展与权限"], ["account", "cloud", "账号与云同步"],
  null,
  ["fixes", "wrench", "遇到问题"], ["commands", "terminal", "宏指令"], ["copyright", "copyright", "版权声明"],
];

module.exports = ({ navItem, pageHeader }) => {
  // query: text in the search box (results state shows × to clear and return to the page).
  function sidebar(active, query) {
    return col({ name: "侧栏", w: 196, h: "fill", pad: [12, 10], gap: 2 }, [
      input(query || "搜索功能、问题或命令", { placeholder: !query, trailing: query ? "close" : "search" }),
      frame({ name: "间距", w: 1, h: 8 }),
      ...NAV.map(item => item === null
        ? divider({ name: "分隔", margin: true, h: 1 })
        : navItem(item[1], item[2], item[0] === active, 0)),
    ]);
  }
  // full: the board grows with the page (in game the content column scrolls).
  function win(active, content, full = false, query = null) {
    return col({ name: "使用帮助", w: HELP_W, h: full ? "hug" : HELP_H, bg: C.surface, radius: 10, clip: true, border: { c: C.border, w: 1 } }, [
      row({ name: "顶栏", w: "fill", h: 48, pad: [0, 10, 0, 14], gap: 10 }, [
        img("logo", 26, 26, { radius: 6 }), text("使用帮助"), hint("使用文档", { w: "hug" }), spacer(),
        text("v1.0.0.0", { size: F.small, color: C.muted }), iconButton("close"),
      ]),
      divider(),
      row({ name: "主体", w: "fill", h: full ? "hug" : "fill", align: "stretch" }, [
        sidebar(active, query),
        frame({ name: "侧栏分隔", w: 1, h: "fill", bg: C.divider }),
        col({ name: "内容", w: "fill", h: full ? "hug" : "fill", pad: [18, 22], gap: 14, clip: !full }, content),
      ]),
    ]);
  }

  // ---- Content blocks (HelpWindow Paragraph / Bullet / Step / Definition / Callout / Image / command).
  const para = (value, color = C.text) => text(value, { w: "fill", color });
  const bullets = items => col({ name: "列表", w: "fill", gap: 6 }, items.map(item => row({ name: "条目", w: "fill", gap: 6, align: "start" }, [
    row({ name: "圆点", w: 12, h: 18, justify: "center" }, [frame({ name: "dot", w: 4, h: 4, radius: 2, bg: C.muted })]), para(item),
  ])));
  // Term column on the left reads as "symptom → what to do" on 遇到问题.
  const defs = (items, termW = 132) => col({ name: "释义", w: "fill", gap: 10 }, items.map(([term, desc]) => row({ name: `释义/${term}`, w: "fill", gap: 14, align: "start" }, [
    text(term, { w: termW, color: C.accent }), para(desc),
  ])));
  // Ice blue for tips, red for cautions (gold stays reserved for card titles and the brand).
  function callout(kind, value) {
    const warn = kind === "warn";
    return row({ name: warn ? "注意" : "提示", w: "fill", gap: 10, radius: 8, pad: [10, 12, 10, 10], align: "stretch", bg: warn ? C.dangerSoft : C.accentSoft }, [
      frame({ name: "色条", w: 3, h: "fill", radius: 2, bg: warn ? C.danger : C.accent }),
      col({ name: "正文", w: "fill", gap: 2 }, [text(warn ? "注意" : "提示", { size: F.small, color: warn ? C.danger : C.accent }), para(value)]),
    ]);
  }
  const framed = child => frame({ name: "截图框", radius: 6, clip: true, border: { c: C.cardBorder, w: 1 } }, [child]);
  // Screenshot of a redesigned page: a placeholder that help-shots.js fills with a scaled copy
  // of that 1.0 board, so the help always shows the current design.
  function shot(board, caption) {
    const markup = `<svg xmlns="http://www.w3.org/2000/svg" width="${SHOT_W}" height="${SHOT_H}"><rect width="100%" height="100%" fill="#0B1016"/>` +
      `<text x="50%" y="50%" fill="#A6B3C2" font-size="14" text-anchor="middle" font-family="Noto Sans SC">截图：${board}（1.0 画板缩放）</text></svg>`;
    return col({ name: `截图/${board}`, w: "fill", gap: 6 }, [framed(vector(markup, SHOT_W, SHOT_H, { name: "截图", ref: `shot|${board}` })), hint(caption)]);
  }
  // Screens 1.0 does not redraw (meter overlay, Discord gate) keep the shipped help image.
  const picture = (src, w, h, caption) => col({ name: `截图/${caption}`, w: "fill", gap: 6 }, [framed(img(src, w, h, { name: "截图" })), hint(caption)]);
  const command = (cmd, desc) => row({ name: `命令/${cmd}`, w: "fill", gap: 12 }, [
    col({ name: "说明", w: "fill", gap: 4 }, [
      row({ name: "命令", h: 24, pad: [0, 8], radius: 5, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [text(cmd, { color: C.accent })]),
      hint(desc),
    ]),
    button("复制", "secondary", { icon: "copy" }),
  ]);
  const commands = list => list.flatMap((c, i) => i ? [divider(), command(...c)] : [command(...c)]);

  // ---------------------------------------------------------------- pages
  const notice = win("notice", [
    pageHeader("使用须知", "请先理解边界，再使用解析器、悬浮窗和第三方扩展。"),
    card("使用前请确认", [para("使用任何 Dalamud、ACT 或相关第三方工具之前，请自行了解对应游戏的用户协议以及第三方工具相关规则。")]),
    card("项目本身不鼓励", [bullets(["PVP 信息优势", "影响其他玩家正常游戏", "利用解析数据骚扰其他玩家", "在公开环境跳脸", "使用插件进行不当行为"])]),
    card("特别提醒", [col({ name: "段落", w: "fill", gap: 6 }, [
      para("不要去绿玩面前跳脸。", C.danger), para("你自己开插件是一回事。"), para("跑到别人面前："),
      para("“你看我这个插件多牛逼！”", C.accent), para("然后把截图、悬浮窗、DPS、第三方工具全甩别人脸上，是另一回事。"),
      para("一经发现立刻踢出！", C.danger), para("请保持最基本的边界感。"),
    ])]),
  ]);

  const start = win("start", [
    pageHeader("快速上手", "第一次使用？按顺序完成下面四步。"),
    card("第 1 步 · 登录 DACT 账号", [
      para("点游戏画面上的 ACT 按钮或输入 /actcompat 打开控制中心。没有账号就点「创建账号」，有账号直接登录；勾选「记住登录状态」后下次会自动登录。"),
      shot("登录（横版）", "控制中心的登录界面"),
    ]),
    card("第 2 步 · 绑定 Discord", [
      para("第一次登录后需要绑定 Discord 才能继续，按界面上的三步操作即可："),
      stepsPanel([
        "点「复制绑定码并前往 Discord」，加入 DACT 的 Discord 服务器。",
        "在成员列表的「Bot」分组找到 DACT，右键选择「发消息」。",
        "私聊输入 /dact，选择绑定账号并粘贴绑定码。回到游戏后会自动进入。",
      ]),
      picture("help-discord", 720, 507, "绑定 Discord（这一页 1.0 不改）"),
      callout("tip", "绑定后忘记密码或换电脑，都能通过 Discord 找回账号和云备份。一个 Discord 最多绑定 3 个 DACT 账号。"),
    ]),
    card("第 3 步 · 确认解析器在运行", [
      para("打开「主页」，「解析器」状态卡和侧栏底部显示「运行中」就说明一切正常。请保持基础设置里的「启用解析」和「自动启动解析器」开启。"),
      shot("主页", "主页：解析器状态、快捷入口和基础设置"),
    ]),
    card("第 4 步 · 打一下木人", [
      para("对木人或副本里的敌人造成几次伤害，战斗统计就会显示你和队友。右键 ACT 按钮可以打开或关闭战斗统计。"),
      picture("help-meter", 540, 300, "战斗统计（经典榜，悬浮窗本体 1.0 不改）"),
      callout("tip", "没有数据？看「遇到问题」。控制中心右上角的 ? 随时可以打开这份帮助。"),
    ]),
  ], true);

  const meter = win("meter", [
    pageHeader("战斗统计", "调整统计窗的外观、显示的数据和重置方式。"),
    card("打开与摆放", [
      bullets([
        "在「战斗统计」页打开「显示战斗统计」；找不到窗口时点「定位到战斗统计」，或输入 /actcompat meter。",
        "点「自定义…」打开自定义布局，取消「锁定」后可以拖动、缩放，摆好再锁定。开启「锁定时鼠标穿透」后，点击会穿过统计窗传给游戏。",
        "「脱战隐藏」只是把窗口藏起来，统计仍在继续。",
      ]),
      shot("战斗统计", "战斗统计页"),
    ]),
    card("榜单模板与显示的数据", [bullets([
      "「经典榜」「横版」「职能分栏」三选一；点「自定义…」可以调字号、透明度、背景色，以及要显示的槽位。",
      "经典榜标题栏可以在 DPS 榜 / HPS 榜、8 人本 / 24 人本之间切换。",
      "「显示范围」决定显示哪些玩家：按榜单设置，或跟随主页的「游戏解析范围」。",
      "「玩家 ID 显示」可以遮盖名字，只影响界面，不会修改日志。",
    ])]),
    card("数值是怎么算的", [defs([
      ["DPS", "按你自己的出手时间计算，晚进场或死亡后会比整场口径高。"],
      ["EncDPS", "按整场战斗时长计算，适合比较同一场里的队友。"],
      ["rDPS（预估）", "去掉别人给你的团辅、加回你给队友的团辅。这是本地实时估算，和 FFLogs 会有差异。"],
      ["HPS", "按开怪到现在的完整时间计算，不再治疗时会慢慢下降。"],
      ["暴击 / 直击 / 直暴", "本场命中里暴击、直击、同时暴击直击的比例，不是角色面板数值。样本少时跳动是正常的。"],
      ["--", "还没有有效数值，不等于 0。"],
    ])]),
    card("统计重置与历史", [bullets([
      "「DACT默认」：副本里普通脱战和转阶段继续累计，团灭或离开副本才结算。",
      "「脱战指定秒数后重置」：脱战后倒计时 0–3600 秒再清空；倒计时内重新进战会取消。",
      "「重置当前战斗…」立即清空本场，不会删除历史和原始日志。主页「战斗历史」或输入 /actcompat history 查看近期战斗。",
    ])]),
    card("FFLogs DPS Parse 预估", [
      stepsPanel([
        "在「战斗统计」页的 FFLogs 卡片里开启「启用 FFLogs 在线估算」。",
        "点「创建 / 管理 API Client」，登录 FFLogs 创建免费 Client；没有网站时回调地址填 https://example.com。",
        "把 Client ID 和 Client Secret 填进对应输入框，点「测试并刷新」。",
      ]),
      callout("warn", "Client Secret 只保存在本机，不要截图或发给别人。"),
      callout("tip", "预估只是参考：它用本场 DPS 对照缓存的排名分布。上传到 FFLogs 后的成绩以官网为准。"),
    ]),
  ], true);

  const overlays = win("overlays", [
    pageHeader("悬浮窗", "Cactbot 提醒、时间轴和其他网页悬浮窗。"),
    card("Cactbot", [
      bullets([
        "DACT 自带 Cactbot。「文字提醒」和「时间轴」是两个独立窗口，可以同时打开；旧版「文字提醒 + 时间轴」组合窗和它们二选一。",
        "「悬浮窗」页顶部的 Cactbot 条里，「Cactbot 设置」可以改提醒语言、播报方式和自定义脚本目录；「安装 / 更新」用于更新或换用自己的 Cactbot 包。",
      ]),
      shot("悬浮窗", "悬浮窗页：左侧选择悬浮窗，右侧调整它的设置"),
    ]),
    card("添加网页悬浮窗", [stepsPanel([
      "点页面右上角「添加悬浮窗」。",
      "选择模板，或粘贴完整的 http / https / file 地址。只添加你信任的网页。",
      "在右侧「常规」里设置「启动时自动打开」「脱战隐藏」等选项。",
    ])]),
    card("移动、缩放与鼠标穿透", [bullets([
      "点「编辑位置和大小」后拖动窗口或右下角，完成后点「完成位置编辑」。编辑时会暂时关闭穿透与锁定，完成后恢复。",
      "鼠标穿透开启时点击会传给游戏，网页本身点不动；需要操作网页时先关闭穿透。",
      "字太小就调「页面缩放」，内容被截断就把窗口拉大。",
    ])]),
    card("有画面但没有数据", [para("先确认侧栏底部显示「解析器运行中」，再到该悬浮窗的「来源」标签，在「连接高级设置」里点「重新检测」。部分网页还需要联网或单独设置。")]),
  ], true);

  const extensions = win("extensions", [
    pageHeader("扩展与权限", "触发器、语音、邮差等 ACT 扩展的启用、配置和权限。"),
    card("自带的扩展", [
      defs([
        ["Triggernometry", "触发器：根据战斗日志播报、计时和绘图。"],
        ["鲶鱼精邮差 / PostNamazu", "让触发器执行游戏指令、标点和地标。"],
        ["ACT.FoxTTS", "把文字提醒读出来。"],
        ["银山雀儿 / SilverDasher", "狩猎、临危受命和跨区提醒。默认关闭，需要时再打开。"],
        ["抹茶 / Cafe.Matcha", "排本、临危受命和过图提醒。"],
      ], 170),
      shot("扩展", "扩展页"),
    ]),
    card("启用和打开配置", [
      stepsPanel(["在「扩展」页打开扩展那一行的开关。", "点同一行的「打开配置」。导入触发器、选择语音等都在扩展自己的窗口里完成。"]),
      callout("tip", "启用扩展或修改权限后，DACT 会自动重启这个扩展，不用重启游戏。"),
    ]),
    card("权限", [
      para("在「扩展」页点扩展那一行的「权限」，可以按扩展逐项开放能力。只开实际要用的："),
      defs([
        ["网络请求", "在线更新、远程触发器仓库和语音服务。"],
        ["发送游戏指令", "邮差的指令、标点和地标。"],
        ["访问游戏原生内存", "银山雀儿和邮差的部分原版功能。"],
        ["运行高风险脚本", "触发器里的高级脚本。"],
        ["写入文件", "扩展缓存，或导出到你选择的文件。"],
      ]),
      callout("warn", "只安装来源可信的扩展。权限限制的是 DACT 提供的接口，挡不住 DLL 直接调用 Windows。"),
      row({ name: "按钮", w: "fill" }, [button("查看内置第三方 DLL 的作者与来源", "secondary")]),
    ]),
    card("安装其他 ACT 插件", [para("点「扩展」页右上角「安装 DLL / ZIP」，或输入 /actcompat install \"路径\"。DACT 会先检查文件并列出它需要的权限，你同意后它才会运行。")]),
    card("更新后还是旧版本", [para("外部工具箱更新扩展后，点侧栏底部「运行状态」（或输入 /actcompat status），点「重启共享 Host」，相当于以前的“重启 ACT”，不用退出游戏。抹茶点「重启抹茶 Host」，自己安装的插件点「重启通用 Host」。")]),
  ], true);

  // 设置&账号 split in 1.0: the account block lives on 账号与云同步, the rest on 设置.
  const account = win("account", [
    pageHeader("账号与云同步", "账号、云备份、找回账号、好友、皮肤和其他设置。"),
    card("云同步", [
      bullets([
        "打开「打开游戏后自动同步配置」后，每次登录会自动检查并备份一次；配置没变就不上传。云端保留最近两份。",
        "需要恢复时先选择云端版本、预览恢复内容，再确认恢复；恢复前的配置会被保留，可以回滚。",
      ]),
      shot("账号与云同步", "账号与云同步页"),
    ]),
    card("账号", [para("在「账号与云同步」页查看账号名和登录有效期，退出登录或修改密码。")]),
    card("找回账号", [para("账号绑定 Discord 后，忘记密码或换电脑时，在 Discord 私聊 DACT 输入 /dact，按菜单找回账号和云备份。")]),
    card("好友", [para("点侧栏「好友」打开好友页，可以添加好友、聊天；右键好友可以设置备注，备注随账号在各设备同步。有未读消息时「好友」旁会显示数字。")]),
    card("设置", [
      bullets([
        "外观与皮肤：点「管理皮肤」预览和切换，部分皮肤需要对应的赞助等级。",
        "解析器与诊断：重启解析器、打开详细运行状态或查看三方扩展声明。",
        "通用：界面语言和 ACT 快捷按钮；数据与日志：诊断日志、FFLogs 上传日志目录和历史记录上限。",
        "恢复出厂设置在页面最底部，会移走现有数据，只在最后才用。",
      ]),
      shot("设置", "设置页"),
    ]),
  ], true);

  const fixes = win("fixes", [
    pageHeader("遇到问题", "按顺序检查，大多数问题不用重启游戏。"),
    // 1.0: the AI can run these checks for you; the manual steps below stay for reference.
    card("先让 AI 看看", [
      para("AI 助手默认关闭：在侧栏「AI 助手」点「开启并设置」，填好自己的 API Key 后，点「让 AI 诊断」或直接在 AI 助手里提问。它会读取运行状态、扩展权限和诊断日志（先去掉角色名），找出问题并给出可以直接点的修复按钮；解决不了时帮你把日志发给开发者。"),
      row({ name: "按钮", w: "fill", gap: 8 }, [button("让 AI 帮我诊断", "primary", { icon: "sparkle" }), button("手动反馈", "secondary", { icon: "feedback" })]),
    ]),
    card("第 1 步 · 先看运行状态", [para("点侧栏底部「运行状态」或输入 /actcompat status，从上往下找第一个不是「运行中 / 已连接」的组件，先处理它；后面的组件通常会跟着恢复。")]),
    card("第 2 步 · 重启对应的组件", [defs([
      ["完全没有战斗数据", "在「设置 · 解析器与诊断」点「重启解析器」。"],
      ["触发器、邮差、TTS 或银山雀儿不工作", "在运行状态里点「重启共享 Host」。"],
      ["抹茶不工作", "点「重启抹茶 Host」。"],
      ["自己安装的插件不工作", "点「重启通用 Host」。"],
      ["DACT 本体刚更新", "在卫月插件列表里重新加载 DACT；还不行再完整退出游戏和启动器。"],
    ], 226)]),
    card("常见情况", [defs([
      ["战斗统计没有数据", "确认侧栏底部显示「解析器运行中」，关掉「脱战隐藏」，再对木人造成几次伤害。只是打开窗口、进本或选中目标不会产生数据。"],
      ["某个触发器、TTS 或标点不工作", "扩展要同时满足三点：已启用、所需权限已开、扩展自己的配置里已启用这个功能。"],
      ["提示“重启 ACT”", "不用重启游戏，在运行状态里点「重启共享 Host」即可。"],
      ["通知没有出现", "游戏在前台时用游戏内通知，切到其他程序时用 Windows 通知；请检查 Windows 的专注助手设置。"],
      ["本地 DPS 和 FFLogs 不一样", "两边计算方式不同，小差异是正常的。差得很多时保留当场的 Network 日志一起反馈。"],
    ], 226)]),
    card("第 3 步 · 反馈问题", [
      bullets([
        "写清 DACT 版本、发生时间、当时在做什么、看到了什么，以及能不能重复出现。",
        "在「设置 · 数据与日志」点「复制诊断日志」，把完整内容一起发来。",
        "战斗数据不对时，再附上「打开 FFLogs 上传日志」（主页快捷入口）目录里当场的 Network 日志。发出前检查角色名和服务器名。",
      ]),
      row({ name: "按钮", w: "fill", gap: 8 }, [button("打开运行状态", "secondary", { icon: "pulse" }), button("打开诊断日志目录", "secondary", { icon: "folder" })]),
    ]),
  ], true);

  const macro = win("commands", [
    pageHeader("宏指令", "点「复制」即可放入剪贴板，再粘贴到游戏聊天框或宏中。"),
    card("常用入口", commands([
      ["/actcompat", "打开或关闭插件控制中心。"],
      ["/actcompat on", "始终打开插件控制中心。"],
      ["/actcompat off", "关闭插件控制中心。"],
      ["/actcompat meter", "打开战斗统计。"],
      ["/actcompat simple on|off", "开启或退出精简模式；即使其他界面已关闭，也可用 off 恢复。"],
      ["/actcompat history", "打开近期战斗。"],
      ["/actcompat logs", "打开已保存的日志文件列表。"],
      ["/actcompat status", "打开解析器与 Host 运行状态。"],
    ])),
    card("悬浮窗", commands([
      ["/actcompat cactbot", "打开当前选择的 Cactbot 悬浮窗。"],
      ["/actcompat overlay", "打开当前选择的 HTML 悬浮窗；可在后面追加模板名。"],
    ])),
    card("维护与诊断", commands([
      ["/actcompat clear", "立即清空当前战斗显示，不删除历史或原始日志。"],
      ["/actcompat install \"<DLL 或 ZIP 路径>\"", "静态预检并导入第三方 ACT 扩展；路径含空格时保留引号。"],
      ["/actcompat factory-reset", "打开控制中心；恢复出厂设置仍需在界面中确认。"],
      ["/actcompat host", "启动兼容 Host 并重启解析器，主要用于诊断。"],
      ["/actcompat stop", "停止解析器与共享兼容 Host，主要用于诊断。"],
      ["/actcompat sample", "载入本地样例战斗，仅用于开发与界面检查。"],
    ])),
  ], true);

  const copyright = win("copyright", [
    pageHeader("版权声明", "本页只声明 DACT 自身的版权与许可。"),
    card("DACT", [
      // The holder line matches LICENSE.md and the repository name, so the product rename
      // does not change it; the next paragraph links the old name for continuity.
      para("Copyright © 2026 DalamudActCompat contributors.", C.accent),
      para("DACT（原名 Dalamud ACT Compat）自有源代码以 GNU General Public License version 3（GPL-3.0）发布。你可以在许可证条款下运行、研究、修改与再分发本项目。"),
      para("本程序按现状提供，不附带任何明示或默示担保，包括适销性或特定用途适用性的担保。完整条款以项目随附的 LICENSE.md 为准。"),
      para("本声明不主张任何第三方插件、依赖、网页悬浮窗、游戏内容、商标或素材的版权。", C.muted),
      row({ name: "按钮", w: "fill", gap: 8 }, [
        button("查看项目源代码", "secondary", { icon: "external" }), button("查看 GPL-3.0 许可证", "secondary", { icon: "external" }),
      ]),
    ]),
  ]);

  // Search covers every section's title, summary and keywords; a result jumps to its card.
  const result = (page, title, summary) => card(title, [para(summary, C.muted)], {
    titleExtra: [pill(page, "off", { dot: false })], actions: [button("打开这一节", "ghost")],
  });
  const search = win(null, [
    pageHeader("搜索结果", "找到 3 条相关说明。点击结果可跳转到对应章节。", [button("清除搜索", "ghost")]),
    result("扩展与权限", "更新后还是旧版本", "重启共享、抹茶或通用 Host。"),
    result("遇到问题", "重启对应的组件", "解析器、共享、抹茶和通用 Host 各管什么。"),
    result("遇到问题", "常见情况", "没数据、触发器不工作、重启 ACT、通知和 FFLogs 差异。"),
  ], false, "重启");
  const empty = win(null, [
    pageHeader("搜索结果", "找到 0 条相关说明。点击结果可跳转到对应章节。", [button("清除搜索", "ghost")]),
    card("没有找到", [para("试试更短的关键词，例如“没数据”“权限”“重启”“悬浮窗”或命令里的单词。")]),
  ], false, "鲶鱼精邮差标点不同步");

  const note = "1100×760 独立窗口（最小 860×580）";
  const long = `${note}；高于 760 时内容区滚动`;
  return [
    { name: "帮助 · 使用须知", tree: notice, note: `${note}，首次打开显示这一页` },
    { name: "帮助 · 快速上手", tree: start, note: long },
    { name: "帮助 · 战斗统计", tree: meter, note: long },
    { name: "帮助 · 悬浮窗", tree: overlays, note: long },
    { name: "帮助 · 扩展与权限", tree: extensions, note: long },
    { name: "帮助 · 账号与云同步", tree: account, note: long },
    { name: "帮助 · 遇到问题", tree: fixes, note: long },
    { name: "帮助 · 宏指令", tree: macro, note: `${long}；/actcompat 命令相关入口直接打开这一页` },
    { name: "帮助 · 版权声明", tree: copyright, note },
    { name: "帮助 · 搜索结果", tree: search, note: "回车或点放大镜搜索；× 清除后回到原页面" },
    { name: "帮助 · 搜索无结果", tree: empty, note: "搜索没有命中时" },
  ];
};
