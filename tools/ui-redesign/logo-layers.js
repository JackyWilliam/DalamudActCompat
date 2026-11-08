// Splits assets/dact-logo.svg into the parts the splash animates (back orbit, three cards, three
// bars, front ring, star glow, star) and renders them with headless Edge into one transparent
// 4×3 atlas of 512-px cells: src/DalamudActCompat/Assets/dact-logo-layers.png. Every cell keeps
// the full 512×512 canvas, so drawing the cells in order at the same rectangle rebuilds the logo.
//   node tools/ui-redesign/logo-layers.js
// Cell order is a contract with ControlCenterWindow.Intro.cs (LogoLayer).
const fs = require("fs");
const path = require("path");
const os = require("os");
const { execFileSync } = require("child_process");

const repo = path.resolve(__dirname, "..", "..");
const svg = fs.readFileSync(path.join(__dirname, "assets", "dact-logo.svg"), "utf8");
const layers = [
  ["orbit-back", ["Vector"]],
  ["card-back", ["Vector_2", "Vector_3"]],
  ["card-mid", ["Vector_4", "Clip path group"]],
  ["card-front", ["Vector_8", "Clip path group_2"]],
  ["bar-1", ["Vector_12", "Vector_15"]],
  ["bar-2", ["Vector_13", "Vector_16"]],
  ["bar-3", ["Vector_14", "Vector_17"]],
  ["ring-front", ["Vector_18", "Vector_19"]],
  ["star-glow", ["Vector_20", "Vector_21"]],
  ["star", ["Vector_22", "Vector_23"]],
];

// The logo's parts are the direct children of its clipped root group.
const rootOpen = svg.match(/<g id="[^"]*" clip-path="url\(#([^)]+)\)">/);
const bodyStart = rootOpen.index + rootOpen[0].length;
const defsStart = svg.indexOf("<defs>");
const body = svg.slice(bodyStart, svg.lastIndexOf("</g>", defsStart));
const defs = svg.slice(defsStart, svg.indexOf("</defs>") + "</defs>".length);
const parts = new Map();
let depth = 0;
let start = -1;
for (const tag of body.matchAll(/<\/?[a-zA-Z][^>]*?\/?>/g)) {
  const text = tag[0];
  if (depth === 0) start = tag.index;
  if (text.startsWith("</")) depth--;
  else if (!text.endsWith("/>")) depth++;
  if (depth === 0) {
    const markup = body.slice(start, tag.index + text.length);
    const id = markup.match(/^<[a-z]+ id="([^"]+)"/)[1];
    parts.set(id, markup);
  }
}
const used = layers.flatMap(([, ids]) => ids);
const missing = [...parts.keys()].filter((id) => !used.includes(id));
const unknown = used.filter((id) => !parts.has(id));
if (missing.length || unknown.length || new Set(used).size !== used.length) {
  throw new Error(`Logo parts and layers disagree. Unassigned: ${missing.join(", ")}; unknown: ${unknown.join(", ")}`);
}

// Inline SVGs share one id namespace in a page, so each layer gets its own id prefix.
const prefixed = (markup, prefix) => markup
  .replace(/\bid="([^"]+)"/g, (_, id) => `id="${prefix}${id}"`)
  .replace(/url\(#([^)]+)\)/g, (_, id) => `url(#${prefix}${id})`);
const cells = layers.map(([name, ids], index) => {
  const content = `<g clip-path="url(#${rootOpen[1]})">${ids.map((id) => parts.get(id)).join("")}</g>${defs}`;
  const x = (index % 4) * 512;
  const y = Math.floor(index / 4) * 512;
  return `<svg data-layer="${name}" style="position:absolute;left:${x}px;top:${y}px" width="512" height="512" viewBox="0 0 512 512" fill="none" xmlns="http://www.w3.org/2000/svg">${prefixed(content, `l${index}-`)}</svg>`;
});
const html = `<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;background:transparent;width:2048px;height:1536px;overflow:hidden}</style></head><body>${cells.join("")}</body></html>`;

const work = fs.mkdtempSync(path.join(os.tmpdir(), "dact-logo-layers-"));
const page = path.join(work, "layers.html");
fs.writeFileSync(page, html);
const output = path.join(repo, "src", "DalamudActCompat", "Assets", "dact-logo-layers.png");
const edge = path.join(process.env["ProgramFiles(x86)"] ?? "C:\\Program Files (x86)", "Microsoft", "Edge", "Application", "msedge.exe");
execFileSync(edge, ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
  "--default-background-color=00000000", "--window-size=2048,1536", `--user-data-dir=${path.join(work, "profile")}`,
  `--screenshot=${output}`, "file:///" + page.replace(/\\/g, "/")], { stdio: "ignore" });
fs.rmSync(work, { recursive: true, force: true });
console.log(`Wrote ${layers.length} layers to ${path.relative(repo, output)}`);
