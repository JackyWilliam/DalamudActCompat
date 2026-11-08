// DACT skins for the 1.0 redesign: palettes (DactTheme.For / UltimateSkins.Make) and the
// decorations each special skin draws in game, ported from UltimateSkins.cs and
// RainWindowRenderer.cs to SVG strings. Pure functions with no Node APIs: the same source is
// pasted into Figma MCP scripts (see skins-mcp.js) and used by Node for previews.
"use strict";

// ---- palettes: surface, raised, hover, gold, accent, text, muted, border (hex or [hex, alpha]).
const BASE = {
  default: { name: "深海原色", group: "基础", p: ["#090C11", "#12181F", "#1B252F", "#C7A85C", "#6BC7F5", "#FFFFFF", "#A6B3C2", "#35406B"] },
  eorzea: { name: "艾欧泽亚", group: "基础", trait: "light", unlock: "赞助 3 级解锁", p: ["#F4DAB5", "#F9E6C8", "#E8CAA0", "#765534", "#805228", "#493728", "#74604C", "#A3865A"] },
  jade: { name: "森之青玉", group: "探索配色", trait: "solid", p: ["#0D1C1B", "#142B28", "#203D34", "#C9BA89", "#74DCC1", "#EDF8F1", "#9EBFB5", "#456957"] },
  amethyst: { name: "月下紫晶", group: "探索配色", trait: "solid", p: ["#1A1428", "#261E37", "#392B4F", "#CCB8DB", "#C5A4FF", "#F4EFFB", "#B9A8C9", "#69557C"] },
  amber: { name: "暮光琥珀", group: "探索配色", trait: "solid", p: ["#231913", "#35261C", "#4B3525", "#E7B771", "#FFC988", "#FFF3E6", "#C7AD95", "#805C39"] },
  "neon-pink": { name: "绯梦霓光", group: "探索配色", trait: "solid", p: ["#271023", "#3D1232", "#602049", "#FF91CE", "#FE1493", "#FFF0F8", "#DFA6C9", "#BD256F"] },
  "liquid-glass": { name: "澄光流璃", group: "探索配色", trait: "glass", p: [["#F9FBFF", .78], ["#FFFFFF", .20], ["#FFFFFF", .38], "#293B4D", "#174A6B", "#14191F", "#3D4955", ["#596D80", .28]] },
  obsidian: { name: "无相玄墨", group: "探索配色", trait: "solid", p: ["#000000", "#0C0C0C", "#252525", "#E1E1E1", "#FFFFFF", "#F5F5F5", "#A3A3A3", "#383838"] },
  "rainy-window": { name: "听雨窗", group: "探索配色", trait: "rain", p: ["#1D2C38", ["#2A3D4B", .92], "#3C5666", "#C6D9E1", "#9CCAD9", "#EDF4F7", "#B0C4CE", "#58717F"] },
};

// Ultimate styles (UltimateSkins.Make arguments, same order of meaning).
const ULT = {
  "ultimate-ucob": { kind: "Bahamut", name: "巴哈姆特绝境战", gun: "#171110", panel: "#1D1514", groove: "#0E0A0A", line: "#35272A", face: "#2D211F", faceLow: "#221816", faceHover: "#382926", facePress: "#160F0E", frame: "#9C8558", frameHigh: "#C9AE74", head: "#E2BB62", hover: "#E8C981", glow: ["#F0C86E", .5], active: "#36C8E6", activeHigh: "#C2F3FF", activeLow: "#127C98", activeFace: "#183C46", activeFaceLow: "#112C34", activeRim: "#3F98AB", activeInk: "#04222B", text: "#F1E6DA", muted: "#B0A194", titleTop: "#2B1E22", titleBottom: "#1A1313", tintLeft: ["#703470", .32], tintRight: ["#C44838", .14], inlay: "#36C8E6", rounding: 3 },
  "ultimate-uwu": { kind: "Weapon", name: "究极神兵绝境战", gun: "#101920", panel: "#14202A", groove: "#0A1117", line: "#263844", face: "#1E2D39", faceLow: "#16232D", faceHover: "#253848", facePress: "#0F1820", frame: "#AAB6C0", frameHigh: "#E6EEF4", head: "#86D3D8", hover: "#EEF6FB", glow: ["#C8F0FF", .6], active: "#F2D774", activeHigh: "#FFF7D6", activeLow: "#A9852A", activeFace: "#3B3720", activeFaceLow: "#2B2917", activeRim: "#C8AF64", activeInk: "#3A2D05", text: "#E8F0F4", muted: "#9DB0BC", titleTop: "#223340", titleBottom: "#121D26", tintLeft: ["#000000", 0], tintRight: ["#82D2E1", .07], inlay: "#41B9C4", rounding: 3 },
  "ultimate-tea": { kind: "Alexander", name: "亚历山大绝境战", gun: "#121715", panel: "#161D19", groove: "#0B0F0D", line: "#28312C", face: "#24302A", faceLow: "#1B241F", faceHover: "#2B3931", facePress: "#131A16", frame: "#8E6B3E", frameHigh: "#B88A4F", head: "#D4AF62", hover: "#D4AF62", glow: ["#F2C45C", .55], active: "#3DCB62", activeHigh: "#B6F6C0", activeLow: "#167234", activeFace: "#22492F", activeFaceLow: "#183925", activeRim: "#5E9A6A", activeInk: "#0A2412", text: "#EFE8D6", muted: "#A8A18F", titleTop: "#25302A", titleBottom: "#161D19", tintLeft: ["#000000", 0], tintRight: ["#000000", 0], inlay: "#F2ECDA", rounding: 4 },
  "ultimate-dsr": { kind: "Dragonsong", name: "幻想龙诗绝境战", gun: "#131315", panel: "#18181C", groove: "#0B0B0D", line: "#2E2E34", face: "#25252B", faceLow: "#1B1B20", faceHover: "#2F2F37", facePress: "#121215", frame: "#707079", frameHigh: "#A6A6B1", head: "#D9B26A", hover: "#D6D6E0", glow: ["#FFA05A", .45], active: "#F08A3C", activeHigh: "#FFD6AE", activeLow: "#A1461A", activeFace: "#4B2817", activeFaceLow: "#381D11", activeRim: "#B96B3D", activeInk: "#3B1406", text: "#ECE8E2", muted: "#A6A19B", titleTop: "#1E2536", titleBottom: "#131620", tintLeft: ["#E2622A", .26], tintRight: ["#6EAAE6", .16], inlay: "#7FB6E8", rounding: 2 },
  "ultimate-top": { kind: "Omega", name: "欧米茄绝境验证战", gun: "#111218", panel: "#15161E", groove: "#0A0B10", line: "#272A36", face: "#20222D", faceLow: "#181A23", faceHover: "#282B39", facePress: "#101118", frame: "#8F96A6", frameHigh: "#D4D9E3", head: "#9FE0FF", hover: "#BDEBFF", glow: ["#82DCFF", .55], active: "#A66DFF", activeHigh: "#E4D0FF", activeLow: "#6A35C9", activeFace: "#2F2252", activeFaceLow: "#22183E", activeRim: "#8B64D8", activeInk: "#1A0838", text: "#E8EAF2", muted: "#9FA4B6", titleTop: "#23262F", titleBottom: "#15161E", tintLeft: ["#000000", 0], tintRight: ["#000000", 0], inlay: "#9FE3FF", rounding: 999 },
  "ultimate-fru": { kind: "Futures", name: "光暗未来绝境战", gun: "#16121E", panel: "#1B1624", groove: "#0E0B13", line: "#30283A", face: "#2A2034", faceLow: "#20192A", faceHover: "#332840", facePress: "#140F1A", frame: "#B2ADBF", frameHigh: "#ECE8F6", head: "#D7B8FF", hover: "#F3EAFF", glow: ["#E6BEFF", .55], active: "#F28DB2", activeHigh: "#FFDCE8", activeLow: "#B04A75", activeFace: "#4B2337", activeFaceLow: "#381A2A", activeRim: "#CA7190", activeInk: "#45102A", text: "#F1ECF6", muted: "#AAA0B7", titleTop: "#281F33", titleBottom: "#18131F", tintLeft: ["#B082FF", .42], tintRight: ["#FFD28C", .25], inlay: "#F3CF7A", rounding: 4 },
};
const SLOTS = ["surface", "raised", "hover", "gold", "accent", "text", "muted", "border"];
const ORDER = ["default", "eorzea", "jade", "amethyst", "amber", "neon-pink", "liquid-glass", "obsidian", "rainy-window",
  "ultimate-ucob", "ultimate-uwu", "ultimate-tea", "ultimate-dsr", "ultimate-top", "ultimate-fru"];

