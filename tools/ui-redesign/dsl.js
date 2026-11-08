// Tiny layout description shared by the HTML preview and the Figma builder. Frames follow
// Figma auto layout (row/col, gap, padding, hug/fill/fixed sizing), which maps 1:1 to CSS
// flexbox, so the browser preview shows what Figma will build.
//
// Colours come only from the eight SkinPalette slots in DactTheme.cs (plus alpha and a few
// fixed status colours), so every existing skin can restyle the new layout unchanged.
"use strict";

// Default skin "深海原色" (DactTheme.DefaultPalette).
const P = {
  surface: "#090C11", raised: "#12181F", hover: "#1B252F", gold: "#C7A85C",
  accent: "#6BC7F5", text: "#FFFFFF", muted: "#A6B3C2", border: "#35406B",  // indigo slate matching the DACT logo (was gold #574A2E)
};
// Derived colours; each one is a palette slot with alpha or a lerp that ImGui can compute.
const C = {
  ...P,
  divider: "rgba(53,64,107,0.55)",
  cardBorder: "rgba(53,64,107,0.75)",
  field: "#090C11",                          // inputs sit inset (Surface) inside Raised cards
  fieldBorder: "rgba(166,179,194,0.16)",
  primary: "#1C4A61",                         // lerp(Hover, Accent, .35)
  accentSoft: "rgba(107,199,245,0.14)",
  goldSoft: "rgba(199,168,92,0.16)",
  danger: "#E06C6C", dangerSoft: "rgba(224,108,108,0.14)", dangerFill: "#4A1F23",
  ok: "#6BC7F5", off: "rgba(166,179,194,0.12)",
  badge: "#D9534F",
  hint: "rgba(166,179,194,0.82)",
};

// Figma sizes for Noto Sans SC Medium (same family as the in-game Noto Sans CJK SC);
// 13 px here equals ImGui's 17 px default font.
const F = { title: 18, h2: 15, body: 13, small: 12, tiny: 11 };
const LH = { 18: 24, 15: 20, 13: 18, 12: 16, 11: 14 };

function frame(props, children = []) {
  return { t: "frame", layout: "col", gap: 0, pad: 0, w: "hug", h: "hug", ...props, children: children.filter(Boolean) };
}
const row = (props, children) => frame({ layout: "row", align: "center", ...props }, children);
const col = (props, children) => frame({ layout: "col", ...props }, children);
const spacer = () => frame({ name: "spacer", w: "fill", h: 1 });
const vspacer = () => frame({ name: "spacer", w: 1, h: "fill" });

function text(value, props = {}) {
  const size = props.size || F.body;
  return { t: "text", text: value, size, lh: LH[size] || Math.round(size * 1.35), color: C.text, w: "hug", ...props };
}
const hint = (value, props) => text(value, { size: F.small, color: C.hint, w: "fill", ...props });
const icon = (name, props = {}) => ({ t: "icon", name, size: 16, color: C.muted, ...props });
const img = (src, w, h, props = {}) => ({ t: "img", src, w, h, ...props });
// Fixed-size vector art (skin thumbnails); markup is a complete <svg> sized w × h.
const vector = (markup, w, h, props = {}) => ({ t: "svg", markup, w, h, name: "图形", ...props });
const divider = (props = {}) => frame({ name: "divider", w: "fill", h: 1, bg: C.divider, ...props });

// ---- Components (comp key = Figma main component; same key => same structure).

function button(label, kind = "secondary", props = {}) {
  const look = {
    primary: { bg: C.primary, color: C.text },
    secondary: { bg: C.hover, color: C.text },
    danger: { bg: C.dangerFill, color: "#F3B6B6" },
    ghost: { bg: null, color: C.accent },
  }[kind];
  const lead = props.icon ? icon(props.icon, { color: look.color, size: 14 }) : null;
  return row({
    name: `按钮/${label}`, comp: `Button/${kind}${lead ? "+icon" : ""}`, h: 30, pad: [0, kind === "ghost" ? 6 : 14],
    gap: 6, radius: 6, bg: look.bg, justify: "center", w: props.w || "hug", disabled: props.disabled,
    opacity: props.disabled ? 0.45 : 1,
  }, [lead, text(label, { color: look.color })]);
}
const iconButton = (name, props = {}) => row({
  // One component for every icon: the icon is an instance-swap property, the fill a variant.
  name: `图标按钮/${name}`, comp: `IconButton/${props.bg ? "filled" : "plain"}`, w: 28, h: 28, radius: 6, justify: "center",
  bg: props.bg === undefined ? null : props.bg,
}, [icon(name, { color: props.color || C.muted })]);

