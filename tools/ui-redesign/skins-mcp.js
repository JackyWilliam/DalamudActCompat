// Writes use_figma scripts that build one board per skin (section 05) from the redesigned
// 主页 board, each carrying its skin's own chrome instead of a recolour:
//   node tools/ui-redesign/skins-mcp.js <output-directory>
// skins-0.js also creates the "DACT 绝本配色" variable collection (six modes: one per
// ultimate; the base collection is capped at ten modes) and clears the old section.
"use strict";
const fs = require("fs");
const path = require("path");
const skins = require("./skins");

const outDir = process.argv[2];
fs.mkdirSync(outDir, { recursive: true });
const HOME_SECTION_ID = "3:24", SKIN_SECTION_ID = "15:850";

// skins.js runs inside the plugin too: fittings depend on button sizes known only there.
const library = fs.readFileSync(path.join(__dirname, "skins.js"), "utf8")
  .split("\n").filter(l => !/^\s*\/\//.test(l) && !l.startsWith("\"use strict\"") && !l.startsWith("module.exports")).map(l => l.trim()).filter(Boolean).join("\n");

// Ultimate tokens, bound where a control uses one plain colour.
const ULT_TOKENS = {
  frame: "frame", "frame-high": "frameHigh", face: "face", "face-low": "faceLow", groove: "groove", active: "active", "active-high": "activeHigh",
  "active-face": "activeFace", "active-face-low": "activeFaceLow", "active-rim": "activeRim", "active-ink": "activeInk", "title-top": "titleTop",
  "title-bottom": "titleBottom", inlay: "inlay",
};
const ultimateVars = `
let collB = (await figma.variables.getLocalVariableCollectionsAsync()).find(c => c.name === "DACT 绝本配色");
if (!collB) {
  collB = figma.variables.createVariableCollection("DACT 绝本配色");
  const ids = ${JSON.stringify(skins.ORDER.filter(id => id.startsWith("ultimate")))};
  collB.renameMode(collB.modes[0].modeId, skin(ids[0]).name);
  const modes = [collB.modes[0].modeId, ...ids.slice(1).map(id => collB.addMode(skin(id).name))];
  const tokens = ${JSON.stringify(ULT_TOKENS)};
  const defs = [...SLOTS.map(s => [s, k => k.palette[s][0], 1, s === "text" || s === "muted" ? ["TEXT_FILL", "STROKE_COLOR"] : ["FRAME_FILL", "SHAPE_FILL", "STROKE_COLOR"]]),
    ["primary", k => k.u.activeFace, 1, ["FRAME_FILL", "SHAPE_FILL"]], ["accent-soft", k => k.u.active, .14, ["FRAME_FILL", "SHAPE_FILL"]],
    ...Object.entries(tokens).map(([name, key]) => [name, k => k.u[key], 1, ["FRAME_FILL", "SHAPE_FILL", "STROKE_COLOR"]])];
  for (const [name, pick, alpha, scopes] of defs) {
    const v = figma.variables.createVariable("绝本/" + name, collB, "COLOR");
    ids.forEach((id, i) => { const h = pick(skin(id)); v.setValueForMode(modes[i], { r: parseInt(h.slice(1, 3), 16) / 255, g: parseInt(h.slice(3, 5), 16) / 255, b: parseInt(h.slice(5, 7), 16) / 255, a: alpha }); });
    v.scopes = scopes;
  }
}`;

const runtime = `
const SECTION = await figma.getNodeByIdAsync(${JSON.stringify(SKIN_SECTION_ID)});
const PAGE = (function p(n) { return n.type === "PAGE" ? n : p(n.parent); })(SECTION);
await figma.setCurrentPageAsync(PAGE);
await figma.loadFontAsync({ family: "Noto Sans SC", style: "Medium" });
const HOME = (await figma.getNodeByIdAsync(${JSON.stringify(HOME_SECTION_ID)})).children.find(n => n.type === "FRAME" && n.name === "主页");
const COLLS = await figma.variables.getLocalVariableCollectionsAsync();
const collA = COLLS.find(c => c.name === "DACT 皮肤配色");
const VAR = {}, VAR_BY_ID = {};
for (const v of await figma.variables.getLocalVariablesAsync()) { VAR[v.name] = v; VAR_BY_ID[v.id] = v; }
const rgb = h => ({ r: parseInt(h.slice(1, 3), 16) / 255, g: parseInt(h.slice(3, 5), 16) / 255, b: parseInt(h.slice(5, 7), 16) / 255 });
const C2 = c => Array.isArray(c) ? c : [c, 1];
const solid = (c, a = 1) => { const [h, o] = C2(c); return { type: "SOLID", color: rgb(h), opacity: o * a }; };
// Top-to-bottom gradient; Figma's identity transform runs left to right.
const vgrad = stops => ({ type: "GRADIENT_LINEAR", gradientTransform: [[0, 1, 0], [-1, 0, 1]], gradientStops: stops.map(([pos, c, a = 1]) => { const [h, o] = C2(c); return { position: pos, color: { ...rgb(h), a: o * a } }; }) });
const tok = (name, a = 1) => ({ ...figma.variables.setBoundVariableForPaint({ type: "SOLID", color: { r: 0, g: 0, b: 0 } }, "color", VAR["绝本/" + name]), opacity: a });
// A translucent paint list is assigned twice: Figma drops a bound paint's opacity on the first write.
const put = (n, key, paints) => { n[key] = paints; n[key] = paints; };
const isAuto = n => "layoutMode" in n && n.layoutMode !== "NONE";
function place(markup, parent, x, y, name, index) {
  const n = figma.createNodeFromSvg(markup); n.name = name;
  if (index === undefined) parent.appendChild(n); else parent.insertChild(index, n);
  if (x !== undefined) { if (isAuto(parent)) n.layoutPositioning = "ABSOLUTE"; n.x = x; n.y = y; }
  return n;
}
function round(n, r) { for (const k of ["topLeftRadius", "topRightRadius", "bottomLeftRadius", "bottomRightRadius"]) { if (n.boundVariables?.[k]) n.setBoundVariable(k, null); n[k] = r; } }
const shadow = (a, y = 1, color = { r: 1, g: 1, b: 1 }) => ({ type: "INNER_SHADOW", color: { ...color, a }, offset: { x: 0, y }, radius: 0, spread: 0, visible: true, blendMode: "NORMAL" });
// Detaches every component instance except icons, remembering which component each was.
async function detach(root) {
  const out = [];
  for (;;) {
    const inst = root.findOne(n => n.type === "INSTANCE" && !n.name.startsWith("Icon/"));
    if (!inst) return out;
    const main = await inst.getMainComponentAsync();
    const set = main.parent?.type === "COMPONENT_SET" ? main.parent.name : main.name;
    const props = { ...(inst.variantProperties || {}) };
    out.push({ node: inst.detachInstance(), set, props });
  }
}
// Ultimate boards read the 绝本 collection: same slot names under the 绝本/ prefix. Each bound
// paint also stores the variable's value for the board's mode as its base colour; new
// bindings otherwise keep the black placeholder, which some renderers show.
function rebind(root, modeId) {
  const target = name => name.startsWith("palette/") ? VAR["绝本/" + name.slice(8)] : name === "derived/primary" ? VAR["绝本/primary"] : name === "derived/accent-soft" ? VAR["绝本/accent-soft"] : name.startsWith("绝本/") ? VAR[name] : null;
  for (const n of [root, ...root.findAll(() => true)]) for (const key of ["fills", "strokes"]) {
    if (!(key in n) || n[key] === figma.mixed || !n[key].length) continue;
    let changed = false;
    const paints = n[key].map(p => {
      const v = p.boundVariables?.color && VAR_BY_ID[p.boundVariables.color.id], t = v && target(v.name);
      if (!t) return p;
      changed = true;
      const c = t.valuesByMode[modeId];
      const { boundVariables, ...plain } = p;
      return { ...figma.variables.setBoundVariableForPaint({ ...plain, color: { r: c.r, g: c.g, b: c.b } }, "color", t), opacity: p.opacity };
    });
    if (changed) put(n, key, paints);
  }
}
const byName = (root, prefix) => root.findAll(n => n.name.startsWith(prefix));
const ofSet = (kinds, set) => kinds.filter(k => k.set === set);
const lbl = (n, text, x, y, size = 22, color = "#D9DCE1") => { const t = figma.createText(); t.fontName = { family: "Noto Sans SC", style: "Medium" }; t.fontSize = size; t.characters = text; t.fills = [solid(color)]; n.appendChild(t); t.x = x; t.y = y; return t; };

async function board(id, x, y, note) {
  const k = skin(id), P = k.palette, hx = s => P[s][0];
  lbl(SECTION, k.name + (note ? " — " + note : ""), x, y - 52);
  const stage = figma.createFrame(); stage.name = "皮肤/" + k.name; SECTION.appendChild(stage);
  stage.resize(1100, 820); stage.x = x; stage.y = y; stage.clipsContent = true; stage.fills = [solid("#0E1218")];
  place(scene(1100, 820, k.trait === "glass" ? "glass" : k.trait === "rain" ? "rain" : "dark"), stage, 0, 0, "背景（示意游戏画面）");
  if (k.u) place(k.u.kind === "Alexander" ? gears(1100, 820, 90, 90, 920, 640) : plate(k.u, 1100, 820, 90, 90, 920, 640), stage, 0, 0, k.u.kind === "Alexander" ? "齿轮装饰" : "铭牌背景（示意）");
  const win = HOME.clone(); stage.appendChild(win); win.x = 90; win.y = 90; win.name = "DACT 控制中心 · " + k.name;
  const kinds = await detach(win);
  const collB = COLLS.find(c => c.name === "DACT 绝本配色"), modeB = k.u && collB.modes.find(m => m.name === k.name).modeId;
  if (k.u) win.setExplicitVariableModeForCollection(collB, modeB);
  else win.setExplicitVariableModeForCollection(collA, collA.modes.find(m => m.name === k.name).modeId);
  const header = win.children.find(n => n.name === "顶栏"), divider = win.children.find(n => n.name === "divider");
  const buttons = ofSet(kinds, "按钮 Button").filter(b => b.props["类型"] !== "文字");
  const fields = [...ofSet(kinds, "下拉 Select"), ...ofSet(kinds, "输入框 Input")].map(b => b.node);
  const navs = ofSet(kinds, "导航项 NavItem"), cards = byName(win, "卡片/"), tiles = byName(win, "状态卡/");

  if (k.trait === "solid" || k.trait === "rain") {
    // DactTheme.UsesSolidControls: buttons tint toward the accent and carry a fine rim.
    const rim = lerp(hx("border"), hx("accent"), .45);
    for (const b of buttons) {
      if (b.props["类型"] !== "危险") put(b.node, "fills", [solid(lerp(hx("hover"), hx("accent"), b.props["类型"] === "主要" ? .30 : .08))]);
      put(b.node, "strokes", [solid(rim)]); b.node.strokeWeight = 1; b.node.strokeAlign = "INSIDE"; round(b.node, 5);
    }
    for (const n of fields) { put(n, "strokes", [solid(hx("border"))]); round(n, 5); }
  }
  if (k.trait === "rain") {
    // Rain glass: a misty translucent pane with drops; child panes add no fill of their own.
    put(win, "fills", [solid(hx("surface"), .82)]); win.effects = [{ type: "BACKGROUND_BLUR", radius: 20, visible: true }];
    put(win, "strokes", [solid(hx("border"), .65)]); round(win, 11);
    place(rainDrops(920, 640), win, 0, 0, "雨滴（游戏内会折射并滑落）", 0);
    for (const n of [...cards, ...tiles]) put(n, "fills", []);
  }
  if (k.trait === "glass") {
    put(win, "fills", [solid("#FFFFFF", .56)]); win.effects = [{ type: "BACKGROUND_BLUR", radius: 18, visible: true }, shadow(.55)];
    put(win, "strokes", [vgrad([[0, "#FFFFFF", .75], [1, "#99C4F2", .2]])]); win.strokeWeight = 1.25; round(win, 14);
    for (const n of [...cards, ...tiles]) { put(n, "fills", [solid("#FFFFFF", .12)]); put(n, "strokes", [solid("#FFFFFF", .5)]); round(n, 12); n.effects = [shadow(.45)]; }
    for (const b of buttons) {
      const primary = b.props["类型"] === "主要";
      put(b.node, "fills", [vgrad(primary ? [[0, "#CFEAF8", .85], [1, "#8CC7EA", .5]] : [[0, "#FFFFFF", .6], [1, "#FFFFFF", .22]])]);
      put(b.node, "strokes", [vgrad([[0, "#FFFFFF", .95], [1, "#9CC0E0", .35]])]); b.node.strokeWeight = 1; round(b.node, b.node.height / 2);
    }
    for (const n of fields) { put(n, "fills", [solid("#FFFFFF", .25)]); put(n, "strokes", [solid("#FFFFFF", .6)]); round(n, 12); }
    for (const n of navs.filter(v => v.props["状态"] === "当前")) { put(n.node, "fills", [solid("#FFFFFF", .42)]); put(n.node, "strokes", [solid("#FFFFFF", .6)]); round(n.node, 17); }
  }
  if (k.trait === "light") {
    // Stand-ins for the player's Light-theme sprites: parchment window, ButtonA, TabButtonA, CircleButtons.
    put(win, "fills", [vgrad([[0, "#FBEBD0"], [1, "#F0D2A4"]])]); put(win, "strokes", [solid("#6E5232")]); win.strokeWeight = 1.5; round(win, 12);
    win.effects = [shadow(.7, 2, rgb("#FFF6E2"))];
    put(header, "fills", [vgrad([[0, "#E6C493"], [1, "#F3D8B0"]])]);
    const pill = [[0, "#C9A06A"], [.5, "#A87A45"], [1, "#7E552D"]];
    for (const b of buttons) {
      put(b.node, "fills", [vgrad(b.props["类型"] === "危险" ? [[0, "#B65A44"], [1, "#7A2D21"]] : pill)]);
      put(b.node, "strokes", [solid("#5C3D1E")]); b.node.strokeWeight = 1; round(b.node, b.node.height / 2); b.node.effects = [shadow(.35)];
      for (const t of b.node.findAll(n => n.type === "TEXT")) { put(t, "fills", [solid("#FFFFFF")]); t.effects = [{ type: "DROP_SHADOW", color: { r: .12, g: .08, b: .04, a: .8 }, offset: { x: 0, y: 1 }, radius: 0, spread: 0, visible: true, blendMode: "NORMAL" }]; }
      for (const v of b.node.findAll(n => n.type === "VECTOR")) put(v, "strokes", [solid("#FFFFFF")]);
    }
    for (const b of ofSet(kinds, "图标按钮 IconButton")) {
      put(b.node, "fills", [vgrad(pill)]); put(b.node, "strokes", [solid("#5C3D1E")]); b.node.strokeWeight = 1; round(b.node, 14);
      for (const v of b.node.findAll(n => n.type === "VECTOR")) put(v, "strokes", [solid("#FFFFFF")]);
    }
    for (const n of navs.filter(v => v.props["状态"] === "当前")) {
      put(n.node, "fills", [vgrad([[0, "#A47A49"], [1, "#6E4B28"]])]); put(n.node, "strokes", [solid("#4E3218")]); n.node.strokeWeight = 1; round(n.node, 9);
      for (const t of n.node.findAll(x => x.type === "TEXT")) put(t, "fills", [solid("#FFFFFF")]);
      for (const v of n.node.findAll(x => x.type === "VECTOR")) put(v, "strokes", [solid("#FFE7B8")]);
    }
    for (const n of fields) { put(n, "strokes", [solid(hx("border"))]); round(n, 12); }
    for (const n of [...cards, ...tiles]) round(n, 12);
  }
  if (k.u) {
    const u = k.u;
    // Title bar: plate gradient, colour tints from both ends, then the frame band.
    put(header, "fills", [vgrad([[0, u.titleTop], [1, u.titleBottom]])]);
    place(svg(920, 48, '<rect width="460" height="48" fill="url(#tl)"/><rect x="460" width="460" height="48" fill="url(#tr)"/><path d="M8 .5H912" stroke="#FFFFFF" stroke-opacity=".09"/>',
      '<linearGradient id="tl" x1="0" x2="1" y1="0" y2="0"><stop offset="0" stop-color="' + C2(u.tintLeft)[0] + '" stop-opacity="' + C2(u.tintLeft)[1] + '"/><stop offset="1" stop-color="' + C2(u.tintLeft)[0] + '" stop-opacity="0"/></linearGradient>' +
      '<linearGradient id="tr" x1="0" x2="1" y1="0" y2="0"><stop offset="0" stop-color="' + C2(u.tintRight)[0] + '" stop-opacity="0"/><stop offset="1" stop-color="' + C2(u.tintRight)[0] + '" stop-opacity="' + C2(u.tintRight)[1] + '"/></linearGradient>'), header, 0, 0, "标题色晕", 0);
    const at = win.children.indexOf(divider); divider.remove();
    place(band(u, 920), win, undefined, undefined, "花纹带", at);
    place(emblem(u, 36), stage, 72, 96, "徽记");
    const status = win.findOne(n => n.name === "解析器状态");
    if (status) { const dot = status.children.find(n => n.name === "dot"); if (dot) dot.remove(); place(gauge(u), status, undefined, undefined, "运行灯", 0); }
    for (const n of navs) place(tabMark(u, n.props["状态"] === "当前"), n.node, 36, 25, "标签标记");
    for (const b of buttons) {
      const look = b.props["类型"] === "主要" ? "open" : b.props["类型"] === "危险" ? "danger" : "normal";
      const [top, low, rim] = look === "open" ? [u.activeFace, u.activeFaceLow, u.activeRim] : look === "danger" ? ["#6A2828", "#521E1E", "#A9584F"] : [u.face, u.faceLow, u.frame];
      put(b.node, "fills", [vgrad([[0, top], [1, low]])]); put(b.node, "strokes", [solid(rim)]); b.node.strokeWeight = 1; b.node.strokeAlign = "INSIDE";
      round(b.node, Math.min(u.rounding, b.node.height / 2)); b.node.effects = [shadow(u.kind === "Futures" ? .22 : .08)];
      if (look !== "danger") place(fittings(u, b.node.width, b.node.height, look), b.node, 0, 0, "按钮配件", 0);
    }
    for (const n of fields) { put(n, "fills", [tok("groove")]); put(n, "strokes", [tok("frame", .8)]); }
    for (const t of ofSet(kinds, "开关 Toggle")) {
      const on = t.props["状态"] === "开", knob = t.node.children[0];
      put(t.node, "fills", [on ? tok("active") : tok("groove")]); put(t.node, "strokes", [on ? tok("active-rim") : tok("frame", .8)]); t.node.strokeWeight = 1;
      if (knob) put(knob, "fills", [on ? tok("active-ink") : solid(u.muted)]);
    }
    for (const c of ofSet(kinds, "顶栏状态 HeaderChip")) { put(c.node, "fills", [tok("groove")]); put(c.node, "strokes", [tok("frame", .9)]); c.node.strokeWeight = 1; }
    for (const c of cards) place(cardMark(u), c, -6, 17, "卡片标记");
    rebind(win, modeB);
  }
  return { id, stage: stage.id };
}
`;

const groups = [
  { ids: ["default", "eorzea", "liquid-glass", "rainy-window", "obsidian"], row: 0 },
  { ids: ["jade", "amethyst", "amber", "neon-pink"], row: 1 },
  { ids: skins.ORDER.filter(id => id.startsWith("ultimate")), row: 2 },
];
const NOTES = {
  default: "所有用户可用的深色界面",
  eorzea: "游戏内用本机 Light 主题贴图（羊皮纸窗口、ButtonA、TabButtonA、圆形按钮），不随包分发；此处为矢量示意",
  "liquid-glass": "乳白半透玻璃 + 深色文字；游戏内实时折射背后的游戏画面",
  "rainy-window": "灰蓝雨窗；游戏内水珠折射背景并缓慢滑落",
  obsidian: "纯黑底色，交互态才提亮为灰",
  jade: "探索配色：实心描边按钮", amethyst: "探索配色：实心描边按钮", amber: "探索配色：实心描边按钮", "neon-pink": "探索配色：实心描边按钮",
};
for (const id of skins.ORDER.filter(i => i.startsWith("ultimate")))
  NOTES[id] = skins.skin(id).u.kind === "Alexander" ? "标题渐变、花纹带、徽记、运行灯、标签标记、按钮配件、卡片标记、发光齿轮" : "标题渐变、花纹带、徽记、运行灯、标签标记、按钮配件、卡片标记；铭牌背景游戏内读取本机文件";
// skin-thumbs.js: draws every "svg:thumb|<id>|<w>|<h>|<mystery>" placeholder that board
// scripts left on the draft page (see figma-core.js), in place and at the same size.
const thumbs = `${library}
const page = await figma.getNodeByIdAsync("0:1");
await figma.setCurrentPageAsync(page);
const done = [];
for (const holder of page.findAll(n => n.type === "FRAME" && n.name.startsWith("svg:thumb|"))) {
  const [, id, w, h, mystery] = holder.name.slice(4).split("|");
  const art = figma.createNodeFromSvg(thumb(id, +w, +h, mystery === "1"));
  art.name = holder.parent.name.startsWith("皮肤卡/") ? "缩略图" : "大预览";
  const parent = holder.parent;
  parent.insertChild(parent.children.indexOf(holder), art);
  holder.remove();
  done.push(id);
}
return done;
`;
fs.writeFileSync(path.join(outDir, "skin-thumbs.js"), thumbs);
console.log(`skin-thumbs.js ${thumbs.length} chars`);

const jobs = [];
for (const g of groups) g.ids.forEach((id, col) => jobs.push({ id, x: 60 + col * 1180, y: 120 + g.row * 1000 }));
// Two or three boards per call keep each script under the 50k limit.
const chunks = [jobs.slice(0, 3), jobs.slice(3, 6), jobs.slice(6, 9), jobs.slice(9, 12), jobs.slice(12, 15)];
chunks.forEach((chunk, i) => {
  const head = i === 0 ? `
// First call: clear the old recolour-only boards and rename the section.
for (const n of [...SECTION.children]) n.remove();
SECTION.name = "05 皮肤（各自特色）";
SECTION.resizeWithoutConstraints(60 + 6 * 1180, 120 + 3 * 1000);` : "";
  // The collection is created before the runtime indexes variables and collections.
  const code = `${library}\n${i === 0 ? ultimateVars : ""}\n${runtime}${head}\nconst out = [];\n${chunk.map(j => `out.push(await board(${JSON.stringify(j.id)}, ${j.x}, ${j.y}, ${JSON.stringify(NOTES[j.id])}));`).join("\n")}\nreturn out;\n`;
  fs.writeFileSync(path.join(outDir, `skins-${i}.js`), code);
  console.log(`skins-${i}.js ${code.length} chars: ${chunk.map(j => j.id).join(", ")}`);
});
