// Splits the redesign into use_figma scripts for the Figma MCP (max 50k characters each):
//   node tools/ui-redesign/figma-mcp.js <config.json> <output-directory>
// config.json: { draftPageId, libPageId, compSectionId, iconSectionId, docSectionId, logoHash,
//                sectionIds: { "<screens.js section name>": "<draft section id>" } }
// Writes library.js (icons + component sets with variants and properties), then one script per
// board. Run them one after another: Figma state changes must stay sequential.
"use strict";
const fs = require("fs");
const path = require("path");
const design = require("./screens");
const icons = require("./icons");
const { LH } = require("./dsl");

const [configPath, outDir] = process.argv.slice(2);
const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
fs.mkdirSync(outDir, { recursive: true });

// Palette colours become bound variables; anything else stays a raw colour.
const TOKENS = {
  "#090C11": "palette/surface", "#12181F": "palette/raised", "#1B252F": "palette/hover", "#C7A85C": "palette/gold",
  "#6BC7F5": "palette/accent", "#FFFFFF": "palette/text", "#A6B3C2": "palette/muted", "#35406B": "palette/border",
  "#1C4A61": "derived/primary", "#E06C6C": "derived/danger", "#4A1F23": "derived/danger-fill",
  "#F3B6B6": "derived/danger-text", "#D9534F": "derived/badge",
};
// Translucent tokens whose alpha lives in the variable itself (paint stays 100%). Figma drops a
// bound paint's own opacity when a nested instance switches variant, so selected rows need this.
const ALPHA_TOKENS = { "#6BC7F5@0.14": "derived/accent-soft" };
function color(value) {
  if (!value) return null;
  let r, g, b, a = 1;
  if (value[0] === "#") { const n = parseInt(value.slice(1), 16); [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255]; }
  else { const parts = value.replace(/rgba?\(|\)/g, "").split(",").map(Number); [r, g, b] = parts; if (parts.length > 3) a = parts[3]; }
  const hex = "#" + [r, g, b].map(v => v.toString(16).padStart(2, "0")).join("").toUpperCase();
  if (ALPHA_TOKENS[`${hex}@${a}`]) return { v: ALPHA_TOKENS[`${hex}@${a}`], a: 1 };
  return TOKENS[hex] ? { v: TOKENS[hex], a } : { r: +(r / 255).toFixed(4), g: +(g / 255).toFixed(4), b: +(b / 255).toFixed(4), a };
}
// Drops defaults so each board fits one call, and converts colours.
function compact(n) {
  const o = {};
  for (const [k, v] of Object.entries(n)) {
    if (k === "children") { if (v.length) o.children = v.map(compact); continue; }
    if (v === null || v === undefined || v === false) continue;
    if ((k === "gap" || k === "pad") && v === 0) continue;
    if ((k === "w" || k === "h") && v === "hug") continue;
    if (k === "layout" && v === "col") continue;
    if (k === "opacity" && v === 1) continue;
    if (k === "margin" || k === "disabled" || k === "lh") continue;
    // Referenced vector art is redrawn in the plugin, keeping board scripts under the size limit.
    if (k === "markup" && n.ref) continue;
    if (k === "bg" || k === "color") { o[k] = color(v); continue; }
    if (k === "border") { o.border = { c: color(v.c), w: v.w }; continue; }
    o[k] = v;
  }
  if (n.t === "text") o.lh = LH[n.size];
  // Board instances name their variant so nested rows can switch (see sync() in figma-core.js).
  // groups is filled only after the library specs are collected, so library masters skip this.
  if (n.comp && groups.size) {
    const { base, props } = variantOf(n.comp);
    if (groups.get(base)?.variants.length > 1 && Object.keys(props).length) o.vp = props;
  }
  return o;
}

