// Figma development plugin: builds the DACT 1.0 redesign from the layout description as
// auto-layout frames, with reusable main components (buttons, toggles, inputs, nav items,
// icons). build.js inlines DESIGN, ICONS and IMAGES.
const DESIGN = /*__DESIGN__*/ null;
const ICONS = /*__ICONS__*/ null;
const IMAGES = /*__IMAGES__*/ null;

let font, imageHashes = {}, masters = {}, masterSection, masterCursor = { x: 40, y: 80, row: 0 };

function color(value) {
  if (!value) return null;
  if (value[0] === "#") {
    const n = parseInt(value.slice(1), 16);
    return { r: ((n >> 16) & 255) / 255, g: ((n >> 8) & 255) / 255, b: (n & 255) / 255, a: 1 };
  }
  const [r, g, b, a] = value.replace(/rgba?\(|\)/g, "").split(",").map(Number);
  return { r: r / 255, g: g / 255, b: b / 255, a: a === undefined ? 1 : a };
}
function paints(value) {
  const c = color(value);
  return c ? [{ type: "SOLID", color: { r: c.r, g: c.g, b: c.b }, opacity: c.a }] : [];
}
const isAuto = n => n && "layoutMode" in n && n.layoutMode !== "NONE";
const padOf = p => typeof p === "number" ? [p, p, p, p] : p.length === 2 ? [p[0], p[1], p[0], p[1]] : p;

// Sizing follows the HTML renderer: numbers are fixed, "fill" fills the parent, and in a
// stretch parent every non-fixed child fills the cross axis.
function applySizing(node, spec, parent, parentSpec) {
  // abs children float over an auto-layout parent at x/y (popups) and size like free children.
  const floating = !!spec.abs && isAuto(parent);
  if (floating) node.layoutPositioning = "ABSOLUTE";
  const auto = isAuto(parent) && !floating;
  const stretch = parentSpec && parentSpec.align === "stretch";
  const crossAxis = parent && parent.layoutMode === "HORIZONTAL" ? "h" : "w";
  const fixedW = typeof spec.w === "number" ? spec.w : null;
  const fixedH = typeof spec.h === "number" ? spec.h : null;
  if (fixedW !== null || fixedH !== null) {
    if (node.type === "TEXT") node.textAutoResize = "HEIGHT";
    node.resize(fixedW !== null ? fixedW : Math.max(node.width, 1), fixedH !== null ? fixedH : Math.max(node.height, 1));
  }
  for (const [axis, prop] of [["w", "layoutSizingHorizontal"], ["h", "layoutSizingVertical"]]) {
    const value = spec[axis];
    if (typeof value === "number") { if (auto) node[prop] = "FIXED"; continue; }
    const wantsFill = value === "fill" || (stretch && axis === crossAxis);
    if (wantsFill && auto) {
      node[prop] = "FILL";
      if (node.type === "TEXT" && axis === "w") node.textAutoResize = "HEIGHT";
    } else if (node.type === "TEXT") {
      if (axis === "w") node.textAutoResize = "WIDTH_AND_HEIGHT";
    } else if (isAuto(node)) node[prop] = "HUG";
  }
  if (parent && (parent.layoutMode === "NONE" || floating) && spec.x !== undefined) { node.x = spec.x; node.y = spec.y; }
}

function styleFrame(f, spec) {
  f.name = spec.name || "frame";
  f.fills = paints(spec.bg);
  if (spec.border) { f.strokes = paints(spec.border.c); f.strokeWeight = spec.border.w; f.strokeAlign = "INSIDE"; } else f.strokes = [];
  if (Array.isArray(spec.radius)) [f.topLeftRadius, f.topRightRadius, f.bottomRightRadius, f.bottomLeftRadius] = spec.radius;
  else f.cornerRadius = spec.radius || 0;
  f.clipsContent = !!spec.clip;
  f.opacity = spec.opacity === undefined ? 1 : spec.opacity;
  if (spec.layout === "free") f.layoutMode = "NONE";
  else {
    f.layoutMode = spec.layout === "row" ? "HORIZONTAL" : "VERTICAL";
    f.itemSpacing = spec.gap || 0;
    [f.paddingTop, f.paddingRight, f.paddingBottom, f.paddingLeft] = padOf(spec.pad || 0);
    f.primaryAxisAlignItems = { start: "MIN", center: "CENTER", end: "MAX", between: "SPACE_BETWEEN" }[spec.justify || "start"];
    f.counterAxisAlignItems = { start: "MIN", center: "CENTER", end: "MAX", stretch: "MIN" }[spec.align || "start"];
  }
}

