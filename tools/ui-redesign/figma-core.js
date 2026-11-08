// Runtime shared by every generated use_figma call (Figma MCP). The generator prepends
// PAGE_ID, LIB_PAGE_ID, COMP_SECTION_ID, ICON_SECTION_ID, LOGO_HASH, IMAGE_HASHES, ICONS and VARIANT_KEYS.
// Nothing persists between calls, so each call re-resolves variables, text styles and the
// DACT component library from the file.
const page = await figma.getNodeByIdAsync(PAGE_ID);
await figma.setCurrentPageAsync(page);
if (LIB_PAGE_ID !== PAGE_ID) await (await figma.getNodeByIdAsync(LIB_PAGE_ID)).loadAsync();
const FONT = { family: "Noto Sans SC", style: "Medium" };
const FONT_BOLD = { family: "Noto Sans SC", style: "Bold" };
await figma.loadFontAsync(FONT);
await figma.loadFontAsync(FONT_BOLD);
const VARS = {}, RESOLVED = {}, DEFAULT_MODES = {};
for (const v of await figma.variables.getLocalVariablesAsync()) {
VARS[v.name] = v;
if (v.resolvedType !== "COLOR") continue;
if (!DEFAULT_MODES[v.variableCollectionId]) DEFAULT_MODES[v.variableCollectionId] = (await figma.variables.getVariableCollectionByIdAsync(v.variableCollectionId)).defaultModeId;
const c = v.valuesByMode[DEFAULT_MODES[v.variableCollectionId]];
RESOLVED[v.name] = { r: c.r, g: c.g, b: c.b, a: c.a === undefined ? 1 : c.a };
}
// Text styles are named "DACT/<size> <role>" and looked up by size.
const STYLES = {};
for (const s of await figma.getLocalTextStylesAsync()) { const m = /^DACT\/(\d+)/.exec(s.name); if (m) STYLES[m[1]] = s; }
const compSection = await figma.getNodeByIdAsync(COMP_SECTION_ID);
const iconSection = await figma.getNodeByIdAsync(ICON_SECTION_ID);
// Component key (as written in screens.js) -> main component, through the variant names.
const MASTERS = new Map();
for (const n of compSection.children) {
  if (n.type === "COMPONENT_SET") for (const v of n.children) { for (const k of [].concat(VARIANT_KEYS[`${n.name}|${v.name}`] || [])) MASTERS.set(k, v); }
  else if (n.type === "COMPONENT") { for (const k of [].concat(VARIANT_KEYS[`${n.name}|`] || [])) MASTERS.set(k, n); }
}
const ICON_MASTERS = new Map(iconSection.children.filter(n => n.type === "COMPONENT").map(n => [n.name, n]));
// Popups (DropdownMenu) carry the shared shadow style instead of a per-node effect.
const SHADOW = (await figma.getLocalEffectStylesAsync()).find(s => s.name === "DACT/浮层阴影");
const created = [];

// Colours arrive as {v: variable name, a: alpha} (bound to the DACT palette) or {r,g,b,a}.
// Binding resets the paint opacity and instance overrides render the base colour, so the
// paint starts from the default-skin value and gets its alpha back after binding.
// inInstance: overrides on an instance drop the variable's own alpha (derived/accent-soft
// rendered solid on Pill/ok), so there the variable alpha is folded into the paint opacity.
function paint(c, inInstance) {
  if (!c) return [];
  if (c.v) {
    const { r, g, b, a } = RESOLVED[c.v];
    const opacity = inInstance ? c.a * a : c.a;
    return [{ ...figma.variables.setBoundVariableForPaint({ type: "SOLID", color: { r, g, b }, opacity }, "color", VARS[c.v]), opacity }];
  }
  return [{ type: "SOLID", color: { r: c.r, g: c.g, b: c.b }, opacity: c.a }];
}
// Gaps, paddings and radii bind to the "DACT 尺寸" variables when one has that exact value.
function bindNumber(node, field, value, prefix) {
  if (!value) return;
  const v = VARS[`${prefix}/${value}`];
  if (v) node.setBoundVariable(field, v);
}
// Figma drops a bound paint's opacity on the first assignment to a fresh node; assigning a
// translucent paint list twice keeps it.
function setPaints(node, key, paints) {
  node[key] = paints;
  if (paints.some(p => p.opacity !== undefined && p.opacity < 1)) node[key] = paints;
}
const isAuto = n => n && "layoutMode" in n && n.layoutMode !== "NONE";
const padOf = p => typeof p === "number" ? [p, p, p, p] : p.length === 2 ? [p[0], p[1], p[0], p[1]] : p;