// Every skin as { id, name, group, trait, palette: {slot: [hex, alpha]}, u? }.
function skin(id) {
  if (BASE[id]) {
    const b = BASE[id];
    const palette = {};
    b.p.forEach((c, i) => { palette[SLOTS[i]] = Array.isArray(c) ? c : [c, 1]; });
    return { id, name: b.name, group: b.group, trait: b.trait || "default", unlock: b.unlock, palette };
  }
  const u = ULT[id];
  const palette = {};
  [u.gun, u.panel, u.faceHover, u.head, u.active, u.text, u.muted, u.frame].forEach((c, i) => { palette[SLOTS[i]] = [c, 1]; });
  return { id, name: u.name, group: "绝境战", trait: "ultimate", unlock: "通关任意绝境战解锁", palette, u };
}
const all = () => ORDER.map(skin);

// ---- colour helpers
const hex2 = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
const toHex = rgb => "#" + rgb.map(v => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, "0")).join("").toUpperCase();
const lerp = (a, b, t) => { const x = hex2(a), y = hex2(b); return toHex(x.map((v, i) => v + (y[i] - v) * t)); };
const col = c => Array.isArray(c) ? c : [c, 1];
const f = n => +n.toFixed(2);
// SVG primitives: c = colour (hex or [hex, alpha]).
const fillAttr = (c, a = 1) => { const [h, o] = col(c); return `fill="${h}"${o * a < 1 ? ` fill-opacity="${f(o * a)}"` : ""}`; };
const strokeAttr = (c, w, a = 1) => { const [h, o] = col(c); return `fill="none" stroke="${h}" stroke-width="${f(w)}"${o * a < 1 ? ` stroke-opacity="${f(o * a)}"` : ""}`; };
const circle = (x, y, r, c, a) => `<circle cx="${f(x)}" cy="${f(y)}" r="${f(r)}" ${fillAttr(c, a)}/>`;
const ring = (x, y, r, c, w, a) => `<circle cx="${f(x)}" cy="${f(y)}" r="${f(r)}" ${strokeAttr(c, w, a)}/>`;
const rect = (x, y, w, h, c, a, r = 0) => `<rect x="${f(x)}" y="${f(y)}" width="${f(w)}" height="${f(h)}"${r ? ` rx="${f(r)}"` : ""} ${fillAttr(c, a)}/>`;
const rectStroke = (x, y, w, h, c, sw, a, r = 0) => `<rect x="${f(x)}" y="${f(y)}" width="${f(w)}" height="${f(h)}"${r ? ` rx="${f(r)}"` : ""} ${strokeAttr(c, sw, a)}/>`;
const line = (x1, y1, x2, y2, c, w, a) => `<path d="M${f(x1)} ${f(y1)}L${f(x2)} ${f(y2)}" ${strokeAttr(c, w, a)} stroke-linecap="round"/>`;
const poly = (pts, c, a) => `<path d="M${pts.map(p => `${f(p[0])} ${f(p[1])}`).join("L")}Z" ${fillAttr(c, a)}/>`;
const polyStroke = (pts, c, w, a, closed = true) => `<path d="M${pts.map(p => `${f(p[0])} ${f(p[1])}`).join("L")}${closed ? "Z" : ""}" ${strokeAttr(c, w, a)} stroke-linejoin="round"/>`;
const svg = (w, h, body, defs = "") => `<svg width="${f(w)}" height="${f(h)}" viewBox="0 0 ${f(w)} ${f(h)}" fill="none" xmlns="http://www.w3.org/2000/svg">${defs ? `<defs>${defs}</defs>` : ""}${body}</svg>`;
const hgrad = (id, stops) => `<linearGradient id="${id}" x1="0" y1="0" x2="1" y2="0">${stops.map(([o, c]) => { const [h, a] = col(c); return `<stop offset="${o}" stop-color="${h}" stop-opacity="${a}"/>`; }).join("")}</linearGradient>`;
const polar = (x, y, r, a) => [x + Math.cos(a) * r, y + Math.sin(a) * r];
const sparkle = (x, y, r, c) => { const i = r * .24; return poly([[x, y - r], [x + i, y], [x, y + r], [x - i, y]], c) + poly([[x - r, y], [x, y - i], [x + r, y], [x, y + i]], c); };
const diamond = (x, y, rx, ry, c, a) => poly([[x, y - ry], [x + rx, y], [x, y + ry], [x - rx, y]], c, a);

