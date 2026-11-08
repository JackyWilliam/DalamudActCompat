// Turns the 16×16 stroke icons in icons.js into polylines for ImGui, so the game draws the
// exact shapes used in the Figma design (no icon font). Curves and arcs are flattened here
// once; ImGui only strokes the resulting points.
//   node tools/ui-redesign/export-icons.js src/DalamudActCompat/UI/Kit/DactIconPaths.g.cs
"use strict";
const fs = require("fs");
const icons = require("./icons");

const NUMBER = /[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?/g;
// Segments per curve; 16 units drawn at most ~40 px, so this stays well below a pixel.
const CURVE_STEPS = 12;

function tokenize(d) {
  const out = [];
  for (const m of d.matchAll(/([MmLlHhVvCcSsQqTtAaZz])([^MmLlHhVvCcSsQqTtAaZz]*)/g)) {
    out.push({ cmd: m[1], args: (m[2].match(NUMBER) || []).map(Number) });
  }
  return out;
}

// SVG endpoint arc -> points (SVG 1.1 implementation notes, F.6.5).
function arcPoints(x1, y1, rx, ry, phi, large, sweep, x2, y2) {
  if (rx === 0 || ry === 0) return [[x2, y2]];
  const cos = Math.cos(phi * Math.PI / 180), sin = Math.sin(phi * Math.PI / 180);
  const dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
  const xp = cos * dx + sin * dy, yp = -sin * dx + cos * dy;
  rx = Math.abs(rx); ry = Math.abs(ry);
  const lambda = (xp * xp) / (rx * rx) + (yp * yp) / (ry * ry);
  if (lambda > 1) { rx *= Math.sqrt(lambda); ry *= Math.sqrt(lambda); }
  const num = rx * rx * ry * ry - rx * rx * yp * yp - ry * ry * xp * xp;
  const den = rx * rx * yp * yp + ry * ry * xp * xp;
  let co = Math.sqrt(Math.max(0, num / den));
  if (large === sweep) co = -co;
  const cxp = co * rx * yp / ry, cyp = -co * ry * xp / rx;
  const cx = cos * cxp - sin * cyp + (x1 + x2) / 2, cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
  const angle = (ux, uy, vx, vy) => {
    const a = Math.atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    return a;
  };
  const t1 = angle(1, 0, (xp - cxp) / rx, (yp - cyp) / ry);
  let dt = angle((xp - cxp) / rx, (yp - cyp) / ry, (-xp - cxp) / rx, (-yp - cyp) / ry);
  if (!sweep && dt > 0) dt -= 2 * Math.PI;
  if (sweep && dt < 0) dt += 2 * Math.PI;
  const steps = Math.max(4, Math.ceil(Math.abs(dt) / (Math.PI / 12)));
  const pts = [];
  for (let i = 1; i <= steps; i++) {
    const t = t1 + dt * i / steps;
    pts.push([cx + rx * Math.cos(t) * cos - ry * Math.sin(t) * sin, cy + rx * Math.cos(t) * sin + ry * Math.sin(t) * cos]);
  }
  return pts;
}

function flatten(d) {
  const paths = [];
  let cur = null, x = 0, y = 0, sx = 0, sy = 0, lastCtrl = null;
  const start = (nx, ny) => { cur = { points: [[nx, ny]], closed: false }; paths.push(cur); sx = nx; sy = ny; };
  const line = (nx, ny) => { if (!cur) start(x, y); cur.points.push([nx, ny]); };
  for (const { cmd, args } of tokenize(d)) {
    const rel = cmd === cmd.toLowerCase();
    const C = cmd.toUpperCase();
    const take = n => args.splice(0, n);
    if (C === "Z") { if (cur) cur.closed = true; x = sx; y = sy; cur = null; lastCtrl = null; continue; }
    let first = true;
    while (args.length) {
      const ox = rel ? x : 0, oy = rel ? y : 0;
      if (C === "M") {
        const [a, b] = take(2);
        if (first) start(ox + a, oy + b); else line(ox + a, oy + b);
        x = ox + a; y = oy + b; lastCtrl = null;
      } else if (C === "L") { const [a, b] = take(2); line(ox + a, oy + b); x = ox + a; y = oy + b; lastCtrl = null; }
      else if (C === "H") { const [a] = take(1); x = (rel ? x : 0) + a; line(x, y); lastCtrl = null; }
      else if (C === "V") { const [a] = take(1); y = (rel ? y : 0) + a; line(x, y); lastCtrl = null; }
      else if (C === "C" || C === "S") {
        let x1, y1, x2, y2, ex, ey;
        if (C === "C") { [x1, y1, x2, y2, ex, ey] = take(6); x1 += ox; y1 += oy; }
        else { [x2, y2, ex, ey] = take(4); [x1, y1] = lastCtrl ? [2 * x - lastCtrl[0], 2 * y - lastCtrl[1]] : [x, y]; }
        x2 += ox; y2 += oy; ex += ox; ey += oy;
        for (let i = 1; i <= CURVE_STEPS; i++) {
          const t = i / CURVE_STEPS, u = 1 - t;
          line(u * u * u * x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * ex,
               u * u * u * y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * ey);
        }
        lastCtrl = [x2, y2]; x = ex; y = ey;
      } else if (C === "Q") {
        let [x1, y1, ex, ey] = take(4); x1 += ox; y1 += oy; ex += ox; ey += oy;
        for (let i = 1; i <= CURVE_STEPS; i++) {
          const t = i / CURVE_STEPS, u = 1 - t;
          line(u * u * x + 2 * u * t * x1 + t * t * ex, u * u * y + 2 * u * t * y1 + t * t * ey);
        }
        lastCtrl = [x1, y1]; x = ex; y = ey;
      } else if (C === "A") {
        const [rx, ry, phi, large, sweep, a, b] = take(7);
        for (const p of arcPoints(x, y, rx, ry, phi, !!large, !!sweep, ox + a, oy + b)) line(p[0], p[1]);
        x = ox + a; y = oy + b; lastCtrl = null;
      } else throw new Error(`unsupported path command ${cmd} in ${d}`);
      first = false;
    }
  }
  // A closed path's final point repeats its start; ImGui's closed flag joins them itself.
  for (const p of paths) {
    const a = p.points[0], b = p.points[p.points.length - 1];
    if (p.closed && p.points.length > 2 && Math.hypot(a[0] - b[0], a[1] - b[1]) < 1e-3) p.points.pop();
  }
  return paths;
}

const f = n => {
  const v = Math.round(n * 1000) / 1000;
  return `${Number.isInteger(v) ? v : v}f`;
};
const lines = [];
for (const [name, d] of Object.entries(icons)) {
  const paths = flatten(d).map(p =>
    `new(${p.closed ? "true" : "false"}, [${p.points.map(([px, py]) => `new(${f(px)}, ${f(py)})`).join(", ")}])`);
  lines.push(`        ["${name}"] = [${paths.join(", ")}],`);
}
const out = `// <auto-generated>
// Generated by tools/ui-redesign/export-icons.js from tools/ui-redesign/icons.js; do not edit.
// </auto-generated>
using System.Numerics;

namespace DalamudActCompat.UI.Kit;

internal static partial class DactIcons
{
    private static readonly Dictionary<string, IconStroke[]> Strokes = new(StringComparer.Ordinal)
    {
${lines.join("\n")}
    };
}
`;
const target = process.argv[2];
if (!target) throw new Error("usage: export-icons.js <output .cs>");
fs.writeFileSync(target, out);
console.log(`${Object.keys(icons).length} icons -> ${target} (${out.length} chars)`);