// Numbers are fixed sizes, "fill" fills the auto-layout parent, and inside a stretch parent
// every non-fixed child fills the cross axis (same rules as the HTML preview).
function size(node, s, parent, parentSpec) {
  // abs children float over an auto-layout parent at x/y (popups) and size like free children.
  const floating = !!s.abs && isAuto(parent);
  if (floating) node.layoutPositioning = "ABSOLUTE";
  const auto = isAuto(parent) && !floating;
  const cross = parent && parent.layoutMode === "HORIZONTAL" ? "h" : "w";
  const stretch = parentSpec && parentSpec.align === "stretch";
  const w = typeof s.w === "number" ? s.w : null, h = typeof s.h === "number" ? s.h : null;
  if (w !== null || h !== null) {
    if (node.type === "TEXT") node.textAutoResize = "HEIGHT";
    node.resize(w !== null ? w : Math.max(node.width, 1), h !== null ? h : Math.max(node.height, 1));
  }
  for (const [axis, prop] of [["w", "layoutSizingHorizontal"], ["h", "layoutSizingVertical"]]) {
    const v = s[axis];
    if (typeof v === "number") { if (auto) node[prop] = "FIXED"; continue; }
    if ((v === "fill" || (stretch && axis === cross)) && auto) {
      if (node.type === "TEXT" && axis === "w") node.textAutoResize = "HEIGHT";
      node[prop] = "FILL";
    } else if (node.type === "TEXT") { if (axis === "w") node.textAutoResize = "WIDTH_AND_HEIGHT"; }
    else if (isAuto(node)) node[prop] = "HUG";
  }
  if (parent && (parent.layoutMode === "NONE" || floating) && s.x !== undefined) { node.x = s.x; node.y = s.y; }
}

function styleFrame(f, s) {
  f.name = s.name || s.comp || "frame";
  setPaints(f, "fills", paint(s.bg));
  if (s.border) { setPaints(f, "strokes", paint(s.border.c)); f.strokeWeight = s.border.w; f.strokeAlign = "INSIDE"; } else f.strokes = [];
  if (Array.isArray(s.radius)) [f.topLeftRadius, f.topRightRadius, f.bottomRightRadius, f.bottomLeftRadius] = s.radius;
  else if (s.radius) for (const corner of ["topLeftRadius", "topRightRadius", "bottomRightRadius", "bottomLeftRadius"]) { f[corner] = s.radius; bindNumber(f, corner, s.radius, "radius"); }
  f.clipsContent = !!s.clip;
  f.opacity = s.opacity === undefined ? 1 : s.opacity;
  if (s.layout === "free") { f.layoutMode = "NONE"; return; }
  f.layoutMode = s.layout === "row" ? "HORIZONTAL" : "VERTICAL";
  f.itemSpacing = s.gap || 0;
  bindNumber(f, "itemSpacing", s.gap, "space");
  const pad = padOf(s.pad || 0);
  ["paddingTop", "paddingRight", "paddingBottom", "paddingLeft"].forEach((field, i) => { f[field] = pad[i]; bindNumber(f, field, pad[i], "space"); });
  f.primaryAxisAlignItems = { start: "MIN", center: "CENTER", end: "MAX", between: "SPACE_BETWEEN" }[s.justify || "start"];
  f.counterAxisAlignItems = { start: "MIN", center: "CENTER", end: "MAX", stretch: "MIN" }[s.align || "start"];
}