// ---- ultimate parts (UltimateSkins.cs). Sizes match scale 1 in game.

// End-cap emblem, 30 units square plus its 2-unit drop shadow.
function emblem(u, size = 30) {
  const k = size / 30, c = 15;
  const P = (x, y) => [x * k, y * k];
  const C = (x, y, r, color, a) => circle(x * k, y * k, r * k, color, a);
  const R = (x, y, r, color, w, a) => ring(x * k, y * k, r * k, color, w * k, a);
  const L = (a, b, color, w, al) => line(a[0], a[1], b[0], b[1], color, w * k, al);
  let s = C(c, c + 2, 15, "#000000", .45);
  switch (u.kind) {
    case "Alexander":
      s += C(c, c, 14, "#8E6B3E") + R(c, c, 13.4, "#D9AE6A", 1.2, .8) + C(c, c, 11.6, "#2DAF4F") + C(13.6, 13.2, 8.4, "#6DE08A", .75)
        + R(c, c, 11.6, "#0E3A1B", .8) + R(c, c, 5.2, "#E4FFE8", 1.1, .85);
      for (const [a, b] of [[[15, 5.6], [15, 9.8]], [[15, 20.2], [15, 24.4]], [[5.6, 15], [9.8, 15]], [[20.2, 15], [24.4, 15]]]) s += L(P(...a), P(...b), "#E4FFE8", 1.1, .85);
      s += C(c, c, 1.3, "#E4FFE8") + C(11.4, 9.6, 2.2, "#FFFFFF", .35);
      break;
    case "Bahamut":
      s += poly([P(21, 7), P(29.5, 2), P(24.9, 8.6)], "#C9AE74") + poly([P(9, 7), P(.5, 2), P(5.1, 8.6)], "#C9AE74")
        + C(15, 16, 12.4, "#9C8558") + R(15, 16, 11.8, "#EAD6A0", 1.1, .6) + C(15, 16, 10, "#2E8FCB") + C(13.6, 14.4, 7.4, "#4FB8EC", .85)
        + L(P(8, 21.5), P(22, 10.5), "#E8FBFF", 1.7, .75) + L(P(9.2, 23), P(23.2, 12), "#0E4F7A", .8, .55) + C(11.6, 11.6, 2.4, "#FFFFFF", .45);
      break;
    case "Weapon": {
      const star = [];
      for (let i = 0; i < 16; i++) { const r = i % 2 === 1 ? 3.2 : i % 4 === 0 ? 14.5 : 7.5; star.push(polar(c * k, c * k, r * k, -Math.PI / 2 + i * Math.PI / 8)); }
      s += poly(star, "#C9D3DC") + polyStroke(star, "#3A4652", .7 * k) + C(c, c, 4, "#F4F8FC") + R(c, c, 4, "#8A97A2", .6);
      break;
    }
    case "Dragonsong":
      s += C(c, c, 13.8, "#1E1E24") + R(c, c, 13.8, "#A6A6B1", 1.1);
      for (let i = 0; i < 6; i++) { const [x, y] = polar(c, c, 9.6, -Math.PI / 2 + i * Math.PI / 3); s += C(x, y, 2.3, "#F2EEE6"); }
      s += C(c, c, 5.3, "#C9541E") + C(c, c - .6, 3.8, "#FFB060") + diamond(c * k, c * k, 1.2 * k, 4.3 * k, "#2A0805");
      break;
    case "Omega":
      s += C(c, c, 13.8, "#1B1D25") + R(c, c, 13.8, "#D4D9E3", 1.1);
      for (let i = 0; i < 20; i++) { const [x, y] = polar(c, c, 10.6, i * Math.PI * 2 / 20); s += C(x, y, .7, "#8F96A6"); }
      s += C(c, c, 7.6, "#0B0C11") + C(c, c, 4.4, "#2C6C9A") + C(c, c, 3.1, "#A9E8FF") + C(c, c, 1.6, "#FFFFFF");
      break;
    case "Futures":
      s += feather(P(15, 27), P(15, 1), 3.4 * k) + feather(P(15, 27), P(3.5, 5.5), 3 * k) + feather(P(15, 27), P(26.5, 5.5), 3 * k)
        + C(11.4, 24.6, 3, "#F28DB2") + C(11.4, 24.6, 1.2, "#FFD6E5") + C(18.7, 25.1, 2.5, "#7FB0F0") + C(18.7, 25.1, 1, "#DDEBFF");
      break;
  }
  return svg(size, size + 2 * k, s);
}
function feather(root, tip, width) {
  const ax = [tip[0] - root[0], tip[1] - root[1]], len = Math.hypot(...ax), d = [ax[0] / len, ax[1] / len], n = [-d[1], d[0]];
  const left = [], right = [];
  for (let i = 0; i < 5; i++) {
    const t = (i + 1) / 6, w = width * Math.sin(Math.PI * Math.pow(t, .8));
    left.push([root[0] + d[0] * len * t + n[0] * w, root[1] + d[1] * len * t + n[1] * w]);
    right.push([root[0] + d[0] * len * t - n[0] * w, root[1] + d[1] * len * t - n[1] * w]);
  }
  const outline = [root, ...left, tip, ...right.reverse()];
  return poly(outline, "#CAC5D7") + polyStroke(outline, "#5E5870", .6) + line(root[0], root[1], tip[0], tip[1], "#7D7790", .6);
}