function toggle(on) {
  return row({
    name: on ? "开关/开" : "开关/关", comp: on ? "Toggle/On" : "Toggle/Off", w: 34, h: 20, radius: 10, pad: [0, 3],
    bg: on ? C.accent : C.hover, justify: on ? "end" : "start",
  }, [frame({ name: "knob", w: 14, h: 14, radius: 7, bg: on ? C.surface : C.muted })]);
}
function checkbox(on) {
  return row({
    name: on ? "勾选/开" : "勾选/关", comp: on ? "Checkbox/On" : "Checkbox/Off", w: 18, h: 18, radius: 4, justify: "center",
    bg: on ? C.accent : C.field, border: on ? null : { c: C.fieldBorder, w: 1 },
  }, [on ? icon("check", { size: 12, color: C.surface }) : null]);
}
// Open state keeps the down arrow (ImGui Combo does not flip it) and turns border and arrow ice blue.
function select(value, props = {}) {
  return row({
    name: `下拉/${value}`, comp: props.open ? "Select/open" : "Select", w: props.w || "fill", h: 30, radius: 6, pad: [0, 10], gap: 8,
    bg: C.field, border: { c: props.open ? C.accent : C.fieldBorder, w: 1 }, opacity: props.disabled ? 0.5 : 1,
  }, [text(value, { w: "fill", color: props.placeholder ? C.muted : C.text }), icon("chevronDown", { size: 14, color: props.open ? C.accent : C.muted })]);
}
// One option row of an open dropdown: normal, hover (Hover fill) or selected (soft ice blue + check).
function menuItem(label, state = "normal") {
  const selected = state === "selected";
  return row({
    name: label, comp: `MenuItem/${state}`, w: "fill", h: 28, radius: 6, pad: [0, 10], gap: 8,
    bg: { normal: null, hover: C.hover, selected: C.accentSoft }[state],
  }, [text(label, { w: "fill", color: selected ? C.accent : C.text }), selected ? icon("check", { size: 14, color: C.accent }) : null]);
}
// Collapsible help line (ImGui CollapsingHeader). Ice-blue text marks it clickable; the open
// state turns the arrow down and the caller puts a stepsPanel right below it.
function disclosure(label, open = false) {
  return row({ name: `折叠/${label}`, comp: `Disclosure/${open ? "open" : "closed"}`, w: "fill", h: 24, gap: 6 }, [
    icon(open ? "chevronDown" : "chevronRight", { size: 14, color: C.accent }), text(label, { color: C.accent }),
  ]);
}
// Numbered instructions inside an expanded disclosure: inset panel, one row per step.
function stepsPanel(steps) {
  return col({ name: "说明面板", w: "fill", pad: [10, 12], gap: 8, radius: 8, bg: C.field, border: { c: C.fieldBorder, w: 1 } },
    steps.map((step, i) => row({ name: `第 ${i + 1} 步`, w: "fill", gap: 8, align: "start" }, [
      row({ name: "序号", w: 18, h: 18, radius: 9, bg: C.hover, justify: "center" }, [text(String(i + 1), { size: F.tiny, color: C.muted })]),
      text(step, { w: "fill" }),
    ])));
}
// Name with the server-authorized markers (AccountIdentityBadge): a sponsor's name uses a fixed
// red that ignores the skin, followed by the crown + Lv plate, then the administrator icon.
const SPONSOR_RED = "#F53847";
const sponsorBadge = level => row({ name: "赞助标识", comp: "SponsorBadge", h: 18, pad: [1, 6, 1, 3], gap: 4, radius: 3, bg: "#3B1F14", border: { c: "#E8BF6B", w: 1 } },
  [img("crown", 14, 14, { name: "王冠" }), text(level, { size: F.tiny, color: "#FFDB99" })]);
