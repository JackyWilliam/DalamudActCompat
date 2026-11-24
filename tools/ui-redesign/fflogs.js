// The FFLogs page: a sidebar item of its own that holds the Parse estimate (moved out of 战斗统计)
// and log upload (sign-in, upload latest / selected fights / live). Upload options sit on a
// sub-page, like AI 设置 under AI 助手. The last boards show 战斗统计 without the FFLogs card and
// the per-pull upload button in 战斗记录.
"use strict";
const {
  C, F, frame, row, col, spacer, text, hint, icon, divider,
  button, toggle, checkbox, select, disclosure, input, slider, pill, card, field, toggleRow, grid2,
} = require("./dsl");

module.exports = ({ shell, pageHeader }) => {
  const header = () => pageHeader("FFLogs", "Parse 预估和日志上传。预估用 API Client，上传用 FFLogs 网站账号，两者互不依赖。");

  // ---------------------------------------------------------------- Parse 预估（原战斗统计页右栏）
  // Same controls and order as the card had on 战斗统计; only its home changed.
  // The expanded 如何创建 FFLogs API Client guide is unchanged (06 交互状态), so it is not redrawn.
  const estimateCard = enabled => card("Parse 预估", [
    hint("用本场实际 DPS 对照 FFLogs 同职业、同副本、同分区的分布估算 Parse；缓存缺失时显示 “--”。"),
    row({ name: "元数据", w: "fill", gap: 6 }, [pill("区域 CN · 分区 9 · 指标 DPS", "off", { dot: false })]),
    toggleRow("启用 FFLogs 在线估算", enabled),
    disclosure("如何创建 FFLogs API Client", false),
    field("Client ID", input(enabled ? "9f3c1a52-7b0e-4d6a-b1c4-2e8d5a70c913" : "粘贴 Client ID", { placeholder: !enabled })),
    field("Client Secret", input(enabled ? "••••••••••••••••" : "粘贴 Client Secret", { placeholder: !enabled, trailing: "eye" })),
    row({ name: "凭据按钮", w: "fill", gap: 8 }, [
      button("测试并刷新", "primary"), button("创建 / 管理 API Client", "secondary", { icon: "external" }),
    ]),
    hint("密钥只保存在本机插件配置中，可随时撤销；不会上传玩家 ID。"),
    divider(),
    row({ name: "副本识别", w: "fill", gap: 8, align: "start" }, [
      icon("target", { size: 14 }),
      col({ name: "说明", w: "fill", gap: 2 }, [
        text("当前副本自动识别", { size: F.small, color: C.muted }),
        hint(enabled
          ? "Territory 1263 · 阿卡狄亚零式 · 4 · FFLogs #104 · Savage。榜单区域跟随主页的游戏区域。"
          : "当前不在最新团队副本榜单中，不会加载历史榜单。榜单区域跟随主页的游戏区域。"),
      ]),
    ]),
  ], { titleExtra: [enabled ? pill("已就绪", "ok") : pill("未启用", "off")] });

  // ---------------------------------------------------------------- 日志上传
  const inset = (name, children, props = {}) => col({
    name, w: "fill", pad: [10, 12], gap: 8, radius: 8, bg: C.field, border: { c: props.border || C.fieldBorder, w: 1 }, ...props.frame,
  }, children);
  const bullet = (iconName, title, sub) => row({ name: `能做什么/${title}`, w: "fill", gap: 10, align: "start" }, [
    row({ name: "图标", w: 26, h: 26, radius: 7, bg: C.accentSoft, justify: "center" }, [icon(iconName, { size: 14, color: C.accent })]),
    col({ name: "说明", w: "fill", gap: 1 }, [text(title), hint(sub)]),
  ]);
  const account = (compact = false) => row({ name: "账号", w: "fill", gap: 10 }, [
    row({ name: "头像", w: 30, h: 30, radius: 15, bg: C.hover, justify: "center" }, [icon("users", { size: 15, color: C.accent })]),
    col({ name: "名字", w: "fill", gap: 1 }, [text("Tester"), compact ? null : hint("cn.fflogs.com · 公会 Static")]),
    button("退出登录", "ghost"),
  ]);
  const uploadTitle = (label, tone) => ({ titleExtra: [pill(label, tone)], actions: [button("上传设置", "ghost", { icon: "gear" })] });
  const liveRow = on => row({ name: "实时上传", w: "fill", gap: 10 }, [
    col({ name: "说明", w: "fill", gap: 2 }, [text("实时上传"), hint("每场战斗结束后自动传到同一份报告。")]),
    toggle(on),
  ]);
  const report = (title, sub, tone = C.accent) => inset("最近结果", [
    row({ name: "结果", w: "fill", gap: 10, align: "start" }, [
      icon("check", { size: 14, color: tone }),
      col({ name: "说明", w: "fill", gap: 2 }, [text(title, { w: "fill" }), hint(sub)]),
    ]),
    row({ name: "操作", w: "fill", gap: 4 }, [button("打开报告", "ghost", { icon: "external" }), button("复制链接", "ghost", { icon: "copy" })]),
  ]);
  const uploadButtons = (disabled = false) => row({ name: "上传", w: "fill", gap: 8 }, [
    button("上传最新日志", "primary", { icon: "upload", disabled }), button("选择战斗…", "secondary", { icon: "search", disabled }),
  ]);
  const latestLog = () => hint("最新日志：Network_27580_20261123.log · 今天 21:48");

  // 未登录: the form, and what signing in gets you (the actions stay hidden until then).
  const signedOut = card("日志上传", [
    hint("用 FFLogs 网站账号把 DACT 保存的 Network 日志传成报告。解析和上传在游戏之外的辅助进程里进行。"),
    field("登录邮箱", input("输入邮箱", { placeholder: true })),
    field("登录密码", input("输入密码", { placeholder: true, trailing: "eye" })),
    row({ name: "记住密码", w: "fill", gap: 8 }, [
      checkbox(false), text("记住密码"), hint("加密后只保存在这台电脑", { w: "fill" }),
    ]),
    row({ name: "登录", w: "fill", gap: 8 }, [button("登录 FFLogs", "primary")]),
    divider(),
    bullet("upload", "上传最新日志", "或只选其中几场上传。"),
    bullet("pulse", "实时上传", "打本时每场结束自动上传。"),
    bullet("history", "战斗记录里上传单场", "在某一场的详情里直接上传。"),
  ], { titleExtra: [pill("未登录", "off")] });

  const signedIn = card("日志上传", [
    account(),
    divider(),
    uploadButtons(),
    latestLog(),
    divider(),
    liveRow(false),
    divider(),
    report("上传完成 · 2 场战斗", "报告 aBcD1234EfGh5678 · 私人 · 今天 21:50"),
  ], uploadTitle("已登录", "ok"));

  // 选择战斗: the fights of the latest log, parsed in the helper; only names and end times come back.
  const fightRow = (on, name, time) => row({ name: `战斗/${name} ${time}`, w: "fill", h: 30, pad: [0, 8], gap: 10, radius: 6, bg: on ? C.accentSoft : null }, [
    checkbox(on), text(name, { w: "fill" }), text(time, { size: F.small, color: C.muted }),
  ]);
  const picking = card("日志上传", [
    account(true),
    divider(),
    row({ name: "选择", w: "fill", gap: 8 }, [
      col({ name: "标题", w: "fill", gap: 1 }, [text("选择要上传的战斗"), hint("Network_27580_20261123.log · 5 场")]),
      button("全选", "ghost"),
    ]),
    col({ name: "战斗列表（独立滚动）", w: "fill", pad: 4, gap: 2, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
      fightRow(true, "光暗未来绝境战", "21:42 结束"),
      fightRow(true, "光暗未来绝境战", "21:36 结束"),
      fightRow(false, "阿卡狄亚零式 · 4", "20:58 结束"),
      fightRow(false, "木人", "20:40 结束"),
      fightRow(false, "阿卡狄亚零式 · 3", "20:12 结束"),
    ]),
    row({ name: "操作", w: "fill", gap: 8 }, [button("上传选中的 2 场", "primary", { icon: "upload" }), button("取消", "secondary")]),
    hint("按“上传设置”里的可见性、区域和公会创建一份新报告。"),
  ], uploadTitle("已登录", "ok"));

  const live = card("日志上传", [
    account(),
    divider(),
    uploadButtons(),
    latestLog(),
    divider(),
    liveRow(true),
    inset("实时状态", [
      row({ name: "状态", w: "fill", gap: 10, align: "start" }, [
        frame({ name: "dot", w: 6, h: 6, radius: 3, bg: C.accent }),
        col({ name: "说明", w: "fill", gap: 2 }, [text("已上传 3 场", { w: "fill" }), hint("报告 Xy9KqLmN42PrTs7V · 等待下一场战斗结束")]),
      ]),
      row({ name: "操作", w: "fill", gap: 4 }, [button("打开报告", "ghost", { icon: "external" }), button("复制链接", "ghost", { icon: "copy" })]),
    ], { border: "rgba(107,199,245,0.45)" }),
  ], uploadTitle("实时上传中", "ok"));

  // 进行中: one status block for the engine download (first use only), parsing and uploading.
  const bar = fraction => frame({ name: "进度条", w: "fill", h: 6, radius: 3, bg: C.hover, layout: "free" }, [
    frame({ name: "值", x: 0, y: 0, w: Math.round(290 * fraction), h: 6, radius: 3, bg: C.accent }),
  ]);
  const working = card("日志上传", [
    account(),
    divider(),
    uploadButtons(true),
    inset("进行中", [
      row({ name: "标题", w: "fill", gap: 8 }, [text("正在下载解析引擎", { w: "fill" }), text("42%", { color: C.accent })]),
      bar(0.42),
      hint("只在第一次使用时下载，约 15 MB：微软官方的 V8 运行库，来自 NuGet 并按哈希校验。之后解析日志、上传也在这里显示进度。"),
      row({ name: "操作", w: "fill" }, [button("取消", "secondary")]),
    ], { border: "rgba(107,199,245,0.45)" }),
    divider(),
    liveRow(false),
  ], uploadTitle("已登录", "ok"));

  const page = (estimate, upload) => shell("fflogs", [header(), grid2([estimate], [upload])]);

  // ---------------------------------------------------------------- 上传设置（子页面）
  const settings = shell("fflogs", [
    pageHeader("上传设置", "FFLogs / 上传设置 · 对上传最新日志、选择战斗、实时上传和战斗记录里的单场上传都生效。", [button("返回 FFLogs", "secondary")]),
    row({ name: "上传设置", w: "fill", gap: 14, align: "start" }, [
      col({ name: "左栏", w: "fill", gap: 14 }, [
        card("报告", [
          field("可见性", select("私人"), "公开：所有人可见；私人：只有自己和公会成员可见；不公开：知道链接的人可见。"),
          field("区域", select("CN"), "国服只有 CN；国际服可选 NA / EU / JP / OC。"),
          field("公会", select("个人报告（不选公会）"), "登录后列出账号所在的公会。"),
          field("报告描述", input("可选", { placeholder: true })),
        ]),
      ]),
      col({ name: "右栏", w: "fill", gap: 14 }, [
        card("自动", [
          toggleRow("启动时自动登录", false, "需要先记住密码。启动后等 DACT 账号校验和 Host 就绪再登录。"),
          toggleRow("自动登录后开始实时上传", false),
          toggleRow("实时上传包含已有战斗", false, "打开后，开始实时上传时把当前日志里已经打完的战斗一并上传。"),
        ]),
        card("密码与解析引擎", [
          toggleRow("记住密码", true, "加密后只保存在这台电脑的当前 Windows 账户下；关闭后立即删除。"),
          divider(),
          row({ name: "解析引擎", w: "fill", gap: 8, align: "start" }, [
            col({ name: "说明", w: "fill", gap: 2 }, [text("解析引擎"), hint("FFLogs 的解析器要用 V8 运行库，第一次解析时下载（约 15 MB）。")]),
            pill("已就绪", "ok"),
          ]),
        ]),
      ]),
    ]),
  ]);

  // ---------------------------------------------------------------- 战斗统计（FFLogs 移出后）
  // The template choice becomes three previews instead of a segmented control; the remaining
  // fields keep their wording. Nothing else moves: 重置当前战斗…, 自定义… and the header stay.
  // 204 px is the tile's inner width in the 920 px window (three tiles across the content column).
  const PREVIEW_WIDTH = 204, PREVIEW_HEIGHT = 72;
  const barRow = (y, w, color) => frame({ name: "行", x: 10, y, w, h: 8, radius: 2, bg: color });
  const PREVIEW = {
    经典榜: [barRow(8, 184, C.accent), barRow(21, 150, "#3F9CCB"), barRow(34, 120, "#2F6C8F"), barRow(47, 90, C.hover), barRow(60, 64, C.hover)],
    横版: [0, 1, 2, 3, 4].map(i => frame({ name: "格", x: 10 + i * 38, y: 27, w: 34, h: 18, radius: 3, bg: [C.accent, "#3F9CCB", "#2F6C8F", C.hover, C.hover][i] })),
    职能分栏: [
      ...[0, 1, 2].map(i => frame({ name: "D/T", x: 10, y: 14 + i * 14, w: [84, 68, 52][i], h: 8, radius: 2, bg: [C.accent, "#3F9CCB", "#2F6C8F"][i] })),
      frame({ name: "分隔", x: 102, y: 8, w: 1, h: 56, bg: C.divider }),
      ...[0, 1].map(i => frame({ name: "H", x: 112, y: 14 + i * 14, w: [80, 60][i], h: 8, radius: 2, bg: ["#5FBF8F", "#3C7A5A"][i] })),
    ],
  };
  const template = (label, selected) => col({
    name: `模板/${label}`, w: "fill", pad: 6, gap: 6, radius: 8, bg: selected ? C.accentSoft : C.field,
    border: { c: selected ? C.accent : C.fieldBorder, w: 1 },
  }, [
    frame({ name: "预览", layout: "free", w: PREVIEW_WIDTH, h: PREVIEW_HEIGHT, radius: 5, bg: C.raised, border: { c: C.fieldBorder, w: 1 } }, PREVIEW[label]),
    row({ name: "名称", w: "fill", gap: 4, h: 18 }, [text(label, { w: "fill", color: selected ? C.accent : C.text }), selected ? icon("check", { size: 14, color: C.accent }) : null]),
  ]);
  const meter = shell("meter", [
    pageHeader("战斗统计", "仅调整内置战斗统计的显示，不影响 Cactbot 或 HTML 悬浮窗。", [
      row({ name: "显示开关", gap: 8 }, [text("显示战斗统计", { color: C.muted }), toggle(true)]),
      button("定位到战斗统计", "secondary", { icon: "target" }),
    ]),
    card("榜单模板", [
      row({ name: "模板", w: "fill", gap: 10, align: "stretch" }, [template("经典榜", true), template("横版", false), template("职能分栏", false)]),
      row({ name: "说明", w: "fill", gap: 10 }, [
        hint("一次只显示一个榜单；切换时保留各模板的位置、大小、锁定和槽位配置。"),
        button("自定义布局…", "secondary", { icon: "edit" }),
      ]),
    ]),
    grid2([
      card("显示", [
        field("显示范围", select("按榜单设置"), "沿用各榜单的 8/24 人、队伍选择和仅自己等显示设置。"),
        field("玩家 ID 显示", select("显示原始 ID"), "遮盖只影响界面显示，不修改解析数据和战斗日志。"),
      ]),
    ], [
      card("统计", [
        field("统计重置方式", select("DACT 默认"), "保持原来的统计逻辑。"),
        field("刷新间隔", slider(0.32, "750 毫秒")),
      ], { actions: [button("重置当前战斗…", "danger", { icon: "reset" })] }),
      row({ name: "FFLogs 去向", w: "fill", pad: [8, 12], gap: 8, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } }, [
        icon("fflogs", { size: 14 }), hint("Parse 预估已移到 FFLogs 页。"), button("前往 FFLogs", "ghost"),
      ]),
    ]),
  ]);

  // ---------------------------------------------------------------- 战斗记录 · 上传单场
  // Only the detail header of 战斗记录 changes: one more button and one status line under it.
  const historyHeader = col({
    // 471 px is the detail column of 战斗记录 in the 920 px window, plus this board's own padding.
    name: "战斗记录 · 详情标题", w: 503, pad: 16, gap: 10, radius: 10, bg: C.surface, border: { c: C.border, w: 1 },
  }, [
    row({ name: "标题", w: "fill", gap: 8 }, [
      col({ name: "战斗", w: "fill", gap: 2 }, [text("光暗未来绝境战 · 第 32 次", { size: F.h2 }), hint("今天 21:42 · 03:12 · 打到 P2 中段")]),
      button("上传到 FFLogs", "secondary", { icon: "upload" }),
      button("AI 复盘", "primary", { icon: "sparkle" }),
    ]),
    row({ name: "上传结果", w: "fill", gap: 6 }, [
      icon("check", { size: 14, color: C.accent }), hint("已上传，报告 aBcD1234EfGh5678", { w: "fill" }), button("打开报告", "ghost", { icon: "external" }),
    ]),
  ]);

  return [
    { name: "FFLogs", tree: page(estimateCard(false), signedOut), note: "新侧栏项；未登录、预估未启用。Parse 预估卡片从战斗统计页原样搬来" },
    { name: "FFLogs · 已登录", tree: page(estimateCard(true), signedIn), note: "登录后表单收成一行账号；最近一次上传的结果留在卡片底部" },
    { name: "FFLogs · 选择战斗", tree: page(estimateCard(true), picking), note: "点「选择战斗…」后列出最新日志里的战斗；列表独立滚动" },
    { name: "FFLogs · 实时上传中", tree: page(estimateCard(true), live), note: "开关打开后每场结束自动上传；停止只需关掉开关" },
    { name: "FFLogs · 进行中", tree: page(estimateCard(true), working), note: "首次使用下载解析引擎；解析、上传共用这一块进度，取消只停当前操作，不退出登录" },
    { name: "FFLogs · 上传设置", tree: settings, note: "FFLogs 页「上传设置」进入，「返回 FFLogs」回去" },
    { name: "战斗统计（FFLogs 移出后）", tree: meter, note: "榜单模板改为三张预览卡；其余字段文案不变，FFLogs 卡片移到 FFLogs 页" },
    { name: "战斗记录 · 上传单场", tree: historyHeader, note: "只画详情标题：「AI 复盘」左边多一个「上传到 FFLogs」，结果显示在标题下" },
  ];
};