// ---- Component key (screens.js) -> component set + variant properties.
const SETS = {
  Button: ["按钮 Button", "主要/次要/危险/文字四种；文字用「文字」属性，图标用「图标」属性替换。高 30、圆角 6。ImGui：DactTheme 按钮色 + 自绘线性图标。"],
  IconButton: ["图标按钮 IconButton", "28×28 图标按钮；「图标」属性替换图标，「底色」变体决定是否有 Hover 底。"],
  Toggle: ["开关 Toggle", "34×20 开关，替代 ImGui 勾选框用于设置项；开 = 冰蓝底。"],
  Checkbox: ["勾选框 Checkbox", "18×18，只用于需要勾选语义的场景（如记住登录）。"],
  Select: ["下拉 Select", "高 30，内嵌 Surface 底 + 16% Muted 描边；对应 ImGui Combo。「展开」状态为冰蓝描边和冰蓝箭头（箭头不翻转，与 ImGui Combo 一致），下方接「下拉菜单」浮层。"],
  MenuItem: ["下拉菜单项 MenuItem", "下拉菜单里的一行；悬停 = Hover 底，选中 = 14% 冰蓝底 + 冰蓝文字 + 勾。对应 ImGui Selectable。"],
  // The arrow shows the state, so this set gets no 图标 swap property (see NO_ICON_PROPERTY).
  SponsorBadge: ["赞助标识 SponsorBadge", "赞助者标识：服务器授权的等级 Lv.1–10；深棕底 + 金边 + 插件自带王冠，颜色不随皮肤变化。"],
  AdminBadge: ["管理员标识 AdminBadge", "管理员标识（插件自带 Player26 图标），排在赞助标识之后。"],
  IdentityName: ["身份名 IdentityName", "名字 + 身份标识（AccountIdentityBadge）：赞助者名字为赞助红 #F53847（不随皮肤）。"],
  Disclosure: ["折叠说明 Disclosure", "冰蓝文字 = 可点击；收起为右箭头，展开为下箭头，下方接内嵌说明面板（Surface 底、序号圆点）。对应 ImGui CollapsingHeader，如「如何创建 FFLogs API Client」。"],
  DropdownMenu: ["下拉菜单 DropdownMenu", "下拉框展开后的浮层：Raised 底、Border 描边、8 px 圆角、浮层阴影样式；示例选项取自游戏解析范围。对应 ImGui Combo 弹出窗口（PopupBg）。"],
  Input: ["输入框 Input", "高 30；「尾部图标」变体用于显示密码等操作。"],
  Segmented: ["分段选择 Segmented", "2–3 项互斥选择，当前项为 Hover 底。"],
  Tabs: ["标签页 Tabs", "下划线标签，当前项冰蓝指示条。"],
  Pill: ["状态胶囊 Pill", "正常 = 冰蓝、提醒 = 金、关闭 = 灰、错误 = 红；可带状态圆点。"],
  Badge: ["未读角标 Badge", "红底未读数。"],
  NavItem: ["导航项 NavItem", "侧栏导航；当前项 Hover 底 + 冰蓝图标；可带未读角标。"],
  HeaderChip: ["顶栏状态 HeaderChip", "顶栏右侧的云同步状态胶囊。"],
};
const NO_ICON_PROPERTY = [SETS.Disclosure[0]];
function variantOf(key) {
  const [head, ...rest] = key.split("/");
  const base = head.split("+")[0], tail = rest.join("/");
  const yes = flag => flag ? "有" : "无";
  switch (base) {
    case "Button": { const [kind, extra] = tail.split("+"); return { base, props: { 类型: { primary: "主要", secondary: "次要", danger: "危险", ghost: "文字" }[kind], 图标: yes(extra) } }; }
    case "IconButton": return { base, props: { 底色: yes(tail === "filled") } };
    case "Toggle": case "Checkbox": return { base, props: { 状态: tail === "On" ? "开" : "关" } };
    case "Input": return { base, props: { 尾部图标: yes(head.includes("+icon")) } };
    case "Select": return { base, props: { 状态: tail === "open" ? "展开" : "默认" } };
    case "MenuItem": return { base, props: { 状态: { normal: "普通", hover: "悬停", selected: "选中" }[tail] } };
    case "Disclosure": return { base, props: { 状态: tail === "open" ? "展开" : "收起" } };
    case "IdentityName": return { base, props: { 身份: { plain: "普通", sponsor: "赞助", admin: "管理员", "sponsor+admin": "赞助+管理员" }[tail] } };
    case "Segmented": return { base, props: { 项数: tail.split("/")[0] } };
    case "Pill": { const [tone, dot] = tail.split("+"); return { base, props: { 语气: { ok: "正常", warn: "提醒", off: "关闭", error: "错误" }[tone], 圆点: yes(dot) } }; }
    case "NavItem": { const [state, extra] = tail.split("+"); return { base, props: { 状态: state === "active" ? "当前" : "普通", 未读: yes(extra) } }; }
    default: return { base, props: {} };
  }
}