const adminBadge = () => img("admin", 16, 16, { name: "管理员标识", comp: "AdminBadge" });
function identityName(name, o = {}) {
  const kind = o.sponsor && o.admin ? "sponsor+admin" : o.sponsor ? "sponsor" : o.admin ? "admin" : "plain";
  return row({ name: `身份/${name}`, comp: `IdentityName/${kind}`, gap: 4 }, [
    text(name, { size: o.size || F.body, color: o.sponsor ? SPONSOR_RED : o.color || C.text }),
    o.sponsor ? sponsorBadge(o.sponsor) : null, o.admin ? adminBadge() : null,
  ]);
}
// Popup list under an open select. It floats over the page, so callers usually pass abs/x/y.
function dropdownMenu(items, props = {}) {
  return col({
    name: "下拉菜单", comp: "DropdownMenu", w: props.w || 222, pad: 4, gap: 2, radius: 8, bg: C.raised,
    border: { c: C.border, w: 1 }, shadow: true, abs: props.abs, x: props.x, y: props.y,
  }, items.map((item, i) => menuItem(item, i === props.selected ? "selected" : i === props.hover ? "hover" : "normal")));
}
function input(value, props = {}) {
  return row({
    name: `输入/${value}`, comp: props.trailing ? "Input+icon" : "Input", w: props.w || "fill", h: 30, radius: 6, pad: [0, 10], gap: 8,
    bg: C.field, border: { c: C.fieldBorder, w: 1 }, abs: props.abs, x: props.x, y: props.y,
  }, [text(value, { w: "fill", color: props.placeholder ? C.muted : C.text }), props.trailing ? icon(props.trailing, { size: 14 }) : null]);
}
// A slider is a track with a filled part, a knob and its value on the right.
function slider(fraction, value, props = {}) {
  const width = props.trackWidth || 150;
  const filled = Math.max(6, Math.round(width * fraction));
  return row({ name: `滑块/${value}`, w: props.w || "hug", h: 30, gap: 12 }, [
    frame({ name: "track", layout: "free", w: width, h: 14 }, [
      frame({ name: "rail", x: 0, y: 5, w: width, h: 4, radius: 2, bg: C.hover }),
      frame({ name: "fill", x: 0, y: 5, w: filled, h: 4, radius: 2, bg: C.accent }),
      frame({ name: "knob", x: filled - 7, y: 0, w: 14, h: 14, radius: 7, bg: C.text }),
    ]),
    text(value, { color: C.muted }),
  ]);
}
function segmented(items, active, props = {}) {
  return row({
    name: "分段选择", comp: `Segmented/${items.length}/${active}`, w: props.w || "hug", h: 32, pad: 3, gap: 2, radius: 8, bg: C.field,
    border: { c: C.fieldBorder, w: 1 },
  }, items.map((item, i) => row({
    name: item, h: 26, w: props.w ? "fill" : "hug", pad: [0, 12], radius: 6, justify: "center", bg: i === active ? C.hover : null,
  }, [text(item, { color: i === active ? C.text : C.muted })])));
}
function tabs(items, active) {
  return row({ name: "标签页", comp: `Tabs/${items.length}/${active}`, w: "fill", h: 34, gap: 18, border: null }, items.map((item, i) =>
    col({ name: item, h: 34, justify: "end", gap: 7 }, [
      text(item, { color: i === active ? C.text : C.muted }),
      frame({ name: "indicator", w: "fill", h: 2, radius: 1, bg: i === active ? C.accent : null }),
    ])));
}
function pill(label, tone = "ok", props = {}) {
  const look = {
    ok: [C.accentSoft, C.accent], warn: [C.goldSoft, C.gold], off: [C.off, C.muted], error: [C.dangerSoft, C.danger],
  }[tone];
  return row({ name: `状态/${label}`, comp: `Pill/${tone}${props.dot === false ? "" : "+dot"}`, h: 22, pad: [0, 9], gap: 6, radius: 11, bg: look[0] }, [
    props.dot === false ? null : frame({ name: "dot", w: 6, h: 6, radius: 3, bg: look[1] }),
    text(label, { size: F.small, color: look[1] }),
  ]);
}
const badge = (value) => row({ name: `未读/${value}`, comp: "Badge", h: 18, pad: [0, 6], radius: 9, bg: C.badge, justify: "center" },
  [text(String(value), { size: F.tiny, color: C.text })]);

function card(title, children, props = {}) {
  return col({
    name: `卡片/${title || "无标题"}`, w: props.w || "fill", h: props.h || "hug", pad: props.pad ?? 14, gap: props.gap ?? 12, radius: 10,
    bg: C.raised, border: { c: C.cardBorder, w: 1 },
  }, [
    title ? row({ name: "卡片标题", w: "fill", gap: 8 }, [
      text(title, { color: C.gold, w: "hug" }), ...(props.titleExtra || []), spacer(), ...(props.actions || []),
    ]) : null,
    ...children,
  ]);
}
// Stacked field: label (and optional value on the right), the control, then a hint.
function field(label, control, hintText, props = {}) {
  return col({ name: `字段/${label}`, w: "fill", gap: 6 }, [
    row({ name: "标签", w: "fill", gap: 6 }, [text(label), props.help ? icon("help", { size: 13 }) : null, spacer(), props.trailing || null]),
    control,
    hintText ? hint(hintText) : null,
  ]);
}
// Toggle row: label left, switch right, optional hint below.
function toggleRow(label, on, hintText) {
  return col({ name: `开关行/${label}`, w: "fill", gap: 4 }, [
    row({ name: "行", w: "fill", gap: 10, h: 24 }, [text(label, { w: "fill" }), toggle(on)]),
    hintText ? hint(hintText) : null,
  ]);
}
const grid2 = (left, right, props = {}) => row({ name: props.name || "两栏", w: "fill", gap: props.gap ?? 14, align: "start" }, [
  col({ name: "左栏", w: "fill", gap: props.gap ?? 14 }, left), col({ name: "右栏", w: "fill", gap: props.gap ?? 14 }, right),
]);

module.exports = {
  P, C, F, LH, frame, row, col, spacer, vspacer, text, hint, icon, img, vector, divider,
  button, iconButton, toggle, checkbox, select, menuItem, dropdownMenu, disclosure, stepsPanel, identityName, input, slider, segmented, tabs,
  pill, badge, card, field, toggleRow, grid2,
};
