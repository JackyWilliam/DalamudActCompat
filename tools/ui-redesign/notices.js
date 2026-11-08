// 三方扩展 (ThirdPartyPluginNoticeWindow) in the 1.0 style: same notice text, the five bundled
// DLLs as cards with author / licence / links / hash, the install-before-use footer when something
// is pending, and the two choice dialogs that cannot be dismissed without an answer. Metadata is
// copied from bundled-plugins.lock.json, which the window renders as-is.
"use strict";
const {
  C, F, frame, row, col, spacer, text, hint, img, divider, button, iconButton, pill,
} = require("./dsl");

const W = 1000, H = 640;
const PLUGINS = [
  ["Triggernometry CN Maintained Edition", "2.4.3.0", [["DLL 作者", "Paissa Heavy Industries"], ["当前维护者", "MnFeN (Chinese maintained edition)"], ["版权", "(c) 2016-2025 Paissa Heavy Industries"], ["许可证", "MIT"]], "8c1f…4e2a"],
  ["PostNamazu", "1.3.6.6", [["DLL 作者", "Natsukage"], ["许可证", "Redistribution authorized by the author; the upstream repository declares no SPDX license"]], "51d0…9b07"],
  ["ACT.FoxTTS", "3.3.1.189", [["DLL 作者", "Noisyfox"], ["版权", "Copyright (c) 2018-2022 Noisyfox"], ["许可证", "GPL-3.0"]], "e7a3…12cc"],
  ["银山雀儿 / SilverDasher", "0.6.0.4", [["DLL 作者", "The Players / SilverDasher"], ["当前维护者", "SilverDasher maintainers / QQ group 582145824"], ["版权", "Copyright © The Players 2022-2025"], ["许可证", "The supplied package contains no license file; redistribution of this bundled version was authorized by the upstream maintainer on 2026-08-10"]], "0b9e…a611"],
  ["抹茶 / Cafe.Matcha", "26.9.26.1753", [["DLL 作者", "FFCafe / The Waking Sands"], ["当前维护者", "The Waking Sands contributors; DACT compatibility build maintained by DalamudActCompat"], ["版权", "Copyright © FFCafe and Cafe.Matcha contributors"], ["许可证", "AGPL-3.0"]], "c442…7f85"],
];