function recolor(node, c) {
  for (const v of node.findAllWithCriteria({ types: ["VECTOR"] })) setPaints(v, "strokes", paint(c));
}
// One 16 px main component per icon; instances scale to the slot size.
function iconMaster(name) {
  const key = `Icon/${name}`;
  if (ICON_MASTERS.has(key)) return ICON_MASTERS.get(key);
  // Board scripts carry no icon paths: every icon must already be in the library.
  if (!ICONS[name]) throw new Error(`组件库缺少图标 ${name}`);
  const comp = figma.createComponent();
  comp.name = key; comp.fills = []; comp.resize(16, 16);
  const svg = figma.createNodeFromSvg(`<svg width="16" height="16" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="${ICONS[name]}" stroke="#A6B3C2" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>`);
  svg.name = name; svg.fills = [];
  comp.appendChild(svg); svg.x = 0; svg.y = 0;
  for (const n of [svg, ...svg.findAll(() => true)]) if ("constraints" in n) n.constraints = { horizontal: "SCALE", vertical: "SCALE" };
  recolor(comp, { v: "palette/muted", a: 1 });
  comp.description = `DACT 线性图标 ${name}（16×16，1.5 px 描边）`;
  iconSection.appendChild(comp);
  ICON_MASTERS.set(key, comp); created.push(comp.id);
  return comp;
}
function placeIcon(inst, px, c) {
  if (inst.width !== px) inst.resize(px, px);
  recolor(inst, c);
}

async function text(s, parent, parentSpec) {
  const t = figma.createText();
  t.fontName = FONT;
  const style = STYLES[s.size];
  if (style) await t.setTextStyleIdAsync(style.id);
  else { t.fontSize = s.size; t.lineHeight = { unit: "PIXELS", value: s.lh }; }
  t.characters = s.text;
  // Bold overrides the text style's weight (numbers that must stand out, e.g. parse values).
  if (s.bold) t.fontName = FONT_BOLD;
  setPaints(t, "fills", paint(s.color));
  t.textAlignHorizontal = { left: "LEFT", center: "CENTER", right: "RIGHT" }[s.align || "left"];
  parent.appendChild(t);
  size(t, s, parent, parentSpec);
  return t;
}

