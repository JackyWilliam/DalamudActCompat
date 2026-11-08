// New in 1.0 (user decisions, 2026-11-07): AI 助手 (diagnose, review your own fights, Q&A),
// manual 反馈 next to the header ?, 全局搜索 (Ctrl+K), 新版导览 for people upgrading from 0.4,
// 战斗记录 as a control-center page with 开荒进度, and 场景方案 that switch meter/overlay sets
// by duty type. DACT runs no AI service (user decision): each player connects their own provider
// and API key, requests go straight from their PC, and DACT only ships prompts and job examples.
"use strict";
const {
  C, F, frame, row, col, spacer, vspacer, text, hint, icon, divider, button, iconButton, toggle, checkbox, select, input, slider,
  segmented, tabs, pill, card, field, toggleRow, stepsPanel, grid2,
} = require("./dsl");

const W = 920, H = 640;

module.exports = ({ shell, pageHeader, statTile, homeContent }) => {
  // ---- shared pieces
  const scrim = (x, y, w, h) => frame({ name: "遮罩", abs: true, x, y, w, h, bg: "rgba(4,6,9,0.62)" });
  const aiAvatar = () => row({ name: "AI", w: 26, h: 26, radius: 13, bg: C.accentSoft, justify: "center" }, [icon("sparkle", { size: 14, color: C.accent })]);
  const userBubble = message => row({ name: "我的消息", w: "fill", justify: "end" }, [
    col({ name: "气泡", w: "hug", pad: [8, 12], radius: 10, bg: C.primary }, [text(message)]),
  ]);
  const aiMessage = children => row({ name: "AI 消息", w: "fill", gap: 10, align: "start" }, [
    aiAvatar(), col({ name: "内容", w: "fill", gap: 8 }, children),
  ]);
  // One finding: severity, what was found (with evidence), and the fix as a button the user clicks.
  const finding = (tone, label, title, detail, action) => row({ name: `发现/${title}`, w: "fill", pad: [10, 12], gap: 10, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
    pill(label, tone, { dot: false }),
    col({ name: "说明", w: "fill", gap: 2 }, [text(title, { w: "fill" }), hint(detail)]),
    action ? button(action, "secondary") : null,
  ]);
  const aiTabs = active => segmented(["诊断问题", "战斗复盘", "使用问答"], active);
  // FFLogs parse colours (FflogsEstimateService.ColorForPercentile); parse numbers are bold.
  const parseColor = p => p >= 100 ? "#E5CC80" : p >= 99 ? "#E268A8" : p >= 95 ? "#FF8000" : p >= 75 ? "#A335EE" : p >= 50 ? "#0070FF" : p >= 25 ? "#1EFF00" : "#666666";
  const aiHeader = () => pageHeader("AI 助手", "诊断问题、复盘你自己的战斗、回答使用问题。使用你自己的 AI 服务和 API Key，发出前会去掉角色名和服务器名。", [
    button("AI 设置", "secondary", { icon: "gear" }), button("新对话", "secondary", { icon: "plus" }),
  ]);

  // ---------------------------------------------------------------- AI 助手 · 诊断
  const aiDiagnose = shell("ai", [
    aiHeader(),
    aiTabs(0),
    col({ name: "对话（独立滚动）", w: "fill", h: "fill", pad: 14, gap: 14, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 }, clip: true }, [
      userBubble("触发器突然不播报了，邮差标点也没反应。"),
      aiMessage([
        row({ name: "已读取", w: "fill", gap: 6 }, [
          icon("eye", { size: 13 }), hint("已读取：运行状态、扩展权限、最近 200 行诊断日志（角色名已去除）", { w: "fill" }), button("查看内容", "ghost"),
        ]),
        text("找到 2 处问题，按顺序处理：", { w: "fill" }),
        finding("error", "错误", "共享 Host 已停止", "12:41 退出（代码 -1），触发器、邮差和 TTS 都跑在它里面。", "重启共享 Host"),
        finding("warn", "提醒", "鲶鱼精邮差没有「发送游戏指令」权限", "标点和地标需要这项权限；上次更新扩展后被重置。", "去授权"),
      ]),
      userBubble("重启了，还是不行"),
      aiMessage([
        text("Host 已正常运行，但日志里触发器每次都报「找不到语音引擎」。这个情况我没法在本机修好，建议把日志和这次对话发给开发者。", { w: "fill" }),
        row({ name: "操作", gap: 8 }, [button("发给开发者", "primary", { icon: "send" }), button("再试一次", "secondary")]),
      ]),
    ]),
    row({ name: "输入", w: "fill", gap: 8 }, [input("描述你遇到的问题，或直接点「开始诊断」", { placeholder: true }), button("开始诊断", "primary", { icon: "sparkle" })]),
  ]);

  // ---------------------------------------------------------------- AI 助手 · 战斗复盘
  const advice = (n, title, detail) => row({ name: `建议/${n}`, w: "fill", gap: 10, align: "start" }, [
    row({ name: "序号", w: 20, h: 20, radius: 10, bg: C.accentSoft, justify: "center" }, [text(String(n), { size: F.tiny, color: C.accent })]),
    col({ name: "内容", w: "fill", gap: 2 }, [text(title, { w: "fill" }), hint(detail)]),
  ]);
  const aiReview = shell("ai", [
    aiHeader(),
    aiTabs(1),
    row({ name: "选择战斗", w: "fill", gap: 8 }, [
      select("光暗未来绝境战 · 第 32 次 · 03:12 · P2 团灭"), button("开始复盘", "primary", { icon: "sparkle" }),
    ]),
    row({ name: "指标", w: "fill", gap: 10, align: "stretch" }, [
      // Only what DACT records per fight (no cast-level data): rate, crit/DH, deaths, parse.
      statTile("meter", "rDPS", "19,860", "ok", "DPS 18,940 · 全队第 3"),
      statTile("target", "暴击 / 直击", "24.6% / 31.2%", "plain", "暴击直击 8.4%"),
      statTile("shield", "死亡", "1 次", "warn", "全队首个死亡 02:41"),
      statTile("pulse", "预估 Parse", "62", "plain", "本次 rDPS 19,860", { color: parseColor(62), bold: true }),
    ]),
    card("AI 建议", [
      advice(1, "把爆发药挪到第二次团辅前 5 秒", "这次在团辅后 14 秒才用，药效只覆盖了一半团辅窗口。"),
      advice(2, "转阶段后先打预读技能再进循环", "P1→P2 与 P2→P3 转场后各空转约 2.5 秒，合计损失约 1.1% 伤害。"),
      advice(3, "P2 点名分散时向场边多走一步", "两次被同伴的范围技能擦到，第二次后马上死亡，导致这次团灭。"),
      divider(),
      row({ name: "依据", w: "fill", gap: 8 }, [
        hint("只分析你自己的数据；职业建议参考当前版本的通用规律，仅供参考。", { w: "fill" }), button("在战斗记录里查看", "ghost"),
      ]),
    ], { h: "fill", actions: [button("复制建议", "ghost")] }),
  ]);

  // ---------------------------------------------------------------- AI 助手 · 使用问答
  // Answers come from the help pages and the player's own settings; every answer cites its source
  // and offers a jump to the exact setting instead of describing the path only.
  const chip = label => row({ name: `推荐问题/${label}`, h: 28, pad: [0, 12], radius: 14, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [text(label, { size: F.small, color: C.accent })]);
  const aiQA = shell("ai", [
    aiHeader(),
    aiTabs(2),
    col({ name: "对话（独立滚动）", w: "fill", h: "fill", pad: 14, gap: 14, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 }, clip: true }, [
      userBubble("战斗统计怎么只显示我自己？"),
      aiMessage([
        text("在「战斗统计」点「自定义…」，打开窗口行为里的「只显示自己」。这只影响统计窗显示，不会改变解析和日志；关掉后马上恢复显示全队。", { w: "fill" }),
        row({ name: "操作", gap: 8 }, [button("打开自定义布局", "secondary", { icon: "external" }), button("在帮助里查看", "ghost")]),
        row({ name: "依据", w: "fill", gap: 6 }, [icon("help", { size: 13 }), hint("依据：帮助 · 战斗统计 ·「打开与摆放」、你当前的战斗统计设置", { w: "fill" })]),
      ]),
      userBubble("那 rDPS 和 DPS 有什么区别？"),
      aiMessage([
        text("DPS 按你自己的出手时间计算；rDPS 去掉别人给你的团辅、加回你给队友的团辅，更接近 FFLogs 的口径。DACT 的 rDPS 是本地实时估算，和上传后的 FFLogs 会有小差异。", { w: "fill" }),
        row({ name: "依据", w: "fill", gap: 6 }, [icon("help", { size: 13 }), hint("依据：帮助 · 战斗统计 ·「数值是怎么算的」", { w: "fill" })]),
      ]),
    ]),
    row({ name: "推荐问题", w: "fill", gap: 8 }, [chip("FFLogs 预估怎么开？"), chip("触发器怎么导入？"), chip("为什么统计没有数据？")]),
    row({ name: "输入", w: "fill", gap: 8 }, [input("问一个关于 DACT 的问题", { placeholder: true }), button("发送", "primary", { icon: "send" })]),
  ]);

  // ---------------------------------------------------------------- AI 助手 · AI 设置
  // A page of its own under AI 助手 (user decision): reached from「AI 设置」or「开启并设置」on the
  // AI page and returns there. Off by default; the first switch-on lands here with the key guide.
  // While off, the sidebar item stays (it shows the 未开启 page) but in-place AI entries are hidden.
  const aiSettings = shell("ai", [
    pageHeader("AI 设置", "AI 助手 / AI 设置 · 默认关闭；开启后使用你自己的 AI 服务和 API Key。", [button("返回 AI 助手", "secondary")]),
    row({ name: "AI 设置", w: "fill", gap: 14, align: "start" }, [
      col({ name: "左栏", w: "fill", gap: 14 }, [
        card("开关", [toggleRow("启用 AI 助手", true, "关闭后不读取任何日志、不发出请求，各页面里的「让 AI 诊断」「AI 复盘」入口也会隐藏；已保存的 Key 仍留在本机。")]),
        card("连接你的 AI 服务", [
          hint("DACT 不提供 AI 接口，也不经手你的 Key：请求从你的电脑直接发到你选择的服务商，费用由服务商按你的账户计算。"),
          grid2([field("服务商", select("Anthropic Claude"))], [field("模型", select("推荐（自动选择）"))], { gap: 12 }),
          field("API Key", input("粘贴你的 API Key（sk-ant-…）", { placeholder: true, trailing: "eye" }), "用 Windows 账户加密保存在本机，每个服务商分开保存；不上传到 DACT 服务器，也不进云同步。"),
          field("接口地址", input("默认 https://api.anthropic.com", { placeholder: true }), "留空即用服务商的官方地址；使用中转或代理时填写它的地址。"),
          row({ name: "测试", w: "fill", gap: 8 }, [button("测试连接", "primary", { icon: "refresh" }), pill("还没有测试", "off")]),
          hint("测试通过后，侧栏和各页面才会出现 AI 入口。"),
        ]),
        card("发给 AI 的内容", [
          toggleRow("去掉角色名和服务器名", true, "默认开启，建议保留。"),
          toggleRow("诊断时附带最近 200 行诊断日志", true),
          hint("复盘时还会附带：这场战斗你的技能记录（从本机战斗日志读取，只有你自己的技能和队伍团辅时间）和你所玩职业的要点（参考 xivanalysis 警察网的检查规则）。"),
          toggleRow("每次发送前先让我确认", false),
        ]),
      ]),
      card("怎么获取 API Key", [
        stepsPanel([
          "打开 Anthropic Claude 的控制台，注册并登录。",
          "创建一个新的 API Key 并复制（通常只显示一次）。",
          "充值或开通计费，建议同时设置用量上限。",
          "回到左边粘贴到「API Key」，点「测试连接」；模型列表会从服务商实时获取。",
        ]),
        button("打开 Anthropic Claude 控制台", "secondary", { icon: "external", w: "fill" }),
        divider(),
        text("常见问题", { size: F.small, color: C.muted }),
        hint("测试失败：多半是 Key 复制不完整，或账户余额为 0。"),
        hint("想换服务商：选另一个服务商再测试连接；每个服务商的 Key 分开保存，切回来不用重新粘贴。"),
      ], { w: 300 }),
    ]),
  ], true);

  // ---------------------------------------------------------------- AI 助手 · 未开启（默认）
  const feature = (iconName, title, sub) => row({ name: `功能/${title}`, w: "fill", gap: 12, pad: [10, 12], radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
    row({ name: "图标", w: 32, h: 32, radius: 8, bg: C.accentSoft, justify: "center" }, [icon(iconName, { color: C.accent })]),
    col({ name: "说明", w: "fill", gap: 2 }, [text(title), hint(sub)]),
  ]);
  const aiOff = shell("ai", [
    pageHeader("AI 助手", "诊断问题、复盘你自己的战斗、回答使用问题。", [button("AI 设置", "secondary", { icon: "gear" })]),
    card("AI 助手还没有开启", [
      hint("DACT 不提供 AI 接口：开启后填入你自己的 API Key，请求从你的电脑直接发到你选择的服务商，Key 只保存在本机。"),
      feature("wrench", "诊断问题", "读取运行状态、扩展权限和诊断日志，找出问题并给出能直接点的修复按钮。"),
      feature("meter", "战斗复盘", "只分析你自己的战斗：输出、暴击直击、死亡，以及开荒打到哪里、在哪里团灭。"),
      feature("help", "使用问答", "根据帮助文档和你当前的设置回答，并能直接跳到对应设置。"),
      row({ name: "开启", w: "fill", gap: 10 }, [button("开启并设置", "primary", { icon: "sparkle" }), hint("第一次开启会一步步带你拿到 API Key。", { w: "fill" })]),
    ], { h: "fill" }),
  ]);

  // ---------------------------------------------------------------- 好友 · 来信提醒
  // Its own page under 好友 (user decision), opened by「来信提醒设置」and returning to 好友;
  // the 设置 page no longer carries a copy.
  const notify = shell("friends", [
    pageHeader("来信提醒", "好友 / 来信提醒 · 收到好友消息时怎么提醒你。", [button("返回好友", "secondary")]),
    row({ name: "提醒", w: "fill", gap: 14, align: "start" }, [
      card("来信气泡", [
        toggleRow("显示来信气泡", true, "好友发来消息时，在屏幕边上弹出一个小气泡。"),
        field("气泡位置", select("左侧")),
        field("气泡背景透明度", slider(0.25, "25%", { trackWidth: 200 }), "0% 为不透明，100% 为全透明；文字和按钮保持清晰。"),
      ]),
      card("提示音", [
        toggleRow("播放新消息提示音", true),
        field("提示音", row({ name: "提示音", w: "fill", gap: 8 }, [select("提示音 1"), button("试听", "secondary")])),
        hint("关闭只停止提示，好友消息和未读数照常接收。"),
      ]),
    ]),
  ]);

  // ---------------------------------------------------------------- 反馈（手动 / AI 转交）
  const textarea = (value, h) => col({ name: "多行输入", w: "fill", h, pad: [8, 10], radius: 6, bg: C.field, border: { c: C.accent, w: 1 } }, [text(value, { w: "fill" })]);
  const attach = (on, label, note, action) => row({ name: `附件/${label}`, w: "fill", gap: 8 }, [
    checkbox(on), text(label), hint(note, { w: "fill" }), action ? button(action, "ghost") : null,
  ]);
  const feedbackDialog = col({
    name: "反馈弹窗", w: 500, pad: 18, gap: 12, radius: 12, bg: C.raised, border: { c: C.border, w: 1 }, shadow: true, abs: true, x: 210, y: 96,  // centred: (640 - 449) / 2
  }, [
    row({ name: "标题", w: "fill", gap: 8 }, [icon("feedback", { color: C.gold }), text("反馈问题或建议", { size: F.h2 }), spacer(), iconButton("close")]),
    hint("开发者会在 好友 ·「官方通知」里回复你。"),
    segmented(["问题", "建议", "其他"], 0, { w: "fill" }),
    field("描述", textarea("触发器不播报、邮差标点没反应。AI 诊断：共享 Host 重启后恢复运行，但触发器每次报「找不到语音引擎」，本机无法修复。", 96), "AI 已经帮你填好，可以修改后再发送。"),
    col({ name: "附件", w: "fill", gap: 6 }, [
      attach(true, "附上诊断日志", "约 180 KB，角色名和服务器名已去除", "预览"),
      attach(true, "附上这次 AI 对话", "4 条消息"),
      row({ name: "截图", w: "fill", gap: 8 }, [button("添加截图", "secondary", { icon: "plus" }), hint("最多 3 张，可直接粘贴", { w: "fill" })]),
    ]),
    divider(),
    row({ name: "底栏", w: "fill", gap: 8 }, [hint("发送后可在 设置 · 解析器与诊断 里查看处理进度。", { w: "fill" }), button("取消", "secondary"), button("发送", "primary", { icon: "send" })]),
  ]);
  const feedback = shell("ai", [aiHeader(), aiTabs(0)], false, [scrim(0, 0, W, H), feedbackDialog]);

  // ---------------------------------------------------------------- 全局搜索（Ctrl+K）
  const result = (iconName, title, where, selected, trailing) => row({
    name: `结果/${title}`, w: "fill", h: 44, pad: [0, 12], gap: 12, radius: 8, bg: selected ? C.accentSoft : null,
  }, [
    icon(iconName, { color: selected ? C.accent : C.muted }),
    col({ name: "文字", w: "fill", gap: 1 }, [text(title, { color: selected ? C.accent : C.text }), text(where, { size: F.small, color: C.muted })]),
    trailing || null,
  ]);
  const group = label => text(label, { size: F.small, color: C.muted, w: "fill" });
  const palette = col({
    name: "全局搜索", w: 560, pad: 8, gap: 4, radius: 12, bg: C.raised, border: { c: C.border, w: 1 }, shadow: true, abs: true, x: 180, y: 58,
  }, [
    row({ name: "输入", w: "fill", h: 40, pad: [0, 10], gap: 10, radius: 8, bg: C.field, border: { c: C.accent, w: 1 } }, [
      icon("search", { color: C.accent }), text("fflogs", { w: "fill" }), pill("Esc", "off", { dot: false }),
    ]),
    col({ name: "结果", w: "fill", pad: [6, 0, 0, 0], gap: 2 }, [
      row({ name: "分组", w: "fill", pad: [4, 12] }, [group("设置项")]),
      result("meter", "启用 FFLogs 在线估算", "战斗统计 · FFLogs 卡片", true, toggle(false)),
      result("meter", "FFLogs Client ID / Client Secret", "战斗统计 · FFLogs 卡片", false),
      result("folder", "FFLogs 上传日志目录", "设置 · 数据与日志", false),
      row({ name: "分组", w: "fill", pad: [8, 12, 4, 12] }, [group("帮助")]),
      result("help", "FFLogs DPS Parse 预估", "帮助 · 战斗统计", false),
    ]),
    divider(),
    row({ name: "按键", w: "fill", pad: [4, 12], gap: 16 }, [hint("↑ ↓ 选择", { w: "hug" }), hint("Enter 跳到该位置并高亮", { w: "hug" }), hint("开关可直接切换", { w: "hug" })]),
  ]);
  const search = shell("home", homeContent(), false, [scrim(0, 0, W, H), palette]);

  // ---------------------------------------------------------------- 新版导览（升级到 1.0 后第一次打开）
  // Spotlight on the sidebar 好友 item: four scrim pieces leave a hole around it.
  const SX = 6, SY = 241 - 4, SW = 164, SH = 42;
  const tour = shell("home", homeContent(), false, [
    scrim(0, 0, W, SY), scrim(0, SY + SH, W, H - SY - SH), scrim(0, SY, SX, SH), scrim(SX + SW, SY, W - SX - SW, SH),
    frame({ name: "高亮框", abs: true, x: SX, y: SY, w: SW, h: SH, radius: 9, border: { c: C.accent, w: 2 } }),
    col({ name: "导览卡片", w: 320, pad: 16, gap: 10, radius: 12, bg: C.raised, border: { c: C.accent, w: 1 }, shadow: true, abs: true, x: 186, y: 206 }, [
      row({ name: "进度", w: "fill", gap: 8 }, [text("1.0 新版导览", { size: F.small, color: C.accent }), spacer(), text("3 / 7", { size: F.small, color: C.muted })]),
      text("好友搬到了侧栏", { size: F.h2 }),
      text("以前在顶栏右上角，现在和其他页面放在一起；有未读消息时右边会显示数字。", { w: "fill" }),
      row({ name: "进度点", gap: 5 }, [0, 1, 2, 3, 4, 5, 6].map(i => frame({ name: "点", w: i === 2 ? 16 : 6, h: 6, radius: 3, bg: i === 2 ? C.accent : C.hover }))),
      row({ name: "按钮", w: "fill", gap: 8 }, [button("跳过导览", "ghost"), spacer(), button("上一步", "secondary"), button("下一步", "primary")]),
    ]),
  ]);

  // ---------------------------------------------------------------- 战斗记录（原独立窗口改为页面）
  const recordTabs = active => tabs(["近期战斗", "开荒进度", "日志文件"], active);
  const recordHeader = () => pageHeader("战斗记录", "近期战斗、开荒进度和原始日志；完成一场战斗后自动保存。", [
    button("打开日志文件夹", "secondary", { icon: "folder" }),
  ]);
  const fight = (name, sub, tone, selected) => col({ name: `战斗/${name}`, w: "fill", pad: [8, 10], gap: 3, radius: 8, bg: selected ? C.hover : null, border: selected ? { c: "rgba(107,199,245,0.55)", w: 1 } : null }, [
    row({ name: "标题", w: "fill", gap: 6 }, [text(name, { w: "fill" }), tone ? pill(tone[0], tone[1], { dot: false }) : null]),
    text(sub, { size: F.small, color: C.muted, w: "fill" }),
  ]);
  const member = (job, color, name, value, share, self) => row({ name: `成员/${name}`, w: "fill", h: 30, pad: [0, 10], gap: 10, radius: 6, bg: self ? "rgba(199,168,92,0.12)" : C.field }, [
    row({ name: "职业", w: 40, h: 20, radius: 4, bg: color, justify: "center" }, [text(job, { size: F.tiny })]),
    text(name, { w: "fill", color: self ? C.gold : C.text }), text(share, { size: F.small, color: C.muted, w: 44, align: "right" }),
    text(value, { color: C.accent, w: 64, align: "right" }),
  ]);
  const recent = shell("history", [
    recordHeader(),
    recordTabs(0),
    row({ name: "记录", w: "fill", h: "fill", gap: 14, align: "stretch" }, [
      col({ name: "战斗列表（独立滚动）", w: 236, h: "fill", pad: 8, gap: 2, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 }, clip: true }, [
        fight("光暗未来绝境战", "今天 21:42 · 03:12 · 死亡 3", ["团灭", "error"], true),
        fight("光暗未来绝境战", "今天 21:36 · 05:48 · 死亡 5", ["团灭", "error"]),
        fight("阿卡狄亚零式 · 4", "今天 20:58 · 09:31 · 死亡 0", ["通关", "ok"]),
        fight("木人", "今天 20:40 · 01:05", null),
        fight("阿卡狄亚零式 · 3", "昨天 22:10 · 08:02 · 死亡 1", ["通关", "ok"]),
      ]),
      col({ name: "详情", w: "fill", h: "fill", gap: 12 }, [
        row({ name: "标题", w: "fill", gap: 8 }, [
          col({ name: "战斗", w: "fill", gap: 2 }, [text("光暗未来绝境战 · 第 32 次", { size: F.h2 }), hint("今天 21:42 · 03:12 · 打到 P2 中段")]),
          button("AI 复盘", "primary", { icon: "sparkle" }),
        ]),
        row({ name: "指标", w: "fill", gap: 10, align: "stretch" }, [
          statTile("users", "队伍人数", "8", "plain", "全员在场"), statTile("meter", "总伤害", "31.6m", "plain", "队伍 DPS 164.6k"),
          statTile("shield", "死亡", "3", "warn", "首个死亡 02:51"),
        ]),
        card("队伍表现", [
          ...[["SAM", "#C2452E", "自己", "21,480", "13.1%", true], ["BLM", "#7B5BB8", "队友 01", "21,020", "12.8%"], ["DRG", "#3E5FBF", "队友 02", "20,310", "12.3%"],
            ["DNC", "#C7A85C", "队友 03", "17,450", "10.6%"], ["WAR", "#B33A3A", "队友 04", "11,880", "7.2%"]].map(m => member(...m)),
        ], { h: "fill", actions: [segmented(["rDPS", "HPS"], 0)] }),
        row({ name: "折叠", w: "fill" }, [icon("chevronRight", { size: 14, color: C.accent }), text(" 技能统计（来源、技能、次数、伤害、治疗）", { color: C.accent })]),
      ]),
    ]),
  ]);

  // ---------------------------------------------------------------- 战斗记录 · 开荒进度
  // One bar per pull: height = how far it got (phase + fraction), colour = phase.
  const PHASE = ["#3A4552", "#2F6C8F", "#3F9CCB", "#6BC7F5", "#C7A85C"];
  const PULLS = [0.4, 0.7, 1.2, 0.9, 1.5, 1.6, 1.3, 1.8, 1.7, 2.1, 1.9, 2.3, 1.4, 2.2, 2.4, 2.6, 2.1, 2.5, 2.8, 2.3, 2.9, 3.1, 2.7, 3.2, 3.4, 2.6, 3.3, 3.5, 3.68, 3.1, 3.2, 1.5];
  // The best attempt is a solid gold bar (user edit in Figma, 2026-11-07), not an outline.
  const chart = row({ name: "进度图", w: "fill", h: 104, gap: 4, align: "end" }, PULLS.map((p, i) =>
    frame({ name: `第 ${i + 1} 次`, w: 8, h: Math.round(p / 4 * 100) + 4, radius: 2, bg: i === 28 ? C.gold : PHASE[Math.min(4, Math.floor(p))] })));
  const axis = col({ name: "阶段", w: 22, h: 104, justify: "between" }, ["P4", "P3", "P2", "P1"].map(p => text(p, { size: F.tiny, color: C.muted })));
  const wipe = (phase, label, n, frac) => row({ name: `团灭/${label}`, w: "fill", gap: 10 }, [
    pill(phase, "off", { dot: false }), text(label, { w: 128 }),
    frame({ name: "比例", w: "fill", h: 6, radius: 3, bg: C.hover, layout: "free" }, [frame({ name: "值", x: 0, y: 0, w: Math.round(170 * frac), h: 6, radius: 3, bg: C.accent })]),
    text(`${n} 次`, { size: F.small, color: C.muted, w: 36, align: "right" }),
  ]);
  const progress = shell("history", [
    recordHeader(),
    recordTabs(1),
    row({ name: "开荒", w: "fill", h: "fill", gap: 14, align: "stretch" }, [
      col({ name: "副本列表", w: 236, h: "fill", pad: 8, gap: 2, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
        fight("光暗未来绝境战", "32 次 · 最远 P4 68%", ["开荒中", "warn"], true),
        fight("阿卡狄亚零式 · 4", "11 次 · 已通关", ["已通关", "ok"]),
        fight("阿卡狄亚零式 · 3", "6 次 · 已通关", ["已通关", "ok"]),
      ]),
      col({ name: "进度", w: "fill", h: "fill", gap: 12 }, [
        row({ name: "指标", w: "fill", gap: 10, align: "stretch" }, [
          statTile("history", "尝试", "32 次", "plain", "本周 +11"), statTile("target", "最远", "P4 · 68%", "ok", "第 29 次"),
          statTile("pulse", "最近 10 次平均", "P3 · 20%", "plain", "比上周推进 1 个阶段"),
        ]),
        card("每次尝试打到哪里", [row({ name: "图", w: "fill", gap: 8, align: "end" }, [axis, chart]), hint("金色那一条是最好的一次；点任意一根柱子查看那次战斗。")]),
        card("最常见的团灭点", [
          wipe("P2", "中段点名分散", 9, 0.9), wipe("P3", "转场后第一轮分摊", 6, 0.6), wipe("P1", "开场连续击退", 4, 0.4),
        ], { h: "fill", actions: [button("让 AI 总结开荒", "secondary", { icon: "sparkle" })] }),
      ]),
    ]),
  ]);

  // ---------------------------------------------------------------- 设置 · 场景方案
  // Which overlays a scene opens or closes (user question, 2026-11-07). The field looks like the
  // 战斗统计 select beside it and opens an anchored popover (not a centred dialog) listing 我的悬浮窗,
  // each set to 打开 / 关闭 / 不变. 不变 is the default so an overlay added later is never closed by a
  // scene switch; the field shows only the overlays a scene changes (blue = opens, grey = closes).
  const overlayField = (opens, closes, open) => row({
    name: open ? "悬浮窗选择/展开" : "悬浮窗选择", w: "fill", h: 30, radius: 6, pad: [0, 10, 0, opens.length || closes.length ? 4 : 10], gap: 6, bg: C.field,
    border: { c: open ? C.accent : C.fieldBorder, w: 1 },
  }, [
    ...opens.map(o => pill(o, "ok")), ...closes.map(o => pill(o, "off")),
    opens.length || closes.length ? null : text("不改动", { color: C.muted, w: "hug" }), spacer(),
    icon("chevronDown", { size: 14, color: open ? C.accent : C.muted }),
  ]);
  const scene = (name, sub, meter, opens, closes, on, locked, open) => row({ name: `场景/${name}`, w: "fill", pad: [8, 12], gap: 12, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
    col({ name: "场景", w: 200, gap: 2 }, [text(name), hint(sub)]),
    col({ name: "战斗统计", w: 150, gap: 4 }, [text("战斗统计", { size: F.small, color: C.muted }), select(meter, { disabled: locked })]),
    col({ name: "悬浮窗", w: "fill", gap: 4 }, [text("悬浮窗", { size: F.small, color: C.muted }), overlayField(opens, closes, open)]),
    toggle(on),
  ]);
  const overlayChoice = (name, source, active) => row({ name: `悬浮窗/${name}`, w: "fill", gap: 10 }, [
    col({ name: "名称", w: "fill", gap: 1 }, [text(name), text(source, { size: F.small, color: C.muted })]),
    segmented(["打开", "关闭", "不变"], active),
  ]);
  // Anchored under the 普通副本与日随 field (x 600–825, bottom 339), right edges aligned so it stays
  // inside the 920 px window.
  const overlayPopover = col({
    name: "悬浮窗浮层", w: 340, pad: 12, gap: 10, radius: 10, bg: C.raised, border: { c: C.border, w: 1 }, shadow: true, abs: true, x: 485, y: 343,
  }, [
    text("进入「普通副本与日随」时"),
    overlayChoice("文字提醒", "Cactbot", 0),
    overlayChoice("时间轴", "Cactbot", 1),
    divider(),
    hint("选「不变」就保持原样；新加的悬浮窗默认「不变」。"),
    row({ name: "底部", w: "fill" }, [button("管理我的悬浮窗", "ghost"), spacer()]),
  ]);
  const sceneCard = open => card("场景", [
    hint("当前：高难副本（光暗未来绝境战）。"),
    scene("高难副本", "零式、绝境、极神", "经典榜", ["文字提醒", "时间轴"], [], true),
    scene("普通副本与日随", "迷宫、讨伐、团队任务", "横版", ["文字提醒"], ["时间轴"], true, false, open),
    scene("野外与主城", "不在副本里时", "隐藏", [], ["文字提醒", "时间轴"], true),
    scene("PvP", "水晶冲突、纷争前线等", "隐藏（固定）", [], [], true, true),
    hint("悬浮窗一栏：蓝色是进入时打开，灰色是进入时关闭，没列出的保持不变。点击可修改。"),
    hint("PvP 中始终隐藏战斗统计，不提供 PvP 信息优势（见 使用须知）。"),
  ], { h: "fill", actions: [text("按内容类型自动切换", { size: F.small, color: C.muted }), toggle(true)] });
  const scenes = shell("settings", [
    pageHeader("场景方案", "设置 / 场景方案 · 进入不同类型的内容时，自动换成对应的战斗统计和悬浮窗。", [button("返回设置", "secondary")]),
    sceneCard(false),
  ]);
  const scenesPick = shell("settings", [
    pageHeader("场景方案", "设置 / 场景方案 · 进入不同类型的内容时，自动换成对应的战斗统计和悬浮窗。", [button("返回设置", "secondary")]),
    sceneCard(true),
  ], false, [overlayPopover]);

  // ---------------------------------------------------------------- 顶栏 · 云同步状态
  // The header chip opens this anchored popover (right edge under the chip), not a dialog:
  // status, account, backups, the auto-sync switch, then refresh / open 账号与云同步.
  const infoRow = (label, value, color) => row({ name: `信息/${label}`, w: "fill", gap: 8 }, [
    text(label, { size: F.small, color: C.muted, w: 72 }), text(value, { color: color || C.text, w: "fill" }),
  ]);
  const cloudPopover = col({
    name: "云同步状态浮层", w: 320, pad: 12, gap: 10, radius: 10, bg: C.raised, border: { c: C.border, w: 1 }, shadow: true, abs: true, x: 476, y: 44,
  }, [
    row({ name: "标题", w: "fill", gap: 8 }, [text("云同步状态", { color: C.gold }), spacer(), pill("等待首次同步", "warn")]),
    hint("登录后还没有上传过配置。打开游戏时会自动检查一次。"),
    infoRow("账号", "Raynor"), infoRow("最近备份", "暂无", C.muted), infoRow("云端版本", "0 / 2"),
    divider(),
    toggleRow("打开游戏后自动同步配置", true, "每次打开游戏并登录后检查一次；配置没变化就不上传，退出游戏不会等待网络。"),
    row({ name: "按钮", w: "fill", gap: 8 }, [button("刷新状态", "secondary", { icon: "refresh" }), button("打开云同步", "primary")]),
  ]);
  const cloudStatus = shell("home", homeContent(), false, [cloudPopover]);

  const note = "920×640 控制中心";
  return [
    { name: "AI 助手 · 诊断", tree: aiDiagnose, note: `${note}；对话区独立滚动，结论里的按钮直接执行修复` },
    { name: "AI 助手 · 战斗复盘", tree: aiReview, note: `${note}；只分析自己的数据；职业示例随 DACT 更新` },
    { name: "AI 助手 · 使用问答", tree: aiQA, note: `${note}；答案注明依据，并能直接跳到对应设置` },
    { name: "AI 助手 · AI 设置", tree: aiSettings, note: "从 AI 助手页「AI 设置」或「开启并设置」进入，「返回 AI 助手」回去；默认关闭，Key 只存本机" },
    { name: "反馈（AI 转交）", tree: feedback, note: "顶栏反馈按钮打开同一个弹窗；AI 转交时预填描述和附件" },
    { name: "全局搜索（Ctrl+K）", tree: search, note: "顶栏搜索框或 Ctrl+K；设置项、页面和帮助一起搜" },
    { name: "新版导览", tree: tour, note: "从 0.4 升级后第一次打开时显示 7 步；帮助里可重看" },
    { name: "战斗记录 · 近期战斗", tree: recent, note: `${note}；原「战斗记录」独立窗口改为页面` },
    { name: "战斗记录 · 开荒进度", tree: progress, note: `${note}；按副本归档每次尝试打到的阶段` },
    { name: "设置 · 场景方案", tree: scenes, note: "PvP 场景固定隐藏战斗统计；入口在 设置 ·「场景方案」" },
    { name: "AI 助手 · 未开启", tree: aiOff, note: "默认状态；「开启并设置」进入 AI 设置" },
    { name: "好友 · 来信提醒", tree: notify, note: "好友页「来信提醒设置」进入，「返回好友」回去；设置页不再放这一块" },
    { name: "设置 · 场景方案 · 选择悬浮窗", tree: scenesPick, note: "点「悬浮窗」字段弹出锚定浮层（不是居中弹窗）；每个悬浮窗三选一，默认「不变」" },
    { name: "顶栏 · 云同步状态", tree: cloudStatus, note: "点顶栏云同步胶囊弹出的锚定浮层（右对齐胶囊）；Esc 或点空白处关闭" },
  ];
};