module.exports = () => {
  // The window has a fixed height: only the middle area scrolls, so the install footer (when
  // there is one) stays on screen however long the cards get or however small the window is.
  // thumb: scrollbar thumb length, roughly visible / total content height of that state.
  // atEnd draws the area scrolled to the bottom (content and thumb aligned to the end).
  function win(statusPill, content, { footer, overlays = [], thumb, atEnd }) {
    const end = atEnd ? "end" : "start";
    return col({ name: "三方扩展", w: W, h: H, bg: C.surface, radius: 10, clip: true, border: { c: C.border, w: 1 } }, [
      row({ name: "顶栏", w: "fill", h: 48, pad: [0, 10, 0, 14], gap: 10 }, [
        img("logo", 26, 26, { radius: 6 }), text("三方扩展"), hint("第三方扩展来源声明", { w: "hug" }), spacer(), statusPill, iconButton("close"),
      ]),
      divider(),
      row({ name: "内容区", w: "fill", h: "fill", pad: [18, 8, 18, 22], gap: 10, align: "stretch" }, [
        col({ name: "内容（独立滚动）", w: "fill", h: "fill", gap: 14, clip: true, justify: end }, content),
        col({ name: "滚动条", w: 4, h: "fill", radius: 2, bg: C.raised, justify: end }, [frame({ name: "滑块", w: 4, h: thumb, radius: 2, bg: "rgba(166,179,194,0.45)" })]),
      ]),
      ...(footer ? [divider(), footer] : []),
      ...overlays,
    ]);
  }
  const meta = ([k, v]) => row({ name: `信息/${k}`, w: "fill", gap: 10, align: "start" }, [
    text(k, { size: F.small, color: C.muted, w: 72 }), text(v, { size: F.small, w: "fill" }),
  ]);
  // pending: [label, tone] for cards that still need acknowledgement.
  function pluginCard([name, version, info, hash], status) {
    const pending = status && status[1] === "warn";
    return col({ name: `扩展/${name}`, w: "fill", h: "fill", pad: 14, gap: 10, radius: 10, bg: C.raised, border: { c: pending ? "rgba(199,168,92,0.7)" : C.cardBorder, w: 1 } }, [
      col({ name: "标题", w: "fill", gap: 4 }, [text(name, { w: "fill" }), row({ name: "状态", gap: 8 }, [hint(`版本 ${version}`, { w: "hug" }), pill(status ? status[0] : "当前声明已确认", status ? status[1] : "ok", { dot: false })])]),
      col({ name: "信息", w: "fill", gap: 6 }, info.map(meta)),
      // Hash and links sit at the card bottom, so every card in a row lines its buttons up
      // no matter how long the author / licence text above is.
      frame({ name: "间距", w: 1, h: "fill" }),
      col({ name: "哈希", w: "fill", gap: 2 }, [text("入口 DLL SHA-256", { size: F.tiny, color: C.muted }), text(hash, { size: F.small, color: C.muted })]),
      divider(),
      row({ name: "链接", w: "fill", gap: 4 }, [button("项目", "ghost", { icon: "external" }), button("源码", "ghost", { icon: "external" }), button("下载", "ghost", { icon: "external" })]),
    ]);
  }
  // Rows get a fixed height (the tallest card) because a hug row whose cards all fill
  // collapses in Figma; equal heights also keep both rows' link bars on one line.
  const CARD_H = 352;
  const cards = statuses => {
    const list = PLUGINS.map((p, i) => pluginCard(p, statuses[i]));
    return col({ name: "扩展卡片", w: "fill", gap: 12 }, [
      row({ name: "第一行", w: "fill", h: CARD_H, gap: 12, align: "stretch" }, list.slice(0, 3)),
      row({ name: "第二行", w: "fill", h: CARD_H, gap: 12, align: "stretch" }, [...list.slice(3), frame({ name: "占位", w: "fill" })]),
    ]);
  };
  const about = () => col({ name: "关于这些 DLL", w: "fill", pad: 14, gap: 6, radius: 10, bg: C.raised, border: { c: C.cardBorder, w: 1 } }, [
    text("关于这些 DLL", { color: C.gold }),
    text("DACT 随安装包提供以下第三方 ACT DLL，并在启动时检查其公开上游。它们不由本项目开发，也不代表原作者或维护者与本项目存在合作、认可或联系。首次安装、本插件每次更新及发现上游 DLL 更新后，都会在安装或更新前展示本声明。", { w: "fill" }),
    hint("下列网址仅用于核对作者项目、许可证、源码和实际下载来源；仅在点击按钮后打开。"),
  ]);
  const title = () => row({ name: "标题", w: "fill", gap: 10 }, [text("第三方扩展来源声明", { size: F.title }), hint("安装前核对", { w: "hug" })]);

  const confirmed = () => text("当前声明已确认；作者和来源信息仍会长期显示。", { color: C.accent, w: "fill" });
  const done = win(pill("声明已确认", "ok"), [title(), about(), confirmed(), cards([])], { thumb: 340 });

  const PENDING = [["待确认作者上游更新", "warn"], null, null, null, ["待确认安装包内版本", "warn"]];
  // Sits outside the scroll area at the window bottom (see win), always visible.
  const footer = () => row({ name: "底栏（固定）", w: "fill", h: 64, pad: [0, 22], gap: 14, bg: C.raised }, [
    button("知悉并安装 / 更新", "primary"),
    hint("可关闭后稍后处理；未确认的扩展保持禁用。确认来源并完成安装后才能继续权限设置。", { w: "fill" }),
  ]);
  const banner = row({ name: "检查结果", w: "fill", gap: 8, pad: [8, 12], radius: 8, bg: C.accentSoft }, [
    text("检查结果", { size: F.small, color: C.accent }), text("发现 2 项需要确认：Triggernometry 上游有新版本；抹茶随本插件更新了安装包。", { size: F.small, w: "fill" }),
  ]);
  const pendingContent = () => [title(), banner, about(), cards(PENDING)];
  const pending = win(pill("待确认 2 项", "warn"), pendingContent(), { footer: footer(), thumb: 260 });
  const pendingEnd = win(pill("待确认 2 项", "warn"), pendingContent(), { footer: footer(), thumb: 260, atEnd: true });

  // Choice dialogs float over the window; neither can be closed without picking an option.
  const scrim = () => frame({ name: "遮罩", abs: true, x: 0, y: 0, w: W, h: H, bg: "rgba(4,6,9,0.62)" });
  // y is (H - dialog height) / 2 with the height measured in Figma: dialogs sit centred in the window.
  const dialog = (name, children, y) => col({ name, w: 560, pad: 20, gap: 12, radius: 12, bg: C.raised, border: { c: C.border, w: 1 }, shadow: true, abs: true, x: (W - 560) / 2, y }, children);
  const permission = win(pill("待确认 2 项", "warn"), pendingContent(), { footer: footer(), thumb: 260, overlays: [scrim(), dialog("权限选择", [
    text("是否启用扩展的完整功能？", { size: F.h2, color: C.gold }),
    text("DLL 已完成安装。安全默认设置会关闭网络请求、启动外部程序、写入文件、游戏指令、原生内存和高风险脚本等能力；部分 Triggernometry、鲶鱼精邮差、银山雀儿和抹茶功能因此不可用。你可以现在一次性启用五项随包扩展各自声明的全部能力，也可以保持安全默认，之后在「扩展」每行的「权限」里逐项开启。", { w: "fill" }),
    text("请选择「同意完整权限」或「不同意并保持安全模式」；作出选择前此窗口无法关闭。", { color: C.accent, w: "fill" }),
    row({ name: "按钮", w: "fill", gap: 8 }, [button("同意并启用完整权限", "primary"), button("不同意完整权限，保持安全模式", "secondary")]),
  ], 211)] });
  const tts = win(pill("声明已确认", "ok"), [title(), about(), confirmed(), cards([])], { thumb: 340, overlays: [
    scrim(),
    dialog("TTS Pro", [
      text("是否将 FoxTTS 改为 Cafe TTS Pro？", { size: F.h2, color: C.gold }),
      text("本插件的默认语音链路以 Cafe TTS Pro 为目标。改用 Pro 后，Cactbot 与 Triggernometry 产生的播报文字会交给 Cafe TTS Pro，避免文字正常显示、语音却仍送往旧引擎或没有声音。这里只修改 FoxTTS 的语音引擎，不会覆盖其他 FoxTTS 设置。", { w: "fill" }),
      hint("「本次不更改」会在下次插件更新时再询问；「不再提醒」会永久保留当前引擎且停止询问。"),
      row({ name: "按钮", w: "fill", gap: 8 }, [button("更改为 Pro", "primary"), button("本次不更改", "secondary"), spacer(), button("不再提醒", "ghost")]),
    ], 221),
  ] });

  const note = "1000 × 640 独立窗口；中间内容区独立滚动";
  return [
    { name: "三方扩展 · 声明已确认", tree: done, note: `${note}；设置 ·「三方扩展声明」和帮助里打开` },
    { name: "三方扩展 · 待确认", tree: pending, note: "插件更新或发现上游更新后自动打开；待确认的卡片描金边；「知悉并安装 / 更新」固定在窗口底部，不随内容滚动" },
    { name: "三方扩展 · 待确认（滚动到底）", tree: pendingEnd, note: "滚到最底时底栏仍固定可见；卡片的哈希和项目 / 源码 / 下载固定在卡片底部，同一行对齐" },
    { name: "三方扩展 · 权限选择", tree: permission, note: "安装完成后弹出，选择前不能关闭" },
    { name: "三方扩展 · 改用 TTS Pro", tree: tts, note: "插件更新后询问一次；可选不再提醒" },
  ];
};