const comps = new Map(), iconNames = new Set();
const groups = new Map();
function collect(n) {
  if (n.comp && !comps.has(n.comp)) comps.set(n.comp, compact(n));
  if (n.t === "icon") iconNames.add(n.name);
  (n.children || []).forEach(collect);
}
// Library-only samples come first so every variant exists and the DropdownMenu main component
// holds the longest option list (board uses hide surplus rows).
(design.components || []).forEach(collect);
design.sections.forEach(s => s.screens.forEach(b => collect(b.tree)));
const variantKeys = {};
for (const [key, spec] of comps) {
  const { base, props } = variantOf(key);
  const [setName, description] = SETS[base];
  if (!groups.has(base)) groups.set(base, { set: setName, description, variants: [] });
  const name = Object.entries(props).map(([k, v]) => `${k}=${v}`).join(", ");
  groups.get(base).variants.push({ key, name, spec });
}
for (const g of groups.values()) {
  // Several spec keys can share one Figma variant (Segmented/3/0 and /3/1 differ only in which
  // item is active, which sync() repaints), so each variant name lists every key it serves.
  for (const v of g.variants) {
    const name = g.variants.length > 1 ? `${g.set}|${v.name}` : `${g.set}|`;
    variantKeys[name] = [...(variantKeys[name] || []), v.key];
  }
}

const core = fs.readFileSync(path.join(__dirname, "figma-core.js"), "utf8")
  .split("\n").map(l => l.trim()).filter(l => l && !l.startsWith("//")).join("\n");
const prelude = (pageId, withIcons) =>
  `const PAGE_ID=${JSON.stringify(pageId)};const LIB_PAGE_ID=${JSON.stringify(config.libPageId)};` +
  `const COMP_SECTION_ID=${JSON.stringify(config.compSectionId)};const ICON_SECTION_ID=${JSON.stringify(config.iconSectionId)};` +
  `const LOGO_HASH=${JSON.stringify(config.logoHash)};const IMAGE_HASHES=${JSON.stringify(config.imageHashes || {})};const ICONS=${withIcons ? JSON.stringify(icons) : "{}"};` +
  `const VARIANT_KEYS=${JSON.stringify(variantKeys)};\n${core}\n`;

// ---- library.js: icons, then every component set with TEXT and INSTANCE_SWAP properties.
const groupList = [...groups.values()].map(g => ({ set: g.set, description: g.description, variants: g.variants.map(v => ({ name: v.name, key: v.key, spec: v.spec })) }));
const library = prelude(config.libPageId, true) + `
for (const name of ${JSON.stringify([...iconNames].sort())}) iconMaster(name);
const directTexts = n => { const out = []; const walk = x => { for (const c of x.children || []) { if (c.type === "INSTANCE") continue; if (c.type === "TEXT") out.push(c); else walk(c); } }; walk(n); return out; };
const directIcons = n => { const out = []; const walk = x => { for (const c of x.children || []) { if (c.type === "INSTANCE") { if (c.name.startsWith("Icon/")) out.push(c); continue; } walk(c); } }; walk(n); return out; };
const report = [];
for (const g of ${JSON.stringify(groupList)}) {
  if (compSection.children.some(n => n.name === g.set)) continue;
  const comps = [];
  // Register each new main component at once: later sets nest earlier ones (NavItem uses Badge).
  for (const v of g.variants) { const c = await build(v.spec, compSection, null, true); c.name = v.name || g.set; comps.push(c); MASTERS.set(v.key, c); }
  let owner;
  if (comps.length > 1) {
    owner = figma.combineAsVariants(comps, compSection);
    owner.layoutMode = "HORIZONTAL"; owner.itemSpacing = 16; owner.counterAxisAlignItems = "CENTER";
    owner.paddingTop = owner.paddingBottom = owner.paddingLeft = owner.paddingRight = 16;
    owner.layoutSizingHorizontal = "HUG"; owner.layoutSizingVertical = "HUG";
  } else owner = comps[0];
  owner.name = g.set; owner.description = g.description;
  const variants = comps;
  const textCount = Math.max(...variants.map(v => directTexts(v).length));
  const textKeys = [];
  for (let i = 0; i < textCount; i++) {
    const sample = directTexts(variants.find(v => directTexts(v).length > i))[i];
    textKeys.push(owner.addComponentProperty(textCount === 1 ? "文字" : \`选项 \${i + 1}\`, "TEXT", sample.characters));
  }
  for (const v of variants) directTexts(v).forEach((t, i) => { t.componentPropertyReferences = { characters: textKeys[i] }; });
  const firstIcon = variants.map(directIcons).find(list => list.length);
  // Binding an INSTANCE_SWAP property resets every variant to one icon, which would erase a
  // per-state arrow.
  if (firstIcon && !${JSON.stringify(NO_ICON_PROPERTY)}.includes(g.set)) {
    const key = owner.addComponentProperty("图标", "INSTANCE_SWAP", ICON_MASTERS.get(firstIcon[0].name).id);
    for (const v of variants) for (const ic of directIcons(v)) ic.componentPropertyReferences = { mainComponent: key };
  }
  report.push({ set: g.set, id: owner.id, variants: variants.length, texts: textCount, icon: !!firstIcon });
}
// Grid layout for both sections.
const grid = (section, nodes, maxWidth, gap) => {
  let x = 60, y = 80, rowH = 0;
  for (const n of nodes) {
    if (x > 60 && x + n.width > maxWidth) { x = 60; y += rowH + gap; rowH = 0; }
    n.x = x; n.y = y; x += n.width + gap; rowH = Math.max(rowH, n.height);
  }
  section.resizeWithoutConstraints(Math.max(maxWidth + 60, 800), y + rowH + 80);
};
grid(iconSection, iconSection.children.filter(n => n.type === "COMPONENT"), 900, 32);
grid(compSection, compSection.children.filter(n => n.type === "COMPONENT_SET" || n.type === "COMPONENT"), 1640, 60);
// The component section grows as sets are added; keep the icon section below it.
iconSection.y = compSection.y + compSection.height + 120;
return { report, icons: ICON_MASTERS.size, createdIds: created };
`;
fs.writeFileSync(path.join(outDir, "library.js"), library);