// 6 px strip under the title bar with each plate's frame pattern.
function band(u, width, height = 6) {
  const mid = height / 2;
  let s = rect(0, 0, width, height, u.groove), d = "";
  switch (u.kind) {
    case "Alexander": for (let x = 0; x < width; x += 5) s += rect(x, mid - 1.5, 2, 3, u.frameHigh, .5); break;
    case "Bahamut": for (let x = 0; x < width; x += 28) s += rect(x, mid - 1, 19, 2, u.frame) + rect(x + 21, mid - 1, 3, 2, u.active); break;
    case "Weapon":
      s += line(0, mid - 2, width, mid - 2, u.frame, 1) + line(0, mid + 2, width, mid + 2, u.frame, 1);
      for (let x = 0; x < width; x += 3.5) d += `M${f(x)} ${f(mid + 1)}L${f(x + 1.5)} ${f(mid - 1)}`;
      s += `<path d="${d}" ${strokeAttr(u.inlay, 1)}/>`;
      break;
    case "Dragonsong":
      s += line(0, height - 2, width, height - 2, u.frame, 1);
      for (let x = 4; x < width; x += 8) d += `M${f(x)} 0L${f(x)} ${f(height - 2)}`;
      s += `<path d="${d}" ${strokeAttr(u.frameHigh, 1, .7)}/>`;
      break;
    case "Omega": for (let x = 4; x < width; x += 9) s += circle(x, mid, 2, u.inlay, .25) + circle(x, mid, 1, u.inlay); break;
    case "Futures":
      s += `<rect x="0" y="${f(mid - .5)}" width="${f(width)}" height="1" fill="url(#fband)"/>`;
      for (let x = 8; x < width; x += 16) s += sparkle(x, mid, 2.2, u.inlay);
      return svg(width, height, s, hgrad("fband", [[0, ["#BE96FF", .6]], [.5, ["#FFA0C8", .6]], [1, ["#FFD28C", .6]]]));
  }
  return svg(width, height, s);
}

// Marker under a tab (here: under a sidebar item). 62 × 7, lit when selected.
function tabMark(u, selected) {
  const w = 62, h = 9, cx = w / 2, cy = h / 2, x0 = 0, x1 = w, y0 = cy - 3.5;
  let s = "", defs = "";
  switch (u.kind) {
    case "Alexander": {
      const cell = [6, 3], total = 8 * cell[0] + 7 + 2, mx = cx - total / 2, my = cy - cell[1] / 2 - 1;
      s += rect(mx, my, total, cell[1] + 2, "#070A09", 1, 2) + rectStroke(mx, my, total, cell[1] + 2, selected ? u.frameHigh : u.frame, 1, selected ? 1 : .45, 2);
      for (let i = 0; i < 8; i++) {
        const px = mx + 1 + i * (cell[0] + 1), py = my + 1;
        if (selected) s += rect(px - 1, py - 1, cell[0] + 2, cell[1] + 2, u.glow, .35);
        s += rect(px, py, cell[0], cell[1], selected ? u.inlay : "#29312C");
      }
      break;
    }
    case "Bahamut": {
      const c = selected ? u.active : u.frame, a = selected ? 1 : .55, step = x0 + w * .46;
      if (selected) s += line(x0, y0 + 1, step, y0 + 1, u.active, 3, .25);
      s += polyStroke([[x0, y0 + 1], [step, y0 + 1], [step, y0 + 5], [x1 - 6, y0 + 5]], c, 1, a, false);
      s += circle(x1 - 3, y0 + 5, 3, selected ? u.activeHigh : u.groove) + ring(x1 - 3, y0 + 5, 3, c, 1, a);
      break;
    }
    case "Weapon": {
      const c = selected ? u.active : u.inlay, a = selected ? 1 : .5;
      if (selected) s += rect(x0, y0 + 1.5, w, 4, u.active, .18, 2);
      let d = ""; for (let x = x0; x < x1; x += 3.5) d += `M${f(x)} ${f(y0 + 5)}L${f(x + 1.5)} ${f(y0 + 2)}`;
      s += `<path d="${d}" ${strokeAttr(c, 1.2, a)}/>`;
      break;
    }
    case "Dragonsong": {
      const a = selected ? 1 : .3;
      if (selected) s += rect(x0, cy - 2.5, w, 5, u.glow, .25, 2);
      defs = hgrad(`dsm${selected ? 1 : 0}`, [[0, ["#E2622A", a]], [.5, ["#C98A5A", a]], [1, ["#7FB6E8", a]]]);
      s += `<rect x="0" y="${f(cy - 1)}" width="${w}" height="2" fill="url(#dsm${selected ? 1 : 0})"/>` + diamond(cx, cy, 3, 3, selected ? u.activeHigh : u.frameHigh, selected ? 1 : .8);
      break;
    }
    case "Omega":
      for (let i = 0; i < 8; i++) { const px = cx - 24.5 + i * 7; if (selected) s += circle(px, cy, 3, u.inlay, .3); s += circle(px, cy, 1.6, selected ? u.inlay : "#2A2E3B"); }
      break;
    case "Futures": {
      const a = selected ? 1 : .25;
      if (selected) s += rect(x0, cy - 4, w, 8, "#E6AAFF", .22, 4);
      defs = hgrad(`ftm${selected ? 1 : 0}`, [[0, ["#B98CFF", a]], [.5, ["#FF9EC6", a]], [1, ["#FFD58A", a]]]);
      s += `<rect x="0" y="${f(cy - 2)}" width="${w}" height="4" fill="url(#ftm${selected ? 1 : 0})"/>` + line(x0, cy - 1.5, x1, cy - 1.5, "#FFFFFF", 1, .3 * a);
      break;
    }
  }
  return svg(w, h, s, defs);
}

