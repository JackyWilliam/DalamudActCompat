// Figma development plugin that rebuilds the DACT UI baseline as editable layers.
// tools/ui-baseline/build-figma-plugin.py fills BASELINE with the captures exported by
// `PackageSmokeTests --ui-baseline`; every node comes from a real cimgui draw command.
const BASELINE = /*__DATA__*/ null;

const GAP = 80;          // space between screens inside a section
const SECTION_GAP = 160; // space between sections
const PAD = 60;          // section inner padding

function rgb(c) { return { r: c[0], g: c[1], b: c[2] }; }

function paint(p) {
  if (!p) return [];
  if (p.c) return [{ type: "SOLID", color: rgb(p.c), opacity: p.c[3] }];
  // Vertical gradients run top to bottom, horizontal ones left to right.
  const transform = p.g === "v" ? [[0, 1, 0], [-1, 0, 1]] : [[1, 0, 0], [0, 1, 0]];
  return [{
    type: "GRADIENT_LINEAR",
    gradientTransform: transform,
    gradientStops: [
      { position: 0, color: { r: p.a[0], g: p.a[1], b: p.a[2], a: p.a[3] } },
      { position: 1, color: { r: p.b[0], g: p.b[1], b: p.b[2], a: p.b[3] } },
    ],
  }];
}

// "M x y L x y Z" with absolute screen coordinates, shifted so the vector starts at its box.
function relativePath(d, dx, dy) {
  return d.replace(/(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)/g, (_, x, y) =>
    `${+(parseFloat(x) - dx).toFixed(2)} ${+(parseFloat(y) - dy).toFixed(2)}`);
}