function iconMaster(name, size, stroke) {
  const key = `Icon/${name}/${size}`;
  if (masters[key]) return masters[key];
  const comp = figma.createComponent();
  comp.name = key;
  comp.fills = [];
  comp.resize(size, size);
  const svg = figma.createNodeFromSvg(
    `<svg width="${size}" height="${size}" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg">` +
    `<path d="${ICONS[name]}" stroke="${stroke}" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>`);
  svg.name = name;
  comp.appendChild(svg);
  svg.x = 0; svg.y = 0;
  placeMaster(comp);
  masters[key] = comp;
  return comp;
}
function recolor(node, value) {
  const c = color(value);
  const visit = n => {
    if ("strokes" in n && n.type === "VECTOR") n.strokes = [{ type: "SOLID", color: { r: c.r, g: c.g, b: c.b }, opacity: c.a }];
    if ("children" in n) n.children.forEach(visit);
  };
  visit(node);
}

function placeMaster(comp) {
  masterSection.appendChild(comp);
  if (masterCursor.x + comp.width > 1400) { masterCursor.x = 40; masterCursor.y += masterCursor.row + 32; masterCursor.row = 0; }
  comp.x = masterCursor.x; comp.y = masterCursor.y;
  masterCursor.x += comp.width + 32;
  masterCursor.row = Math.max(masterCursor.row, comp.height);
}

// Builds `spec` into `parent`. Component specs become instances of a lazily built main
// component, with text, colours and nested icons overridden per use.
function build(spec, parent, parentSpec, asMaster) {
  if (spec.comp && !asMaster) {
    let master = masters[spec.comp];
    if (!master) {
      master = buildMaster(spec);
    }
    const instance = master.createInstance();
    parent.appendChild(instance);
    applySizing(instance, spec, parent, parentSpec);
    instance.name = spec.name || spec.comp;
    sync(instance, spec);
    return instance;
  }
  if (spec.t === "text") {
    const t = figma.createText();
    t.fontName = font;
    t.fontSize = spec.size;
    t.lineHeight = { unit: "PIXELS", value: spec.lh };
    t.characters = spec.text;
    t.fills = paints(spec.color);
    t.textAlignHorizontal = { left: "LEFT", center: "CENTER", right: "RIGHT" }[spec.align || "left"];
    parent.appendChild(t);
    applySizing(t, spec, parent, parentSpec);
    return t;
  }
  if (spec.t === "icon") {
    const instance = iconMaster(spec.name, spec.size, "#A6B3C2").createInstance();
    parent.appendChild(instance);
    applySizing(instance, { w: spec.size, h: spec.size, x: spec.x, y: spec.y }, parent, null);
    recolor(instance, spec.color);
    return instance;
  }
  if (spec.t === "svg") {
    const n = figma.createNodeFromSvg(spec.markup);
    n.name = spec.name || "图形";
    parent.appendChild(n);
    applySizing(n, { w: spec.w, h: spec.h, x: spec.x, y: spec.y, abs: spec.abs }, parent, null);
    return n;
  }
  if (spec.t === "img") {
    const r = figma.createRectangle();
    r.name = "图片/" + spec.src;
    r.fills = [{ type: "IMAGE", imageHash: imageHashes[spec.src], scaleMode: "FILL" }];
    r.cornerRadius = spec.radius || 0;
    parent.appendChild(r);
    applySizing(r, { w: spec.w, h: spec.h, x: spec.x, y: spec.y }, parent, null);
    return r;
  }
  const f = asMaster ? figma.createComponent() : figma.createFrame();
  styleFrame(f, spec);
  // Same values as the MCP library's effect style DACT/浮层阴影 (the plugin has no styles).
  if (spec.shadow) f.effects = [{ type: "DROP_SHADOW", color: { r: 0, g: 0, b: 0, a: 0.45 }, offset: { x: 0, y: 8 }, radius: 24, spread: 0, visible: true, blendMode: "NORMAL" }];
  if (asMaster) f.name = spec.comp;
  parent.appendChild(f);
  // A main component sits in a plain section, so "fill" becomes a fixed default size.
  const own = asMaster ? { ...spec, w: spec.w === "fill" ? 220 : spec.w, h: spec.h === "fill" ? 40 : spec.h } : spec;
  applySizing(f, own, asMaster ? null : parent, asMaster ? null : parentSpec);
  for (const child of spec.children) build(child, f, spec, false);
  return f;
}
function buildMaster(spec) {
  const comp = build(spec, masterSection, null, true);
  masters[spec.comp] = comp;
  placeMaster(comp);
  return comp;
}

