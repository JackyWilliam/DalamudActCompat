// Builds the redesign outputs from screens.js:
//   node tools/ui-redesign/build.js <output-directory>
// <output>/preview/*.html  browser previews (one per board) plus overflow checks
// <output>/figma-plugin/    Figma development plugin (import manifest.json in Figma desktop)
"use strict";
const fs = require("fs");
const path = require("path");

const design = require("./screens");
const icons = require("./icons");
const output = path.resolve(process.argv[2] || "artifacts/ui-redesign");
const repo = path.resolve(__dirname, "..", "..");
const asset = (...p) => fs.readFileSync(path.join(repo, "src", "DalamudActCompat", "Assets", ...p)).toString("base64");
// Plugin assets drawn by the designs: the DACT logo (assets/dact-logo.svg, user-provided), sponsor crown, administrator icon, and the two
// shipped help screenshots of screens 1.0 does not redraw (meter overlay, Discord gate).
const images = {
  logo: fs.readFileSync(path.join(__dirname, "assets", "dact-logo.svg")).toString("base64"), crown: asset("Icons", "SponsorCrown.png"), admin: asset("Icons", "Player26_Icon.png"),
  "help-meter": asset("Help", "zh-CN", "meter.png"), "help-discord": asset("Help", "zh-CN", "account-discord.png"),
};
const dataUrl = (key, b64) => `data:image/${key === "logo" ? "svg+xml" : "png"};base64,${b64}`;
const renderer = fs.readFileSync(path.join(__dirname, "render-html.js"), "utf8");

// ---- Browser previews. Each page reports its size and any clipped content into #report so
// a headless browser (`--dump-dom`) can check the layout before screenshots are taken.
const previewDir = path.join(output, "preview");
fs.mkdirSync(previewDir, { recursive: true });
const boards = design.sections.flatMap(section => section.screens.map(screen => ({ section: section.name, ...screen })));
const checker = `
function checkLayout(root) {
  const problems = [];
  for (const el of root.querySelectorAll("*")) {
    const style = getComputedStyle(el);
    if (style.overflow === "hidden" && el.scrollHeight > el.clientHeight + 1)
      problems.push("内容超出 " + (el.dataset.name || el.tagName) + " 高度 " + (el.scrollHeight - el.clientHeight) + "px");
    if (style.whiteSpace === "pre" && el.scrollWidth > el.clientWidth + 1)
      problems.push("文字被截断: " + el.textContent);
    if (el.dataset && el.dataset.name !== undefined && el.children.length && style.display === "flex") {
      const box = el.getBoundingClientRect();
      for (const child of el.children) {
        const c = child.getBoundingClientRect();
        if (c.right > box.right + 1 || c.bottom > box.bottom + 1)
          problems.push("子元素溢出 " + (el.dataset.name || "?") + " → " + (child.dataset.name || child.textContent.slice(0, 20)));
      }
    }
  }
  return problems;
}`;
boards.forEach((board, index) => {
  const html = `<!doctype html><html><head><meta charset="utf-8"><style>body{margin:0;background:#2a2d31;}#stage{display:inline-block;padding:20px;}</style></head>
<body><div id="stage"></div><pre id="report" style="display:none"></pre>
<script>${renderer}
const ICONS=${JSON.stringify(icons)};const IMAGES=${JSON.stringify(Object.fromEntries(Object.entries(images).map(([k, v]) => [k, dataUrl(k, v)])))};
${checker}
const tree=${JSON.stringify(board.tree)};
const el=renderDesign(tree,ICONS,IMAGES,null);document.getElementById("stage").appendChild(el);
document.fonts.ready.then(()=>{const r=el.getBoundingClientRect();
document.getElementById("report").textContent=JSON.stringify({w:Math.ceil(r.width)+40,h:Math.ceil(r.height)+40,problems:checkLayout(el)});});
</script></body></html>`;
  fs.writeFileSync(path.join(previewDir, `${String(index).padStart(2, "0")}.html`), html);
});
fs.writeFileSync(path.join(previewDir, "boards.json"), JSON.stringify(boards.map((b, i) => ({ index: i, section: b.section, name: b.name })), null, 2));

// ---- Figma plugin. With the 1:1 baseline plugin's code.js as a second argument, both tools
// ship as one plugin with two menu commands, so the user imports a single manifest.
const pluginDir = path.join(output, "figma-plugin");
fs.mkdirSync(pluginDir, { recursive: true });
const redesign = fs.readFileSync(path.join(__dirname, "figma-render.js"), "utf8")
  .replace("/*__DESIGN__*/ null", JSON.stringify(design))
  .replace("/*__ICONS__*/ null", JSON.stringify(icons))
  .replace("/*__IMAGES__*/ null", JSON.stringify(images));
const baselinePath = process.argv[3];
const menu = [{ name: "生成 1.0 改版草稿", command: "redesign" }];
let code = `function runRedesign() {\n${redesign}\n}\n`;
if (baselinePath) {
  // Each tool keeps its own top-level names inside a function scope.
  code += `function runBaseline() {\n${fs.readFileSync(path.resolve(baselinePath), "utf8")}\n}\n`;
  menu.unshift({ name: "导入 0.4.5.20 现状（1:1）", command: "baseline" });
}
code += `if (figma.command === "baseline") runBaseline(); else runRedesign();\n`;
fs.writeFileSync(path.join(pluginDir, "code.js"), code);
fs.writeFileSync(path.join(pluginDir, "manifest.json"), JSON.stringify({
  name: "DACT 设计工具",
  id: "dact-design-tools",
  api: "1.0.0",
  main: "code.js",
  editorType: ["figma"],
  documentAccess: "dynamic-page",
  networkAccess: { allowedDomains: ["none"] },
  menu,
}, null, 2));
console.log(`${boards.length} boards -> ${previewDir}; plugin ${(code.length / 1048576).toFixed(1)} MiB (${menu.length} commands) -> ${pluginDir}`);
