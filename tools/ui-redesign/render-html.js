// Browser renderer for the layout description: frames become flexbox, mirroring Figma auto
// layout. Loaded as plain script text by the preview pages (no modules, no dependencies).
/* global document */
function renderDesign(node, ICONS, IMAGES, parent) {
  const parentLayout = parent ? parent.layout : null;
  const stretch = parent && parent.align === "stretch";
  // Width/height sizing relative to the parent's main and cross axis.
  function size(el, axis) {
    const value = node[axis];
    const main = (parentLayout === "row" && axis === "w") || (parentLayout === "col" && axis === "h");
    const cross = (parentLayout === "row" && axis === "h") || (parentLayout === "col" && axis === "w");
    const prop = axis === "w" ? "width" : "height";
    if (typeof value === "number") { el.style[prop] = value + "px"; if (main) el.style.flexShrink = "0"; return; }
    if (value === "fill" || (cross && stretch && value !== "hug-strict")) {
      if (main) { el.style.flex = "1 1 0"; el.style[axis === "w" ? "minWidth" : "minHeight"] = "0"; }
      else if (cross) el.style.alignSelf = "stretch";
      else el.style[prop] = "100%";
      return;
    }
    if (main) el.style.flexShrink = "0";
  }
  function place(el) {
    // abs nodes float over an auto-layout parent, like Figma's absolute position.
    if (parentLayout === "free" || node.abs) { el.style.position = "absolute"; el.style.left = (node.x || 0) + "px"; el.style.top = (node.y || 0) + "px"; }
  }

  if (node.t === "text") {
    const el = document.createElement("div");
    el.textContent = node.text;
    el.style.cssText = `font-family:'Microsoft YaHei';font-size:${node.size}px;line-height:${node.lh}px;color:${node.color};font-weight:${node.bold ? 700 : 400};` +
      `text-align:${node.align || "left"};white-space:${node.w === "hug" ? "pre" : "pre-wrap"};overflow-wrap:anywhere;`;
    size(el, "w"); place(el);
    return el;
  }
  if (node.t === "icon") {
    const el = document.createElement("div");
    el.innerHTML = `<svg width="${node.size}" height="${node.size}" viewBox="0 0 16 16" fill="none"><path d="${ICONS[node.name]}" stroke="${node.color}" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
    el.style.cssText = `width:${node.size}px;height:${node.size}px;flex-shrink:0;display:flex;`;
    place(el);
    return el;
  }
  if (node.t === "svg") {
    const el = document.createElement("div");
    el.innerHTML = node.markup;
    el.style.cssText = `width:${node.w}px;height:${node.h}px;flex-shrink:0;display:flex;`;
    place(el);
    return el;
  }
  if (node.t === "img") {
    const el = document.createElement("img");
    el.src = IMAGES[node.src];
    el.style.cssText = `width:${node.w}px;height:${node.h}px;object-fit:cover;flex-shrink:0;border-radius:${node.radius || 0}px;`;
    place(el);
    return el;
  }
  const el = document.createElement("div");
  el.dataset.name = node.name || "";
  const s = el.style;
  s.boxSizing = "border-box";
  if (node.layout === "free" || node.children.some(c => c.abs)) s.position = "relative";
  if (node.layout !== "free") {
    s.display = "flex";
    s.flexDirection = node.layout === "row" ? "row" : "column";
    s.gap = (node.gap || 0) + "px";
    s.alignItems = { start: "flex-start", center: "center", end: "flex-end", stretch: "flex-start" }[node.align || "start"];
    s.justifyContent = { start: "flex-start", center: "center", end: "flex-end", between: "space-between" }[node.justify || "start"];
  }
  const pad = typeof node.pad === "number" ? [node.pad, node.pad, node.pad, node.pad]
    : node.pad.length === 2 ? [node.pad[0], node.pad[1], node.pad[0], node.pad[1]] : node.pad;
  s.padding = pad.map(v => v + "px").join(" ");
  if (node.bg) s.background = node.bg;
  if (node.border) { s.outline = `${node.border.w}px solid ${node.border.c}`; s.outlineOffset = `-${node.border.w}px`; }
  if (node.radius) s.borderRadius = Array.isArray(node.radius) ? node.radius.map(v => v + "px").join(" ") : node.radius + "px";
  if (node.clip) s.overflow = "hidden";
  // Same values as the Figma effect style DACT/浮层阴影.
  if (node.shadow) s.boxShadow = "0 8px 24px rgba(0,0,0,0.45)";
  if (node.opacity !== undefined && node.opacity !== 1) s.opacity = node.opacity;
  size(el, "w"); size(el, "h"); place(el);
  for (const child of node.children) el.appendChild(renderDesign(child, ICONS, IMAGES, node));
  return el;
}