// Running-state gauge: groove with six lamps (Alexander: bars), first lamps lit.
function gauge(u, running = true) {
  const bar = u.kind === "Alexander", cell = bar ? [3, 9] : [4, 4], gap = bar ? 2 : 3, pad = bar ? 3 : 4;
  const width = bar ? 34 : 47, gh = cell[1] + 4;
  const lamp = u.kind === "Omega" ? u.inlay : u.active;
  let s = rect(0, 0, width, gh, u.groove, 1, 4) + rectStroke(.5, .5, width - 1, gh - 1, u.frame, 1, .6, 4);
  [1, .78, .52, .3, .3, .3].forEach((strength, i) => {
    const st = running ? strength : .18, c = lerp(u.groove, strength > .9 ? lerp(lamp, u.activeHigh, .5) : lamp, st);
    const px = pad + i * (cell[0] + gap), py = 2;
    s += bar ? rect(px, py, cell[0], cell[1], c, 1, 1) : circle(px + cell[0] / 2, py + cell[1] / 2, cell[0] / 2, c);
  });
  return svg(width, gh, s);
}

// Fittings drawn over a decorated button (w × h); look: normal | open | danger.
function fittings(u, w, h, look = "normal") {
  const compact = h < 30, mid = h / 2, open = look === "open";
  const lamp = open ? u.activeHigh : u.frameHigh, la = open ? 1 : .7;
  let s = "";
  switch (u.kind) {
    case "Alexander": {
      const inset = compact ? 4 : 5, bw = compact ? 2 : 3, lc = open ? u.active : lamp;
      if (open) s += rect(3, inset - 1, bw + 2, h - 2 * inset + 2, lc, .35);
      s += rect(4, inset, bw + (open ? 1 : 0), h - 2 * inset, lc, la, 1) + rect(w - 4 - bw, inset, bw, h - 2 * inset, open ? u.frameHigh : lamp, open ? .55 : la, 1);
      break;
    }
    case "Bahamut": {
      s += line(3, 1, w - 3, 1, "#FFE296", 1, .4);
      const y = h - (compact ? 3 : 4), from = compact ? 9 : 12, to = w - (compact ? 9 : 12);
      if (open) s += line(from, y, to, y, u.active, 3, .35) + line(from, y, to, y, u.activeHigh, 1.5);
      else { let d = ""; for (let x = from; x < to; x += 15) d += `M${f(x)} ${f(y)}L${f(Math.min(to, x + 12))} ${f(y)}`; s += `<path d="${d}" ${strokeAttr(u.active, 1, .4)}/>`; }
      break;
    }
    case "Weapon": {
      const c = open ? u.active : u.inlay, y = compact ? 2 : 3;
      let d = ""; for (let x = 8; x < w - 8; x += 3.5) d += `M${f(x)} ${f(y + 2)}L${f(x + 1.5)} ${f(y)}`;
      s += `<path d="${d}" ${strokeAttr(c, 1, open ? 1 : .85)}/>`;
      break;
    }
    case "Dragonsong": {
      s += rectStroke(3, 3, w - 6, h - 6, open ? u.active : u.frameHigh, 1, open ? .45 : .32, Math.max(0, u.rounding - 2));
      const inset = compact ? 3 : 4, knob = compact ? 2 : 2.5;
      for (const x of [8, w - 8]) s += line(x, inset, x, h - inset, lamp, 1.4, la) + diamond(x, mid, knob, knob, lamp, la);
      break;
    }
    case "Omega": {
      const cx = compact ? 10 : 13, c = open ? u.activeHigh : "#3A3F50";
      const hex = (r) => Array.from({ length: 6 }, (_, i) => polar(cx, mid, r, i * Math.PI / 3));
      if (open) s += poly(hex(compact ? 5 : 7), u.active, .3);
      s += poly(hex(compact ? 3.5 : 4.6), c);
      break;
    }
    case "Futures": {
      const len = compact ? 8 : 11, lw = compact ? 2 : 2.5, c = open ? u.active : lamp;
      s += leaf(7, mid, len, lw, -.38, c, la) + leaf(w - 7, mid, len, lw, .38, c, la);
      break;
    }
  }
  return svg(w, h, s);
}
function leaf(x, y, length, width, angle, c, a) {
  const d = [Math.sin(angle), -Math.cos(angle)], n = [-d[1], d[0]], pts = [];
  for (let i = 0; i < 12; i++) {
    const t = i / 12 * Math.PI * 2, wv = width * (Math.cos(t) > 0 ? .8 : 1);
    pts.push([x + d[0] * Math.cos(t) * length * .5 + n[0] * Math.sin(t) * wv, y + d[1] * Math.cos(t) * length * .5 + n[1] * Math.sin(t) * wv]);
  }
  return poly(pts, c, a);
}

// Card mark on a card's left rim at the title line (10 × 10).
function cardMark(u) {
  const c = 6, r = 5;
  let s = "";
  switch (u.kind) {
    case "Alexander": s = circle(c, c, r, "#2C2619") + ring(c, c, r, u.frameHigh, 1) + line(c, c - 3.2, c, c + 3.2, "#D4AF62", .9) + line(c - 3.2, c, c + 3.2, c, "#D4AF62", .9); break;
    case "Bahamut": s = diamond(c, c, r, r, "#1A1212") + polyStroke([[c, c - r], [c + r, c], [c, c + r], [c - r, c]], u.frameHigh, 1) + line(c - 2.4, c, c + 2.4, c, u.active, 1.3); break;
    case "Weapon": s = sparkle(c, c, r * 1.1, u.frameHigh); break;
    case "Dragonsong": s = diamond(c, c, r * .8, r, "#26262C") + polyStroke([[c, c - r], [c + r * .8, c], [c, c + r], [c - r * .8, c]], u.frameHigh, .9) + diamond(c, c, 1.5, 2, u.active); break;
    case "Omega": {
      const hex = Array.from({ length: 6 }, (_, i) => polar(c, c, r, i * Math.PI / 3));
      s = poly(hex, "#1C1E27") + polyStroke(hex, u.frameHigh, .9) + circle(c, c, 1.5, u.inlay);
      break;
    }
    case "Futures": s = sparkle(c, c, r * 1.15, u.inlay) + circle(c, c, 1.4, u.active); break;
  }
  return svg(12, 12, s);
}