// Builds a spec into `parent`. Component specs become instances of the library component,
// with per-use text, colours and icons applied by sync(); asMaster builds the component itself.
async function build(s, parent, parentSpec, asMaster) {
  if (s.comp && !asMaster) {
    const master = MASTERS.get(s.comp);
    if (!master) throw new Error(`组件库缺少 ${s.comp}`);
    const inst = master.createInstance();
    parent.appendChild(inst);
    size(inst, s, parent, parentSpec);
    if (s.name) inst.name = s.name;
    sync(inst, s);
    return inst;
  }
  if (s.t === "text") return text(s, parent, parentSpec);
  if (s.t === "icon") {
    const inst = iconMaster(s.name).createInstance();
    parent.appendChild(inst);
    size(inst, { w: s.size, h: s.size, x: s.x, y: s.y }, parent, null);
    placeIcon(inst, s.size, s.color);
    return inst;
  }
  if (s.t === "svg") {
    // Without markup this is a placeholder named after its ref; skin-thumbs.js draws it.
    const n = s.markup ? figma.createNodeFromSvg(s.markup) : figma.createFrame();
    if (!s.markup) { n.fills = []; s = { ...s, name: "svg:" + s.ref }; }
    n.name = s.name || "图形";
    parent.appendChild(n);
    size(n, { w: s.w, h: s.h, x: s.x, y: s.y, abs: s.abs }, parent, null);
    return n;
  }
  // The DACT logo is a vector component (user-provided SVG); other images stay image fills.
  if (s.t === "img" && s.src === "logo") {
    const logo = compSection.children.find(n => n.type === "COMPONENT" && n.name === "DACT 标志 Logo");
    if (logo) {
      const inst = logo.createInstance(); inst.name = "DACT 标志";
      parent.appendChild(inst); inst.rescale(s.w / logo.width);
      if (isAuto(parent) && s.abs) inst.layoutPositioning = "ABSOLUTE";
      if (parent && (parent.layoutMode === "NONE" || s.abs) && s.x !== undefined) { inst.x = s.x; inst.y = s.y; }
      return inst;
    }
  }
  if (s.t === "img") {
    const r = figma.createRectangle();
    // Image sources other than the ACT logo (sponsor crown, administrator icon) keep their aspect.
    r.name = s.name || "ACT 徽标";
    r.fills = [{ type: "IMAGE", imageHash: IMAGE_HASHES[s.src] || LOGO_HASH, scaleMode: IMAGE_HASHES[s.src] && s.src !== "logo" ? "FIT" : "FILL" }];
    if (s.radius) { r.cornerRadius = s.radius; for (const c of ["topLeftRadius", "topRightRadius", "bottomRightRadius", "bottomLeftRadius"]) bindNumber(r, c, s.radius, "radius"); }
    parent.appendChild(r);
    size(r, { w: s.w, h: s.h, x: s.x, y: s.y }, parent, null);
    return r;
  }
  const f = asMaster ? figma.createComponent() : figma.createFrame();
  styleFrame(f, s);
  if (s.shadow) {
    if (!SHADOW) throw new Error("缺少效果样式 DACT/浮层阴影");
    await f.setEffectStyleIdAsync(SHADOW.id);
  }
  parent.appendChild(f);
  // A main component sits in a plain section, so "fill" becomes a fixed default there.
  const own = asMaster ? { ...s, w: s.w === "fill" ? 220 : s.w, h: s.h === "fill" ? 40 : s.h } : s;
  size(f, own, asMaster ? null : parent, asMaster ? null : parentSpec);
  for (const child of s.children || []) await build(child, f, s, false);
  return f;
}

function sync(node, s) {
  if (!node || !s) return;
  if ((s.t === "text") !== (node.type === "TEXT")) return;
  if (s.t === "text") { if (node.characters !== s.text) node.characters = s.text; setPaints(node, "fills", paint(s.color, true)); return; }
  if (s.t === "icon") {
    const master = iconMaster(s.name);
    // Unrenamed instances carry their main component's name, which avoids the async lookup.
    if (node.type === "INSTANCE" && node.name !== master.name) node.swapComponent(master);
    placeIcon(node, s.size, s.color);
    return;
  }
  if (s.t === "img" || s.t === "svg") return;
  // Nested instances start as whatever variant the main component used (a DropdownMenu row is
  // "normal" in the library); vp names the variant this use needs.
  if (s.vp && node.type === "INSTANCE" && Object.entries(s.vp).some(([k, v]) => node.variantProperties?.[k] !== v)) node.setProperties(s.vp);
  setPaints(node, "fills", paint(s.bg, true));
  // Instances can come out with the master's border at full opacity; repaint it from the spec.
  if (s.border && "strokes" in node) setPaints(node, "strokes", paint(s.border.c, true));
  node.opacity = s.opacity === undefined ? 1 : s.opacity;
  // Children are re-read per row: a variant switch replaces the switched instance's subtree.
  const count = (s.children || []).length;
  for (let i = 0; i < count; i++) sync("children" in node ? node.children[i] : undefined, s.children[i]);
  // List components (DropdownMenu) may hold more rows in the library than this use shows.
  if (node.type === "INSTANCE") node.children.slice(count).forEach(k => { k.visible = false; });
}