// ---- One script per board, built in its draft (or library) section at a fixed x.
const files = [];
for (const section of design.sections) {
  const target = config.sectionIds[section.name];
  if (!target) throw new Error(`No Figma section for ${section.name}`);
  let x = 60;
  section.screens.forEach((board, bi) => {
    const label = board.note ? `${board.name} — ${board.note}` : board.name;
    const code = prelude(target.page, false) + `
const sec = await figma.getNodeByIdAsync(${JSON.stringify(target.id)});
const label = figma.createText(); label.fontName = FONT; label.fontSize = 22; label.characters = ${JSON.stringify(label)};
label.fills = [{ type: "SOLID", color: { r: 0.85, g: 0.86, b: 0.88 } }];
sec.fills = [{ type: "SOLID", color: { r: 0.165, g: 0.176, b: 0.192 } }];
sec.appendChild(label); label.x = ${x}; label.y = 40;
const board = await build(${JSON.stringify(compact(board.tree))}, sec, null, false);
board.name = ${JSON.stringify(board.name)}; board.x = ${x}; board.y = 100;
sec.resizeWithoutConstraints(Math.max(sec.width, ${x} + board.width + 60), Math.max(sec.height, board.height + 160));
return { boardId: board.id, labelId: label.id, w: board.width, h: board.height, createdIds: created };
`;
    const file = `board-${files.length.toString().padStart(2, "0")}.js`;
    fs.writeFileSync(path.join(outDir, file), code);
    files.push({ file, section: section.name, board: board.name, chars: code.length });
    x += board.tree.w + 120;
  });
}
// Batches: consecutive boards on the same page share one call (and one copy of the runtime)
// while the script stays under the MCP's 50k-character limit.
const batches = [];
for (const f of files) {
  const code = fs.readFileSync(path.join(outDir, f.file), "utf8");
  const cut = code.indexOf("\nconst sec = ");
  const body = code.slice(cut).replace(/\nreturn \{ boardId[^\n]*\n?$/, "\n");
  const last = batches[batches.length - 1];
  const head = code.slice(0, cut);
  if (last && last.head === head && last.size + body.length < 47000) { last.bodies.push(body); last.boards.push(f.board); last.size += body.length; }
  else batches.push({ head, bodies: [body], boards: [f.board], size: head.length + body.length });
}
batches.forEach((b, i) => {
  // Each board runs in its own block so the per-board consts do not collide.
  const parts = b.bodies.map((body, j) => `{${body}results.push({ board: ${JSON.stringify(b.boards[j])}, id: board.id, w: board.width, h: board.height });\n}`);
  fs.writeFileSync(path.join(outDir, `batch-${i}.js`), `${b.head}\nconst results = [];\n${parts.join("\n")}\nreturn { results, createdIds: created };\n`);
  console.log(`batch-${i}.js ${b.size} chars: ${b.boards.join(", ")}`);
});
// ---- Packed batches (pack-N.js): the same boards, but JSON fragments that repeat inside a batch
// (whole subtrees, text-node styling, frame headers) move into a dictionary the script expands
// again. Specs are dominated by such repeats (nav items, bullets, cards), so one call carries
// roughly twice as many boards and far fewer characters have to be sent through the MCP.
const SEP = "¦", TOKEN = /§(\d+)§/g;
function packSpecs(specs) {
  const counts = new Map();
  const add = c => { if (c.length > 24) counts.set(c, (counts.get(c) || 0) + 1); };
  const walk = n => {
    if (Array.isArray(n)) return n.forEach(walk);
    if (!n || typeof n !== "object") return;
    const json = JSON.stringify(n);
    add(json);
    if (n.t === "text") { const v = JSON.stringify(n.text); add(json.slice(json.indexOf(v) + v.length)); }
    if (n.children) add(json.slice(0, json.indexOf('"children":[') + 12));
    Object.values(n).forEach(walk);
  };
  specs.forEach(walk);
  let joined = specs.map(s => JSON.stringify(s)).join("\u0000");
  const dict = [];
  for (const c of [...counts.keys()].sort((a, b) => b.length - a.length)) {
    const n = joined.split(c).length - 1 + dict.reduce((sum, d) => sum + d.split(c).length - 1, 0);
    const token = `§${dict.length}§`;
    if (n < 2 || (n - 1) * c.length <= n * token.length + 4) continue;
    joined = joined.split(c).join(token);
    for (let i = 0; i < dict.length; i++) dict[i] = dict[i].split(c).join(token);
    dict.push(c);
  }
  const texts = joined.split("\u0000");
  // String.raw keeps JSON escapes intact; the separators and tokens never occur in the specs.
  for (const s of [...texts, ...dict]) if (/[`¦]|\$\{/.test(s)) throw new Error("spec text cannot be packed into String.raw");
  return { dict, texts };
}
// PACK_ONLY="section or board|..." packs just those (new boards next to ones the user edited).
const packOnly = process.env.PACK_ONLY ? process.env.PACK_ONLY.split("|") : null;
const packItems = [];
for (const section of design.sections) {
  const target = config.sectionIds[section.name];
  let x = 60;
  for (const board of section.screens) {
    if (!packOnly || packOnly.includes(section.name) || packOnly.includes(board.name)) packItems.push({ section: section.name, target, x, name: board.name, label: board.note ? `${board.name} — ${board.note}` : board.name, spec: compact(board.tree) });
    x += board.tree.w + 120;
  }
}
function packScript(items) {
  const { dict, texts } = packSpecs(items.map(i => i.spec));
  const blocks = items.map((it, j) => `{
const sec = await figma.getNodeByIdAsync(${JSON.stringify(it.target.id)});
const label = figma.createText(); label.fontName = FONT; label.fontSize = 22; label.characters = ${JSON.stringify(it.label)};
label.fills = [{ type: "SOLID", color: { r: 0.85, g: 0.86, b: 0.88 } }];
sec.fills = [{ type: "SOLID", color: { r: 0.165, g: 0.176, b: 0.192 } }];
sec.appendChild(label); label.x = ${it.x}; label.y = 40;
const board = await build(JSON.parse(UNPACK(PACK_T[${j}])), sec, null, false);
board.name = ${JSON.stringify(it.name)}; board.x = ${it.x}; board.y = 100;
sec.resizeWithoutConstraints(Math.max(sec.width, ${it.x} + board.width + 60), Math.max(sec.height, board.height + 160));
results.push({ board: ${JSON.stringify(it.name)}, id: board.id, w: board.width, h: board.height });
}`);
  return prelude(items[0].target.page, false) +
    `const PACK_D = String.raw\`${dict.join(SEP)}\`.split("${SEP}");\nconst PACK_T = String.raw\`${texts.join(SEP)}\`.split("${SEP}");\n` +
    `const UNPACK = s => s.replace(${TOKEN}, (_, i) => UNPACK(PACK_D[+i]));\nconst results = [];\n${blocks.join("\n")}\nreturn { results, createdIds: created };\n`;
}
const packs = [];
for (const it of packItems) {
  const last = packs[packs.length - 1];
  if (last && last[0].target.page === it.target.page && packScript([...last, it]).length < 48000) last.push(it);
  else packs.push([it]);
}
packs.forEach((p, i) => {
  const code = packScript(p);
  fs.writeFileSync(path.join(outDir, `pack-${i}.js`), code);
  console.log(`pack-${i}.js ${code.length} chars: ${p.map(it => it.name).join(", ")}`);
});
fs.writeFileSync(path.join(outDir, "index.json"), JSON.stringify({ library: library.length, files, batches: batches.map(b => b.boards), packs: packs.map(p => p.map(it => it.name)) }, null, 2));
console.log(`library.js ${library.length} chars: ${groups.size} sets, ${comps.size} variants, ${iconNames.size} icons`);
for (const f of files) console.log(`${f.file} ${f.chars} chars  ${f.section} / ${f.board}`);