// Alexander: two clusters of luminous gears breaking out of the window's top-right and
// bottom-left corners (window at wx, wy, size ww × wh inside a stage of sw × sh).
function gears(sw, sh, wx, wy, ww, wh) {
  const gold = "#FFD46A", min = [wx, wy], max = [wx + ww, wy + wh], t = 9;
  const list = [
    [[max[0] - 26, min[1] + 12], 44, 1], [[max[0] + 28, min[1] - 8], 22, -1], [[max[0] + 22, min[1] + 54], 25, -1],
    [[min[0] + 6, max[1] - 12], 40, -1], [[min[0] - 30, max[1] - 56], 20, 1],
  ];
  let s = "";
  for (const cl of [[list[0][0][0] + 14, list[0][0][1] + 10], [list[3][0][0] - 10, list[3][0][1] - 12]])
    for (let i = 4; i >= 1; i--) s += circle(cl[0], cl[1], 34 + i * 16, gold, .028);
  for (const [[cx, cy], radius, dir] of list) {
    const angle = dir * t * Math.PI * 2 / (dir > 0 ? 80 : 56);
    const teeth = Math.max(14, Math.round(radius * .62)), depth = Math.max(3, radius * .12), step = Math.PI * 2 / teeth, out = [];
    for (let i = 0; i < teeth; i++) {
      const a = angle + i * step;
      out.push(polar(cx, cy, radius - depth, a + step * .06), polar(cx, cy, radius, a + step * .26), polar(cx, cy, radius, a + step * .54), polar(cx, cy, radius - depth, a + step * .74));
    }
    s += poly(out, gold, .14) + polyStroke(out, gold, 6, .12) + polyStroke(out, gold, 3.2, .22) + polyStroke(out, gold, 1.5, .95)
      + ring(cx, cy, radius * .58, gold, 1, .7);
    for (let i = 0; i < 28; i++) { const [x, y] = polar(cx, cy, radius * .72, angle + i * Math.PI * 2 / 28); s += circle(x, y, .8, gold, .55); }
    const [ax, ay] = polar(cx, cy, radius * .25, angle - .5), [bx, by] = polar(cx, cy, radius * .31, angle + 2.4);
    s += ring(ax, ay, radius * .26, gold, 1, .6) + ring(bx, by, radius * .18, gold, 1, .5) + circle(cx, cy, radius * .1, gold, .7);
  }
  for (const [[cx, cy], spread, seed] of [[[list[0][0][0] + 10, list[0][0][1] + 6], 70, 0], [[list[3][0][0] - 8, list[3][0][1] - 10], 60, 17]])
    for (let i = 0; i < 12; i++) {
      const n = i + seed, period = 3.5 + n % 5 * .8, phase = ((t / period + n * .37) % 1);
      const [x, y] = polar(cx, cy, spread * (.35 + (n * 37 % 100) / 160), n * 2.399);
      const px = x + Math.sin(phase * Math.PI * 2 + n) * 4, py = y - phase * 26, a = Math.sin(phase * Math.PI), size = 1 + n % 3 * .5;
      s += circle(px, py, size * 3, gold, .12 * a) + circle(px, py, size, "#FFF1C0", .9 * a);
    }
  return svg(sw, sh, s);
}

// Rain drops on the window glass (RainWindowRenderer fallback: same noise, same shapes).
function noise(index, salt) {
  let v = (Math.imul(index, 374761393) + Math.imul(salt, 668265263)) >>> 0;
  v = Math.imul(v ^ (v >>> 13), 1274126177) >>> 0;
  return (v & 0xffff) / 65535;
}
function rainDrops(w, h, count = 110, scale = 1.6) {
  let dark = "", mid = "", glint = "", trails = "";
  const ell = (x, y, rx, ry) => `M${f(x - rx)} ${f(y)}a${f(rx)} ${f(ry)} 0 1 0 ${f(rx * 2)} 0a${f(rx)} ${f(ry)} 0 1 0 ${f(-rx * 2)} 0`;
  for (let i = 0; i < count; i++) {
    const moving = i < 4, x = noise(i, 1) * w, y = (moving ? .2 + noise(i, 4) * .6 : noise(i, 2)) * h;
    const width = (moving ? 2.4 : .7 + noise(i, 5) * 1.8) * scale, height = width * (1.1 + noise(i, 6));
    dark += ell(x, y, width, height); mid += ell(x, y + height * .2, width * .65, height * .55);
    glint += ell(x - width * .3, y - height * .42, Math.max(.45, width * .23), Math.max(.45, width * .23));
    // A head running down leaves a narrow, fading wet path above it.
    if (moving) trails += `<rect x="${f(x - width * .35)}" y="${f(y - 120)}" width="${f(width * .7)}" height="120" fill="url(#trail)"/>`;
  }
  const defs = `<linearGradient id="trail" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#B9D3DD" stop-opacity="0"/><stop offset="1" stop-color="#B9D3DD" stop-opacity=".28"/></linearGradient>`;
  return svg(w, h, trails + `<path d="${dark}" fill="#04111A" fill-opacity=".75"/><path d="${mid}" fill="#5C7D8C" fill-opacity=".6"/><path d="${glint}" fill="#C7E0E8" fill-opacity=".72"/>`, defs);
}