// Window and child names look like "Title###Id/child-name_1A2B3C4D"; keep a readable tail.
function layerName(owner) {
  const tail = owner.split("/").pop();
  return tail.replace(/_[0-9A-F]{8}$/, "").replace(/^.*###/, "").replace(/^##/, "") || owner;
}

async function chooseFont() {
  const fonts = await figma.listAvailableFontsAsync();
  const families = ["Microsoft YaHei", "Microsoft YaHei UI", "微软雅黑", "Noto Sans SC", "Inter"];
  for (const family of families) {
    const match = fonts.find(f => f.fontName.family === family && f.fontName.style === "Regular")
      || fonts.find(f => f.fontName.family === family);
    if (match) { await figma.loadFontAsync(match.fontName); return match.fontName; }
  }
  await figma.loadFontAsync({ family: "Inter", style: "Regular" });
  return { family: "Inter", style: "Regular" };
}

function buildNode(n, font, images, ox, oy) {
  if (n.k === "rect") {
    const r = figma.createRectangle();
    r.resizeWithoutConstraints(Math.max(n.w, 0.01), Math.max(n.h, 0.01));
    r.x = n.x - ox; r.y = n.y - oy;
    [r.topLeftRadius, r.topRightRadius, r.bottomRightRadius, r.bottomLeftRadius] = n.r;
    r.fills = paint(n.fill);
    if (n.stroke) {
      r.strokes = paint(n.stroke);
      r.strokeWeight = n.sw;
      r.strokeAlign = "INSIDE";
      r.name = "描边";
    } else r.name = n.w * n.h > 4000 ? "面板" : "形状";
    return r;
  }
  if (n.k === "path") {
    const v = figma.createVector();
    v.vectorPaths = [{ windingRule: "NONZERO", data: relativePath(n.d, n.x, n.y) }];
    v.x = n.x - ox; v.y = n.y - oy;
    v.fills = paint(n.fill);
    v.strokes = [];
    v.name = "图形";
    return v;
  }
  if (n.k === "text") {
    const t = figma.createText();
    t.fontName = font;
    t.fontSize = n.size;
    t.lineHeight = { unit: "PIXELS", value: n.lh };
    t.characters = n.text;
    t.fills = [{ type: "SOLID", color: rgb(n.color), opacity: n.color[3] }];
    t.textAutoResize = "WIDTH_AND_HEIGHT";
    t.x = n.x - ox; t.y = n.y - oy;
    return t;
  }
  if (n.k === "image") {
    const r = figma.createRectangle();
    r.resizeWithoutConstraints(Math.max(n.w, 0.01), Math.max(n.h, 0.01));
    r.x = n.x - ox; r.y = n.y - oy;
    r.fills = [{ type: "IMAGE", imageHash: images[n.src], scaleMode: "FILL" }];
    r.name = "图片";
    return r;
  }
  return null;
}

async function buildScreen(shot, font, images, section, x, y) {
  const nodes = shot.nodes;
  const frame = figma.createFrame();
  frame.name = shot.name;
  frame.x = x; frame.y = y;
  frame.resizeWithoutConstraints(shot.w, shot.h);
  frame.fills = [{ type: "SOLID", color: rgb(BASELINE.background) }];
  frame.clipsContent = true;
  section.appendChild(frame);

  // The original raster stays underneath, hidden and locked, as the 1:1 reference.
  const reference = figma.createRectangle();
  reference.name = "参考截图（原始渲染）";
  reference.resizeWithoutConstraints(shot.refW, shot.refH);
  reference.x = -shot.ox; reference.y = -shot.oy;
  reference.fills = [{ type: "IMAGE", imageHash: figma.createImage(figma.base64Decode(shot.png)).hash, scaleMode: "FILL" }];
  reference.visible = false; reference.locked = true;
  frame.appendChild(reference);

  // Consecutive nodes from the same ImGui window/child form one layer group, in draw order.
  let run = [], owner = null;
  const flush = () => {
    if (run.length === 0) return;
    const group = run.length === 1 ? run[0] : figma.group(run, frame);
    if (run.length > 1) group.name = layerName(owner);
    run = [];
  };
  for (const n of nodes) {
    if (n.layer !== owner) { flush(); owner = n.layer; }
    const node = buildNode(n, font, images, shot.ox, shot.oy);
    if (node) { frame.appendChild(node); run.push(node); }
  }
  flush();
  return frame;
}

async function main() {
  if (!BASELINE) { figma.notify("没有基线数据：请先运行 build-figma-plugin.py"); figma.closePlugin(); return; }
  const font = await chooseFont();
  const images = {};
  for (const [name, data] of Object.entries(BASELINE.images)) images[name] = figma.createImage(figma.base64Decode(data)).hash;

  const page = figma.createPage();
  page.name = BASELINE.pageName;
  await figma.setCurrentPageAsync(page);

  const title = figma.createText();
  title.fontName = font; title.fontSize = 48; title.characters = BASELINE.title;
  title.fills = [{ type: "SOLID", color: { r: 0.1, g: 0.1, b: 0.1 } }];
  page.appendChild(title);
  const subtitle = figma.createText();
  subtitle.fontName = font; subtitle.fontSize = 20; subtitle.characters = BASELINE.subtitle;
  subtitle.fills = [{ type: "SOLID", color: { r: 0.35, g: 0.35, b: 0.35 } }];
  subtitle.y = 72;
  page.appendChild(subtitle);

  let y = 200, total = 0;
  for (const group of BASELINE.groups) {
    const section = figma.createSection();
    section.name = group.name;
    page.appendChild(section);
    section.x = 0; section.y = y;
    let x = PAD, height = 0;
    for (const shot of group.shots) {
      figma.notify(`正在导入 ${group.name} / ${shot.name}`, { timeout: 800 });
      const label = figma.createText();
      label.fontName = font; label.fontSize = 18;
      label.characters = shot.note ? `${shot.name} — ${shot.note}` : shot.name;
      label.fills = [{ type: "SOLID", color: { r: 0.25, g: 0.25, b: 0.25 } }];
      section.appendChild(label);
      label.x = x; label.y = PAD;
      await buildScreen(shot, font, images, section, x, PAD + 36);
      x += Math.max(shot.w, label.width) + GAP;
      height = Math.max(height, shot.h);
      total++;
    }
    section.resizeWithoutConstraints(x - GAP + PAD, height + PAD * 2 + 36);
    y += section.height + SECTION_GAP;
  }

  figma.viewport.scrollAndZoomIntoView(page.children);
  figma.closePlugin(`已导入 ${total} 个界面到页面「${page.name}」`);
}

main().catch(error => { console.error(error); figma.closePlugin("导入失败：" + error.message); });