// Walks an instance alongside the spec that produced it and applies per-use differences.
function sync(node, spec) {
  if (!node || !spec) return;
  // Same component key implies the same structure; skip anything that does not line up.
  if ((spec.t === "text") !== (node.type === "TEXT")) return;
  if (spec.t === "text") {
    if (node.characters !== spec.text) node.characters = spec.text;
    node.fills = paints(spec.color);
    return;
  }
  if (spec.t === "icon") {
    const master = iconMaster(spec.name, spec.size, "#A6B3C2");
    if (node.mainComponent && node.mainComponent.id !== master.id) node.swapComponent(master);
    recolor(node, spec.color);
    return;
  }
  if (spec.t === "img") return;
  node.fills = paints(spec.bg);
  node.opacity = spec.opacity === undefined ? 1 : spec.opacity;
  const children = "children" in node ? node.children : [];
  spec.children.forEach((child, i) => sync(children[i], child));
}

async function chooseFont() {
  const fonts = await figma.listAvailableFontsAsync();
  for (const family of ["Microsoft YaHei", "Microsoft YaHei UI", "微软雅黑", "Noto Sans SC", "Inter"]) {
    const match = fonts.find(f => f.fontName.family === family && f.fontName.style === "Regular") || fonts.find(f => f.fontName.family === family);
    if (match) { await figma.loadFontAsync(match.fontName); return match.fontName; }
  }
  await figma.loadFontAsync({ family: "Inter", style: "Regular" });
  return { family: "Inter", style: "Regular" };
}

async function main() {
  font = await chooseFont();
  for (const [name, data] of Object.entries(IMAGES)) imageHashes[name] = figma.createImage(figma.base64Decode(data)).hash;
  const page = figma.createPage();
  page.name = DESIGN.title;
  await figma.setCurrentPageAsync(page);

  const title = figma.createText();
  title.fontName = font; title.fontSize = 48; title.characters = DESIGN.title;
  page.appendChild(title);

  let y = 140;
  masterSection = figma.createSection();
  masterSection.name = "组件母版（main components）";
  page.appendChild(masterSection);
  let count = 0;
  const sections = [];
  for (const group of DESIGN.sections) {
    const section = figma.createSection();
    section.name = group.name;
    page.appendChild(section);
    sections.push(section);
    let x = 60, height = 0;
    for (const screen of group.screens) {
      figma.notify(`正在生成 ${group.name} / ${screen.name}`, { timeout: 800 });
      const label = figma.createText();
      label.fontName = font; label.fontSize = 20;
      label.characters = screen.note ? `${screen.name} — ${screen.note}` : screen.name;
      section.appendChild(label);
      label.x = x; label.y = 60;
      const frame = build(screen.tree, section, null, false);
      frame.name = screen.name;
      frame.x = x; frame.y = 100;
      x += Math.max(frame.width, label.width) + 100;
      height = Math.max(height, frame.height);
      count++;
    }
    section.resizeWithoutConstraints(x, height + 160);
    section.x = 0; section.y = y;
    y += section.height + 120;
  }
  masterSection.x = 0; masterSection.y = y;
  masterSection.resizeWithoutConstraints(1480, masterCursor.y + masterCursor.row + 80);

  figma.viewport.scrollAndZoomIntoView([sections[0]]);
  figma.closePlugin(`已生成 ${count} 个画板、${Object.keys(masters).length} 个组件到「${page.name}」`);
}

main().catch(error => { console.error(error); figma.closePlugin("生成失败：" + error.message); });