// Backdrops standing in for the game scene behind translucent skins.
function scene(w, h, kind) {
  if (kind === "glass") return svg(w, h,
    `<rect width="${w}" height="${h}" fill="url(#sky)"/>` + circle(w * .78, h * .22, 160, "#FFE7A8", .55) + circle(w * .78, h * .22, 60, "#FFF6DA", .9)
    + `<path d="M0 ${h * .68}C${w * .2} ${h * .55} ${w * .35} ${h * .62} ${w * .5} ${h * .58}S${w * .8} ${h * .5} ${w} ${h * .6}V${h}H0Z" fill="#3E7D6A"/>`
    + `<path d="M0 ${h * .8}C${w * .25} ${h * .7} ${w * .55} ${h * .78} ${w} ${h * .72}V${h}H0Z" fill="#2B5B4C"/>`
    + circle(w * .18, h * .35, 90, "#9AD3FF", .35) + circle(w * .35, h * .85, 120, "#E9B36A", .25),
    `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5AA7E0"/><stop offset=".6" stop-color="#A9D8F2"/><stop offset="1" stop-color="#E7D7B2"/></linearGradient>`);
  if (kind === "rain") {
    let bokeh = "";
    for (let i = 0; i < 26; i++) bokeh += circle(noise(i, 11) * w, h * .35 + noise(i, 12) * h * .5, 10 + noise(i, 13) * 34, i % 3 ? "#F2C27A" : "#9CCAD9", .08 + noise(i, 14) * .14);
    return svg(w, h, `<rect width="${w}" height="${h}" fill="url(#mist)"/>` + bokeh,
      `<linearGradient id="mist" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2E4250"/><stop offset="1" stop-color="#16222B"/></linearGradient>`);
  }
  return svg(w, h, `<rect width="${w}" height="${h}" fill="url(#bg)"/>`,
    `<linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1A222C"/><stop offset="1" stop-color="#0E1218"/></linearGradient>`);
}

// Stand-in for the adventurer-plate backing read from the player's game files: both halves
// of the plate frame reach past the window edges and fade toward its centre.
function plate(u, sw, sh, wx, wy, ww, wh) {
  const fade = (id, flip) => `<linearGradient id="${id}" x1="${flip ? 1 : 0}" y1="0" x2="${flip ? 0 : 1}" y2="0"><stop offset="0" stop-color="${u.titleTop}" stop-opacity=".95"/><stop offset=".55" stop-color="${u.titleBottom}" stop-opacity=".6"/><stop offset="1" stop-color="${u.titleBottom}" stop-opacity="0"/></linearGradient>`;
  const top = wy - 40, height = wh + 80, half = ww * .5 + 60;
  let s = `<rect x="${wx - 60}" y="${top}" width="${half}" height="${height}" rx="18" fill="url(#pl)"/><rect x="${wx + ww * .5}" y="${top}" width="${half}" height="${height}" rx="18" fill="url(#pr)"/>`;
  s += `<path d="M${wx + ww * .35} ${top + .5}H${wx - 42}Q${wx - 59.5} ${top + .5} ${wx - 59.5} ${top + 18}V${top + height - 18}Q${wx - 59.5} ${top + height - .5} ${wx - 42} ${top + height - .5}H${wx + ww * .35}" ${strokeAttr(u.frame, 1.5, .8)}/>`;
  s += `<path d="M${wx + ww * .65} ${top + .5}H${wx + ww + 42}Q${wx + ww + 59.5} ${top + .5} ${wx + ww + 59.5} ${top + 18}V${top + height - 18}Q${wx + ww + 59.5} ${top + height - .5} ${wx + ww + 42} ${top + height - .5}H${wx + ww * .65}" ${strokeAttr(u.frame, 1.5, .8)}/>`;
  if (col(u.tintLeft)[1] > 0) s += circle(wx - 10, wy + wh * .3, 140, u.tintLeft, .9);
  if (col(u.tintRight)[1] > 0) s += circle(wx + ww + 10, wy + wh * .7, 140, u.tintRight, .9);
  return svg(sw, sh, s, fade("pl", false) + fade("pr", true));
}

// Miniature control centre in a skin for the skin manager: title row, sidebar, a card and a
// button, each in that skin's own style. No SVG text (Figma imports it unreliably); an
// undiscovered skin shows the mystery ring with a question mark instead.
const inner = markup => markup.replace(/^<svg[^>]*>/, "").replace(/<\/svg>$/, "").replace(/<defs>[\s\S]*?<\/defs>/, "");
function thumb(id, w = 120, h = 72, mystery = false) {
  const k = skin(id), P = k.palette, u = k.u, uid = "t" + id.replace(/[^a-z]/g, "") + Math.round(w);
  const R = Math.max(3, h * .07), th = h * .17, sw = w * .24, pad = w * .04;
  if (mystery) {
    const sc = h / 40;
    return svg(w, h, rect(0, 0, w, h, P.raised, 1, R) + ring(w / 2, h / 2, h * .29, P.border, 1.5) + ring(w / 2, h / 2, h * .37, P.border, 1, .45)
      + `<path transform="translate(${f(w / 2 - 8 * sc)} ${f(h / 2 - 8 * sc)}) scale(${f(sc)})" d="M6.2 6.2a1.9 1.9 0 1 1 2.6 1.8c-.5.2-.8.6-.8 1.1v.4M8 11.4v.1" ${strokeAttr(P.accent, 1.5)} stroke-linecap="round"/>`);
  }
  let defs = "", s = "";
  const vg = (gid, a, b) => `<linearGradient id="${gid}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${col(a)[0]}" stop-opacity="${col(a)[1]}"/><stop offset="1" stop-color="${col(b)[0]}" stop-opacity="${col(b)[1]}"/></linearGradient>`;
  // Window body.
  if (k.trait === "glass") {
    defs += vg(uid + "s", "#5AA7E0", "#E7D7B2");
    s += `<rect width="${f(w)}" height="${f(h)}" rx="${f(R)}" fill="url(#${uid}s)"/>` + circle(w * .8, h * .25, h * .3, "#FFF0C0", .7)
      + `<path d="M0 ${f(h * .7)}C${f(w * .3)} ${f(h * .55)} ${f(w * .6)} ${f(h * .75)} ${f(w)} ${f(h * .6)}V${f(h)}H0Z" fill="#3E7D6A"/>`
      + rect(.5, .5, w - 1, h - 1, "#FFFFFF", .55, R) + rectStroke(.5, .5, w - 1, h - 1, "#FFFFFF", 1, .85, R);
  } else if (k.trait === "light") {
    defs += vg(uid + "p", "#FBEBD0", "#F0D2A4");
    s += `<rect x=".5" y=".5" width="${f(w - 1)}" height="${f(h - 1)}" rx="${f(R)}" fill="url(#${uid}p)" stroke="#6E5232"/>`;
  } else {
    s += rect(0, 0, w, h, P.surface, 1, R) + rectStroke(.5, .5, w - 1, h - 1, P.border, 1, 1, R);
    if (k.trait === "rain") for (let i = 0; i < 16; i++) { const x = noise(i, 1) * w, y = noise(i, 2) * h, r = .6 + noise(i, 5) * 1.2; s += `<ellipse cx="${f(x)}" cy="${f(y)}" rx="${f(r)}" ry="${f(r * 1.4)}" fill="#04111A" fill-opacity=".7"/>` + circle(x - r * .3, y - r * .5, r * .3, "#C7E0E8", .8); }
  }
  // Title row.
  if (u) {
    defs += vg(uid + "t", u.titleTop, u.titleBottom);
    s += `<path d="M${f(R)} 0H${f(w - R)}Q${f(w)} 0 ${f(w)} ${f(R)}V${f(th)}H0V${f(R)}Q0 0 ${f(R)} 0Z" fill="url(#${uid}t)"/>` + rect(0, th, w, Math.max(1.5, h * .025), u.groove) + line(0, th + h * .0125, w, th + h * .0125, u.frame, Math.max(.6, h * .01));
    const e = th * 1.15;
    s += `<g transform="translate(${f(pad * .4)} ${f((th - e) / 2)})">${inner(emblem(u, e))}</g>`;
  } else s += circle(pad + th * .3, th / 2, th * .18, P.accent);
  s += rect(pad + th * .8, th * .39, w * .2, th * .22, P.text, .55, th * .11);
  // Sidebar with three items, the first one current.
  s += line(sw, th, sw, h, P.border, .8, .55);
  for (let i = 0; i < 3; i++) {
    const y = th + h * .07 + i * h * .13, ih = h * .1;
    if (i === 0) {
      if (k.trait === "light") { defs += vg(uid + "n", "#A47A49", "#6E4B28"); s += `<rect x="${f(pad * .6)}" y="${f(y)}" width="${f(sw - pad * 1.2)}" height="${f(ih)}" rx="${f(ih * .35)}" fill="url(#${uid}n)"/>`; }
      else s += rect(pad * .6, y, sw - pad * 1.2, ih, k.trait === "glass" ? ["#FFFFFF", .5] : P.hover, 1, ih * (k.trait === "glass" ? .5 : .3));
    }
    s += rect(pad * 1.4, y + ih * .38, sw * (i === 0 ? .5 : .42), ih * .24, i === 0 ? (k.trait === "light" ? "#FFFFFF" : P.accent) : P.muted, i === 0 ? 1 : .6, ih * .12);
  }
  // A card with three rows.
  const cx = sw + pad, cy = th + pad + (u ? h * .02 : 0), cw = w - sw - pad * 2, ch = h * .36;
  s += rect(cx, cy, cw, ch, k.trait === "rain" ? ["#000000", 0] : P.raised, 1, R * .8) + rectStroke(cx + .5, cy + .5, cw - 1, ch - 1, k.trait === "glass" ? ["#FFFFFF", .8] : P.border, .8, k.trait === "glass" ? 1 : .75, R * .8);
  for (let i = 0; i < 3; i++) {
    const ly = cy + ch * (.22 + i * .26);
    s += circle(cx + cw * .07, ly, ch * .055, i === 0 ? P.accent : P.muted) + rect(cx + cw * .13, ly - ch * .04, cw * (i === 0 ? .7 : .48), ch * .08, i === 0 ? P.accent : P.muted, i === 0 ? 1 : .55, ch * .04);
  }
  if (u) s += `<g transform="translate(${f(cx - 6 * h / 190 * 2)} ${f(cy + ch * .22 - 6 * h / 190 * 2)}) scale(${f(h / 190 * 2)})">${inner(cardMark(u))}</g>`;
  // The skin's button.
  const bx = cx, by = cy + ch + pad * 1.2, bw = cw * .42, bh = h * .13;
  if (u) {
    defs += vg(uid + "b", u.activeFace, u.activeFaceLow);
    s += `<rect x="${f(bx + .5)}" y="${f(by + .5)}" width="${f(bw - 1)}" height="${f(bh - 1)}" rx="${f(Math.min(u.rounding, bh / 2))}" fill="url(#${uid}b)" stroke="${u.activeRim}"/>`
      + `<g transform="translate(${f(bx)} ${f(by)}) scale(${f(bh / 30)})">${inner(fittings(u, bw * 30 / bh, 30, "open"))}</g>`;
  } else if (k.trait === "light") {
    defs += vg(uid + "g", "#C9A06A", "#7E552D");
    s += `<rect x="${f(bx + .5)}" y="${f(by + .5)}" width="${f(bw - 1)}" height="${f(bh - 1)}" rx="${f(bh / 2)}" fill="url(#${uid}g)" stroke="#5C3D1E"/>`;
  } else if (k.trait === "glass") s += rect(bx, by, bw, bh, "#FFFFFF", .6, bh / 2) + rectStroke(bx + .5, by + .5, bw - 1, bh - 1, "#FFFFFF", 1, .95, bh / 2);
  else if (k.trait === "default") s += rect(bx, by, bw, bh, lerp(col(P.hover)[0], col(P.accent)[0], .35), 1, bh * .2);
  else s += rect(bx, by, bw, bh, lerp(col(P.hover)[0], col(P.accent)[0], .3), 1, bh * .17) + rectStroke(bx + .5, by + .5, bw - 1, bh - 1, lerp(col(P.border)[0], col(P.accent)[0], .45), 1, 1, bh * .17);
  s += rect(bx + bw * .25, by + bh * .4, bw * .5, bh * .2, k.trait === "glass" ? P.text : "#FFFFFF", .85, bh * .1);
  return svg(w, h, s, defs);
}

module.exports = { ORDER, SLOTS, ULT, BASE, skin, all, lerp, emblem, band, tabMark, gauge, fittings, cardMark, gears, rainDrops, scene, plate, thumb, svg };
