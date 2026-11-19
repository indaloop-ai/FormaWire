// Canvas viewer and CAD input for the FormaWire editor. All geometry decisions happen in C#;
// this file draws the scene (2D or 3D), handles pan/zoom/orbit, object snaps, ortho, rubber bands
// and grips, and reports picks and points back.
window.editor = (function () {
  let canvas, ctx, dotnet, scene = { entities: [], preview: [], pairs: [], snaps: [], labels: [], depth: 5 };
  let view = { scale: 20, ox: 0, oy: 0 };          // world → screen: sx = x*scale + ox ; sy = -y*scale + oy
  let mode3d = false;
  let rot = { yaw: -0.55, pitch: 1.05 };            // 3D orbit angles (rad)
  let tool = "select", phase = "";
  let drag = null;                                  // pan | box | orbit | grip
  let hover = -1, highlightId = -1;
  let spaceDown = false;
  let showRulings = true;
  let osnap = { on: true, end: true, mid: true, cen: true, int: true, perp: true, near: false, orig: true, ortho: false, polar: true, grid: true };
  let lockAngle = null;                             // degrees: the next point slides on this ray from the base (Tab)
  let tracking = null;                              // {angle, locked} while the point is held on a polar / locked ray
  const POLAR_STEP = 45, POLAR_TOL = 4;             // polar: hold the cursor on every 45° when within 4°
  let anchor = null;                                // [x,y]: first point of a two-point command (rubber band from here)
  let arcMid = null;                                // [x,y]: second point of a three-point arc
  let cursor = null;                                // last effective cursor point (world, after snap/ortho)
  let cursorPx = null;                              // last cursor position (screen)
  let snap = null;                                  // {x, y, kind} under the cursor
  let hotGrip = null;                               // grip clicked without dragging: the next click is its destination
  let frame = 0;
  const PICK_PX = 7, SNAP_PX = 12, GRIP_PX = 6;
  const POINT_TOOLS = new Set(["line", "arc", "circle", "move", "copy", "rotate", "mirror", "scale", "stretch", "measure", "origin"]);
  const ENTITY_TOOLS = new Set(["chain", "trim", "extend", "break", "fillet", "chamfer", "offset", "align", "startpt", "pickxy", "pickuv"]);
  const SEL_TOOLS = new Set(["move", "copy", "rotate", "mirror", "scale"]);   // work on the selection: until there is one, clicks select
  const TOOL_KEYS = { l: "line", a: "arc", m: "move", k: "copy", o: "rotate", i: "mirror", s: "stretch", t: "trim", e: "extend", b: "break", r: "fillet", p: "offset", d: "measure", c: "chain" };
  const SNAP_NAMES = { end: "Estremo", mid: "Punto medio", cen: "Centro", int: "Intersezione", perp: "Perpendicolare", near: "Vicino", orig: "Origine" };

  // ---- projection (3D: orthographic, yaw about Z then pitch about X)
  function proj3(x, y, z) {
    const cy = Math.cos(rot.yaw), sy = Math.sin(rot.yaw), cp = Math.cos(rot.pitch), sp = Math.sin(rot.pitch);
    const x1 = x * cy - y * sy, y1 = x * sy + y * cy;
    const h = z * cp + y1 * sp;                                // screen "up"
    return [x1 * view.scale + view.ox, -h * view.scale + view.oy];
  }
  function toScreen(p, z) { return mode3d ? proj3(p[0], p[1], z || 0) : [p[0] * view.scale + view.ox, -p[1] * view.scale + view.oy]; }
  function toWorld(sx, sy) { return [(sx - view.ox) / view.scale, (view.oy - sy) / view.scale]; }

  function init(canvasId, ref) {
    canvas = document.getElementById(canvasId); ctx = canvas.getContext("2d"); dotnet = ref;
    const ro = new ResizeObserver(() => { resize(); draw(); });
    ro.observe(canvas.parentElement);
    resize();
    canvas.addEventListener("mousedown", onDown);
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    canvas.addEventListener("wheel", onWheel, { passive: false });
    canvas.addEventListener("contextmenu", e => e.preventDefault());
    canvas.addEventListener("mouseleave", () => { snap = null; hover = -1; requestDraw(); });
    window.addEventListener("keydown", e => { if (e.code === "Space" && isCanvasKeyTarget(e)) { spaceDown = true; e.preventDefault(); } });
    window.addEventListener("keyup", e => { if (e.code === "Space") spaceDown = false; });
    window.addEventListener("keydown", onKey);
    initDrop();
    initDialogFocus();
    centreOrigin();
    draw();
  }
  // an empty sheet: the origin in the middle, about 40 mm across
  function centreOrigin() { view.scale = Math.max(canvas.width, 50) / 40; view.ox = canvas.width / 2; view.oy = canvas.height / 2; }

  // A file dragged over the window shows the drop layer; on drop the file goes to that layer's file input
  // (a Blazor InputFile, which streams it to the server). The layer hides when the drag stops coming.
  function initDrop() {
    let timer = 0;
    const isFileDrag = e => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files");
    window.addEventListener("dragover", e => {
      if (!isFileDrag(e)) return;
      e.preventDefault(); e.dataTransfer.dropEffect = "copy";
      document.body.classList.add("dragging");
      clearTimeout(timer); timer = setTimeout(() => document.body.classList.remove("dragging"), 400);
    });
    window.addEventListener("drop", e => {
      if (!isFileDrag(e)) return;
      e.preventDefault();                                   // never let the browser open the file itself
      clearTimeout(timer); document.body.classList.remove("dragging");
      const input = document.querySelector(".drop-input");
      if (!input || !e.dataTransfer.files.length) return;
      input.files = e.dataTransfer.files;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    });
  }
  function isTyping(e) {
    const t = e.target; if (!t) return false;
    if (t.tagName === "TEXTAREA" || t.tagName === "SELECT") return true;
    if (t.tagName !== "INPUT") return false;
    const ty = (t.type || "text").toLowerCase();
    return !(ty === "checkbox" || ty === "radio" || ty === "button" || ty === "file" || ty === "submit");
  }
  function isCanvasKeyTarget(e) {
    return !document.querySelector(".modal, .start") && (e.target === canvas || e.target === document.body);
  }
  function initDialogFocus() {
    let previousFocus = null, dialog = null;
    new MutationObserver(() => {
      const next = document.querySelector(".modal");
      if (next === dialog) return;
      if (next) { previousFocus = document.activeElement; next.focus(); }
      else if (previousFocus?.isConnected && !previousFocus.closest("[inert]")) previousFocus.focus();
      dialog = next;
    }).observe(document.querySelector(".app"), { childList: true, subtree: true });
  }

  function resize() {
    const r = canvas.parentElement.getBoundingClientRect();
    const w = Math.max(50, Math.floor(r.width)), h = Math.max(50, Math.floor(r.height));
    // what is in the middle of the view stays in the middle (the panel or the command line changed size)
    view.ox += (w - canvas.width) / 2; view.oy += (h - canvas.height) / 2;
    canvas.width = w; canvas.height = h;
  }

  function setScene(json, fit) {
    scene = JSON.parse(json);
    for (const k of ["pairs", "snaps", "labels", "preview"]) if (!scene[k]) scene[k] = [];
    hotGrip = null;
    if (fit) fitView();
    draw();
  }
  function setTool(t, ph) {
    tool = t; phase = ph || ""; hotGrip = null; snap = null; arcMid = null;
    canvas.style.cursor = t === "select" ? "default" : ENTITY_TOOLS.has(t) && !pickingSide() ? "pointer" : "crosshair";
    draw();
  }
  // offset: after the element, the next click only says on which side
  function pickingSide() { return tool === "offset" && phase === "side"; }
  function setAnchor(p) { anchor = p ? [p[0], p[1]] : null; lockAngle = null; draw(); }
  function setArcMid(p) { arcMid = p ? [p[0], p[1]] : null; draw(); }
  function setLock(deg) { lockAngle = deg === null || deg === undefined ? null : +deg; if (cursorPx && wantsPoint()) { const p = effectivePoint(cursorPx[0], cursorPx[1]); cursor = [p.x, p.y]; } draw(); }
  function setSnaps(s) { osnap = Object.assign(osnap, s); draw(); }
  function highlight(id) { highlightId = id; draw(); }
  function getCursor() { return cursor; }
  function setMode3d(on) { mode3d = !!on; anchor = null; fitView(); }
  function setRulings(on) { showRulings = !!on; draw(); }
  function resetView3d() { rot = { yaw: -0.55, pitch: 1.05 }; fitView(); }
  function reveal(id) {
    const el = document.getElementById(id); if (!el) return;
    el.scrollIntoView({ block: "nearest" });
    const inp = el.querySelector("input"); if (inp) inp.focus();
  }
  function blurCommand() { const c = document.getElementById("cmd"); if (c) c.blur(); }

  // ---- fitting
  function allScreenPoints() {
    const pts = [];
    for (const e of scene.entities) for (const p of e.pts) pts.push(toScreen(p, e.z));
    for (const e of scene.preview) for (const p of e.pts) pts.push(toScreen(p, e.z));
    return pts;
  }
  function fitTo(points) {
    if (!points.length) return;
    let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
    for (const s of points) { x0 = Math.min(x0, s[0]); y0 = Math.min(y0, s[1]); x1 = Math.max(x1, s[0]); y1 = Math.max(y1, s[1]); }
    // points are in screen space for the current view: rescale around them
    const w = Math.max(x1 - x0, 1), h = Math.max(y1 - y0, 1);
    const f = Math.min(canvas.width / w, canvas.height / h) * 0.85;
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
    view.scale *= f;
    view.ox = canvas.width / 2 + (view.ox - cx) * f;
    view.oy = canvas.height / 2 + (view.oy - cy) * f;
  }
  function fitView() { const pts = allScreenPoints(); if (pts.length || mode3d) fitTo(pts); else centreOrigin(); draw(); }
  function zoomSelection() {
    const sel = scene.entities.filter(e => e.sel);
    if (!sel.length) return fitView();
    const pts = []; for (const e of sel) for (const p of e.pts) pts.push(toScreen(p, e.z));
    fitTo(pts); view.scale *= 0.8; fitTo(pts); draw();
  }

  // ---- drawing
  function requestDraw() { if (!frame) frame = requestAnimationFrame(() => { frame = 0; draw(); }); }
  function draw() {
    if (!ctx) return;
    const W = canvas.width, H = canvas.height;
    ctx.fillStyle = "#100d16"; ctx.fillRect(0, 0, W, H);
    ctx.lineWidth = 1;
    if (mode3d) {
      // axes triad at origin
      const o = toScreen([0, 0], 0);
      const ax = toScreen([5, 0], 0), ay = toScreen([0, 5], 0), az = proj3(0, 0, 5);
      ctx.strokeStyle = "#a04040"; line(o, ax); ctx.strokeStyle = "#40a040"; line(o, ay); ctx.strokeStyle = "#4060c0"; line(o, az);
      ctx.fillStyle = "#b2abbf"; ctx.font = "11px sans-serif"; ctx.fillText("X", ax[0] + 4, ax[1]); ctx.fillText("Y", ay[0] + 4, ay[1]); ctx.fillText("Z", az[0] + 4, az[1]);
      if (showRulings) {
        for (const pr of scene.pairs) {
          ctx.strokeStyle = pr.color; ctx.lineWidth = 0.6;
          const n = Math.min(pr.a.length, pr.b.length);
          for (let i = 0; i < n; i++) line(toScreen(pr.a[i], pr.za), toScreen(pr.b[i], pr.zb));
        }
      }
    } else {
      if (osnap.grid) drawGrid(W, H);
      const o = toScreen([0, 0], 0);
      ctx.strokeStyle = "#5a3030"; line([0, o[1]], [W, o[1]]);
      ctx.strokeStyle = "#2f5a30"; line([o[0], 0], [o[0], H]);
      drawOriginMark(o);
    }
    for (const e of scene.entities) {
      if (e.sel) continue;
      ctx.strokeStyle = e.role === "xy" ? "#ff9f43" : e.role === "uv" ? "#48dbfb" : e.color;
      ctx.lineWidth = e.role ? (e.active === false ? 1 : 2) : 1;
      ctx.globalAlpha = e.role && e.active === false ? 0.55 : 1;
      poly(e.pts, e.z);
      ctx.globalAlpha = 1;
    }
    for (const e of scene.entities) {
      if (!e.sel) continue;
      ctx.strokeStyle = "#ffe066"; ctx.lineWidth = 2.5; poly(e.pts, e.z);
    }
    if (highlightId >= 0) { const e = scene.entities.find(x => x.id === highlightId); if (e) { ctx.strokeStyle = "#ff6dff"; ctx.lineWidth = 3.5; poly(e.pts, e.z); } }
    if (hover >= 0) { const e = scene.entities.find(x => x.id === hover); if (e) { ctx.strokeStyle = "#ffffff"; ctx.lineWidth = 2; poly(e.pts, e.z); } }
    for (const p of scene.preview) { ctx.strokeStyle = p.color; ctx.lineWidth = 1.5; poly(p.pts, p.z); }
    ctx.fillStyle = "#ffffff";
    for (const e of scene.entities) if (e.role) { dot(e.pts[0], e.z, 2); dot(e.pts[e.pts.length - 1], e.z, 2); }
    if (!mode3d) {
      drawDirection();
      drawLabels();
      drawGrips();
      drawRubberBand();
      drawSnap();
    }
    if (drag && drag.kind === "box") {
      const inside = drag.x1 >= drag.x0 && !drag.stretch;
      ctx.strokeStyle = drag.stretch ? "#ff9f43" : inside ? "#7bed9f" : "#70a1ff"; ctx.setLineDash(inside ? [] : [5, 4]);
      ctx.strokeRect(Math.min(drag.x0, drag.x1), Math.min(drag.y0, drag.y1), Math.abs(drag.x1 - drag.x0), Math.abs(drag.y1 - drag.y0));
      ctx.setLineDash([]);
    }
    ctx.fillStyle = "#b2abbf"; ctx.font = "12px sans-serif";
    ctx.fillText(mode3d ? `3D  rotazione ${(rot.yaw * 180 / Math.PI).toFixed(0)}°  inclinazione ${(rot.pitch * 180 / Math.PI).toFixed(0)}°  — trascina: ruota, destro: sposta, rotella: zoom, clic: seleziona` : (osnap.grid ? `griglia ${+gridStep().toPrecision(3)} mm · ` : "") + `${(100 / view.scale).toFixed(3)} mm / 100 px`, 8, H - 8);
  }
  // grid: the smallest 1-2-5 step at least 14 px wide, every 10 mm-ish line a little brighter
  function gridStep() {
    let s = Math.pow(10, Math.floor(Math.log10(14 / view.scale)));
    for (const k of [1, 2, 5, 10]) if (s * k * view.scale >= 14) return s * k;
    return s * 10;
  }
  function drawGrid(W, H) {
    const s = gridStep(), mant = Math.round(s / Math.pow(10, Math.floor(Math.log10(s) + 1e-9)));
    const every = mant === 1 ? 10 : mant === 2 ? 5 : 2;
    const [x0, y1] = toWorld(0, 0), [x1, y0] = toWorld(W, H);
    ctx.lineWidth = 1;
    for (let i = Math.ceil(x0 / s); i <= Math.floor(x1 / s); i++) {
      const sx = Math.round(i * s * view.scale + view.ox) + 0.5;
      ctx.strokeStyle = i % every === 0 ? "#292235" : "#1c1726"; line([sx, 0], [sx, H]);
    }
    for (let j = Math.ceil(y0 / s); j <= Math.floor(y1 / s); j++) {
      const sy = Math.round(-j * s * view.scale + view.oy) + 0.5;
      ctx.strokeStyle = j % every === 0 ? "#292235" : "#1c1726"; line([0, sy], [W, sy]);
    }
  }
  // the origin: X arrow (red), Y arrow (green), a square on 0,0
  function drawOriginMark(o) {
    const L = 30;
    ctx.lineWidth = 2;
    ctx.strokeStyle = ctx.fillStyle = "#e05a5a"; line(o, [o[0] + L, o[1]]);
    ctx.beginPath(); ctx.moveTo(o[0] + L + 6, o[1]); ctx.lineTo(o[0] + L, o[1] - 4); ctx.lineTo(o[0] + L, o[1] + 4); ctx.closePath(); ctx.fill();
    ctx.strokeStyle = ctx.fillStyle = "#5ac05a"; line(o, [o[0], o[1] - L]);
    ctx.beginPath(); ctx.moveTo(o[0], o[1] - L - 6); ctx.lineTo(o[0] - 4, o[1] - L); ctx.lineTo(o[0] + 4, o[1] - L); ctx.closePath(); ctx.fill();
    ctx.lineWidth = 1; ctx.strokeStyle = "#ddd"; ctx.strokeRect(o[0] - 3.5, o[1] - 3.5, 7, 7);
    ctx.font = "10px sans-serif"; ctx.fillStyle = "#e05a5a"; ctx.fillText("X", o[0] + L + 8, o[1] + 4);
    ctx.fillStyle = "#5ac05a"; ctx.fillText("Y", o[0] + 6, o[1] - L - 2);
  }
  function line(a, b) { ctx.beginPath(); ctx.moveTo(a[0], a[1]); ctx.lineTo(b[0], b[1]); ctx.stroke(); }
  function poly(pts, z) {
    if (pts.length < 2) return;
    ctx.beginPath(); const s0 = toScreen(pts[0], z); ctx.moveTo(s0[0], s0[1]);
    for (let i = 1; i < pts.length; i++) { const s = toScreen(pts[i], z); ctx.lineTo(s[0], s[1]); }
    ctx.stroke();
  }
  function dot(p, z, r) { const s = toScreen(p, z); ctx.beginPath(); ctx.arc(s[0], s[1], r, 0, 7); ctx.fill(); }

  // arrow on the first XY edge: the chain direction
  function drawDirection() {
    const xy = scene.entities.filter(e => e.role === "xy" && e.active !== false);
    if (!xy.length) return;
    const first = xy[0]; if (first.pts.length < 2) return;
    const a = toScreen(first.pts[0]), b = toScreen(first.pts[1]);
    const len = Math.hypot(b[0] - a[0], b[1] - a[1]); if (len < 1) return;
    const ux = (b[0] - a[0]) / len, uy = (b[1] - a[1]) / len, L = Math.min(18, len * 0.6);
    const tip = [a[0] + ux * L, a[1] + uy * L];
    ctx.strokeStyle = "#ff9f43"; ctx.fillStyle = "#ff9f43"; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(tip[0], tip[1]); ctx.lineTo(tip[0] - ux * 7 - uy * 4, tip[1] - uy * 7 + ux * 4); ctx.lineTo(tip[0] - ux * 7 + uy * 4, tip[1] - uy * 7 - ux * 4); ctx.closePath(); ctx.fill();
  }
  function drawLabels() {
    if (!scene.labels.length) return;
    ctx.font = "11px ui-monospace, monospace";
    // the highlighted edge first, then the others where they do not cover one already drawn (zoom in to see all)
    const placed = [];
    const order = scene.labels.filter(l => l.id === highlightId).concat(scene.labels.filter(l => l.id !== highlightId));
    for (const l of order) {
      const s = toScreen(l.p);
      const w = ctx.measureText(l.t).width + 6, box = [s[0] - w / 2, s[1] - 16, w, 14];
      if (placed.some(b => box[0] < b[0] + b[2] && b[0] < box[0] + box[2] && box[1] < b[1] + b[3] && b[1] < box[1] + box[3])) continue;
      placed.push(box);
      ctx.fillStyle = l.id === highlightId ? "rgba(90,30,90,.9)" : "rgba(16,13,22,.85)"; ctx.fillRect(box[0], box[1], box[2], box[3]);
      ctx.fillStyle = l.c; ctx.fillText(l.t, box[0] + 3, s[1] - 5);
    }
  }
  function grips() {
    if (tool !== "select") return [];
    const res = [];
    for (const e of scene.entities) if (e.sel && e.k === "L") { res.push({ id: e.id, p: e.pts[0] }); res.push({ id: e.id, p: e.pts[e.pts.length - 1] }); }
    return res;
  }
  function drawGrips() {
    ctx.fillStyle = "#3b7bff"; ctx.strokeStyle = "#e0e8ff"; ctx.lineWidth = 1;
    for (const g of grips()) {
      const s = toScreen(g.p);
      const hot = hotGrip && hotGrip.id === g.id && hotGrip.p[0] === g.p[0] && hotGrip.p[1] === g.p[1];
      ctx.fillStyle = hot ? "#ff3b3b" : "#3b7bff";
      ctx.fillRect(s[0] - 4, s[1] - 4, 8, 8); ctx.strokeRect(s[0] - 4, s[1] - 4, 8, 8);
    }
  }
  function drawRubberBand() {
    const a = drag && drag.kind === "grip" ? drag.from : hotGrip ? hotGrip.p : anchor;
    if (!a || !cursor) return;
    const sa = toScreen(a), sc = toScreen(cursor);
    const dx = cursor[0] - a[0], dy = cursor[1] - a[1];
    // ghosts: the selection moved / copied / rotated / mirrored
    if (anchor && (tool === "move" || tool === "copy" || tool === "rotate" || (tool === "mirror" && Math.hypot(dx, dy) > 1e-12))) {
      const ang = Math.atan2(dy, dx), c = Math.cos(ang), s = Math.sin(ang);
      const tf = tool === "rotate" ? p => [a[0] + (p[0] - a[0]) * c - (p[1] - a[1]) * s, a[1] + (p[0] - a[0]) * s + (p[1] - a[1]) * c]
        : tool === "mirror" ? p => { const t = (p[0] - a[0]) * c + (p[1] - a[1]) * s; return [2 * (a[0] + c * t) - p[0], 2 * (a[1] + s * t) - p[1]]; }
        : p => [p[0] + dx, p[1] + dy];
      ctx.strokeStyle = "rgba(255,224,102,.55)"; ctx.lineWidth = 1.5; ctx.setLineDash([4, 3]);
      for (const e of scene.entities) if (e.sel) poly(e.pts.map(tf));
      ctx.setLineDash([]);
    }
    // the circle / arc being drawn
    if (anchor && tool === "circle") { ctx.strokeStyle = "rgba(224,224,224,.8)"; ctx.lineWidth = 1.2; poly(circlePts(a, Math.hypot(dx, dy), 0, 2 * Math.PI)); }
    if (anchor && tool === "arc" && arcMid) {
      const arc = arcThrough(a, arcMid, cursor);
      ctx.strokeStyle = "rgba(224,224,224,.8)"; ctx.lineWidth = 1.2;
      if (arc) poly(circlePts(arc.c, arc.r, arc.a0, arc.sweep)); else poly([a, arcMid, cursor]);
      ctx.fillStyle = "#e0e0e0"; dot(arcMid, 0, 3);
      return;
    }
    // polar / locked angle: the tracking ray through the base point
    if (tracking) {
      const r = tracking.angle * Math.PI / 180, far = 1e4;
      ctx.strokeStyle = tracking.locked ? "rgba(255,159,67,.8)" : "rgba(123,237,159,.6)"; ctx.lineWidth = 1; ctx.setLineDash([2, 4]);
      line([sa[0] - Math.cos(r) * far, sa[1] + Math.sin(r) * far], [sa[0] + Math.cos(r) * far, sa[1] - Math.sin(r) * far]);
      ctx.setLineDash([]);
    }
    ctx.strokeStyle = "#e0e0e0"; ctx.lineWidth = 1; ctx.setLineDash([6, 4]); line(sa, sc); ctx.setLineDash([]);
    // dynamic readout next to the cursor
    const len = Math.hypot(dx, dy), deg = Math.atan2(dy, dx) * 180 / Math.PI;
    const how = tracking ? (tracking.locked ? `  bloccato ${+tracking.angle.toFixed(3)}°` : `  polare ${tracking.angle}°`) : "";
    const txt = (!anchor ? `${len.toFixed(4)}  <${deg.toFixed(2)}°   Δ ${dx.toFixed(4)}, ${dy.toFixed(4)}`
      : tool === "rotate" ? `${deg.toFixed(3)}°`
      : tool === "circle" ? `R ${len.toFixed(4)}  Ø ${(2 * len).toFixed(4)}`
      : tool === "mirror" ? `asse <${deg.toFixed(2)}°`
      : `${len.toFixed(4)}  <${deg.toFixed(2)}°   Δ ${dx.toFixed(4)}, ${dy.toFixed(4)}`) + how;
    ctx.font = "11px ui-monospace, monospace";
    const w = ctx.measureText(txt).width + 8;
    ctx.fillStyle = "rgba(16,13,22,.9)"; ctx.fillRect(sc[0] + 14, sc[1] + 10, w, 16);
    ctx.fillStyle = "#d5bcff"; ctx.fillText(txt, sc[0] + 18, sc[1] + 22);
  }
  function circlePts(c, r, a0, sweep) {
    const n = Math.max(8, Math.ceil(Math.abs(sweep) * 180 / Math.PI / 3)), pts = [];
    for (let i = 0; i <= n; i++) { const t = a0 + sweep * i / n; pts.push([c[0] + r * Math.cos(t), c[1] + r * Math.sin(t)]); }
    return pts;
  }
  // the arc from p0 through p1 to p2 (null when the three points are in line)
  function arcThrough(p0, p1, p2) {
    const d = 2 * (p0[0] * (p1[1] - p2[1]) + p1[0] * (p2[1] - p0[1]) + p2[0] * (p0[1] - p1[1]));
    const size = Math.max(Math.hypot(p1[0] - p0[0], p1[1] - p0[1]), Math.hypot(p2[0] - p0[0], p2[1] - p0[1]));
    if (size === 0 || Math.abs(d) < 1e-9 * size * size) return null;
    const s0 = p0[0] ** 2 + p0[1] ** 2, s1 = p1[0] ** 2 + p1[1] ** 2, s2 = p2[0] ** 2 + p2[1] ** 2;
    const c = [(s0 * (p1[1] - p2[1]) + s1 * (p2[1] - p0[1]) + s2 * (p0[1] - p1[1])) / d, (s0 * (p2[0] - p1[0]) + s1 * (p0[0] - p2[0]) + s2 * (p1[0] - p0[0])) / d];
    const a0 = Math.atan2(p0[1] - c[1], p0[0] - c[0]), a2 = Math.atan2(p2[1] - c[1], p2[0] - c[0]);
    const ccw = (p1[0] - p0[0]) * (p2[1] - p1[1]) - (p1[1] - p0[1]) * (p2[0] - p1[0]) > 0;
    const norm = x => ((x % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
    return { c, r: Math.hypot(p0[0] - c[0], p0[1] - c[1]), a0, sweep: ccw ? norm(a2 - a0) : -norm(a0 - a2) };
  }
  function drawSnap() {
    if (!snap) return;
    const s = toScreen([snap.x, snap.y]); const r = 6;
    ctx.strokeStyle = "#7bed9f"; ctx.lineWidth = 2;
    ctx.beginPath();
    switch (snap.kind) {
      case "end": ctx.rect(s[0] - r, s[1] - r, 2 * r, 2 * r); break;
      case "mid": ctx.moveTo(s[0], s[1] - r); ctx.lineTo(s[0] + r, s[1] + r); ctx.lineTo(s[0] - r, s[1] + r); ctx.closePath(); break;
      case "cen": ctx.arc(s[0], s[1], r, 0, 7); break;
      case "int": ctx.moveTo(s[0] - r, s[1] - r); ctx.lineTo(s[0] + r, s[1] + r); ctx.moveTo(s[0] + r, s[1] - r); ctx.lineTo(s[0] - r, s[1] + r); break;
      case "perp": ctx.moveTo(s[0] - r, s[1] - r); ctx.lineTo(s[0] - r, s[1] + r); ctx.lineTo(s[0] + r, s[1] + r); ctx.moveTo(s[0] - r, s[1]); ctx.lineTo(s[0], s[1]); ctx.lineTo(s[0], s[1] + r); break;
      case "orig": ctx.arc(s[0], s[1], r, 0, 7); ctx.moveTo(s[0] - r - 3, s[1]); ctx.lineTo(s[0] + r + 3, s[1]); ctx.moveTo(s[0], s[1] - r - 3); ctx.lineTo(s[0], s[1] + r + 3); break;
      case "near": ctx.moveTo(s[0] - r, s[1] - r); ctx.lineTo(s[0] + r, s[1] - r); ctx.lineTo(s[0] - r, s[1] + r); ctx.lineTo(s[0] + r, s[1] + r); ctx.closePath(); break;
    }
    ctx.stroke();
    ctx.font = "11px sans-serif"; ctx.fillStyle = "#7bed9f"; ctx.fillText(SNAP_NAMES[snap.kind], s[0] + 10, s[1] - 8);
  }

  // ---- picking
  function distToSeg(px, py, a, b) {
    const vx = b[0] - a[0], vy = b[1] - a[1]; const l2 = vx * vx + vy * vy;
    let t = l2 ? ((px - a[0]) * vx + (py - a[1]) * vy) / l2 : 0; t = Math.max(0, Math.min(1, t));
    const dx = px - (a[0] + t * vx), dy = py - (a[1] + t * vy); return Math.sqrt(dx * dx + dy * dy);
  }
  function hitEntity(sx, sy) {
    let best = null, bd = PICK_PX;
    for (const e of scene.entities) {
      const s = e.pts.map(p => toScreen(p, e.z));
      for (let i = 1; i < s.length; i++) { const d = distToSeg(sx, sy, s[i - 1], s[i]); if (d < bd) { bd = d; best = e; } }
    }
    return best;
  }
  function hitTest(sx, sy) { const e = hitEntity(sx, sy); return e ? e.id : -1; }
  function gripAt(sx, sy) {
    for (const g of grips()) { const s = toScreen(g.p); if (Math.abs(s[0] - sx) <= GRIP_PX && Math.abs(s[1] - sy) <= GRIP_PX) return g; }
    return null;
  }

  // nearest point of an entity to w (world), exact for lines and arcs
  function nearestOn(e, w) {
    if (e.k === "L") { const a = e.pts[0], b = e.pts[1]; return footOnSeg(w, a, b, true); }
    if (e.k === "A") {
      const d = Math.hypot(w[0] - e.c[0], w[1] - e.c[1]); if (d < 1e-12) return null;
      const p = [e.c[0] + (w[0] - e.c[0]) / d * e.r, e.c[1] + (w[1] - e.c[1]) / d * e.r];
      return onArc(e, p) ? p : null;
    }
    let best = null, bd = Infinity;
    for (let i = 1; i < e.pts.length; i++) { const q = footOnSeg(w, e.pts[i - 1], e.pts[i], true); const d = Math.hypot(q[0] - w[0], q[1] - w[1]); if (d < bd) { bd = d; best = q; } }
    return best;
  }
  function footOnSeg(w, a, b, clamp) {
    const vx = b[0] - a[0], vy = b[1] - a[1], l2 = vx * vx + vy * vy;
    let t = l2 ? ((w[0] - a[0]) * vx + (w[1] - a[1]) * vy) / l2 : 0;
    if (clamp) t = Math.max(0, Math.min(1, t)); else if (t < -1e-9 || t > 1 + 1e-9) return null;
    return [a[0] + t * vx, a[1] + t * vy];
  }
  // is a point of the circle on the sampled arc? (closest sample within one sampling step)
  function onArc(e, p) {
    let bd = Infinity; for (const q of e.pts) bd = Math.min(bd, Math.hypot(q[0] - p[0], q[1] - p[1]));
    const step = e.pts.length > 1 ? Math.hypot(e.pts[1][0] - e.pts[0][0], e.pts[1][1] - e.pts[0][1]) : 0;
    return bd <= step * 0.6 + 1e-9;
  }
  function perpOn(e, from) {
    if (e.k === "L") return footOnSeg(from, e.pts[0], e.pts[1], false);
    if (e.k === "A") return nearestOn(e, from);
    return null;
  }

  function findSnap(sx, sy) {
    if (!osnap.on || mode3d) return null;
    let best = null, bd = SNAP_PX;
    const consider = (p, kind) => { if (!p) return; const s = toScreen(p); const d = Math.hypot(s[0] - sx, s[1] - sy); if (d < bd) { bd = d; best = { x: p[0], y: p[1], kind }; } };
    for (const e of scene.entities) {
      if (osnap.end && e.pts.length) { consider(e.pts[0], "end"); consider(e.pts[e.pts.length - 1], "end"); }
      if (osnap.mid && e.mid) consider(e.mid, "mid");
      if (osnap.cen && e.c) consider(e.c, "cen");
    }
    if (osnap.int) for (const p of scene.snaps) consider(p, "int");
    if (osnap.orig) consider([0, 0], "orig");
    if (best) return best;
    const e = hitEntity(sx, sy); if (!e) return null;
    const base = currentBase();
    if (osnap.perp && base) { const p = perpOn(e, base); if (p) return { x: p[0], y: p[1], kind: "perp" }; }
    if (osnap.near) { const p = nearestOn(e, toWorld(sx, sy)); if (p) return { x: p[0], y: p[1], kind: "near" }; }
    return null;
  }
  // the first point of the command in progress (rubber band from here)
  function currentBase() { return anchor || (drag && drag.kind === "grip" ? drag.from : hotGrip ? hotGrip.p : null); }
  function onRay(b, deg, p) {
    const r = deg * Math.PI / 180, ux = Math.cos(r), uy = Math.sin(r);
    const t = (p[0] - b[0]) * ux + (p[1] - b[1]) * uy;
    return [b[0] + ux * t, b[1] + uy * t];
  }
  // The point a click means. A locked angle (Tab) wins: the point slides on that ray (a snapped point is projected
  // onto it). Otherwise an object snap, else the cursor: horizontal/vertical with ortho, or held on the nearest
  // multiple of 45° when the cursor is within a few degrees of it (polar).
  function effectivePoint(sx, sy) {
    const base = currentBase();
    tracking = null;
    snap = findSnap(sx, sy);
    if (base && lockAngle !== null) {
      const q = onRay(base, lockAngle, snap ? [snap.x, snap.y] : toWorld(sx, sy));
      tracking = { angle: lockAngle, locked: true };
      return { x: q[0], y: q[1], snapped: !!snap };
    }
    if (snap) return { x: snap.x, y: snap.y, snapped: true };
    let w = toWorld(sx, sy);
    if (base && osnap.ortho) w = Math.abs(w[0] - base[0]) >= Math.abs(w[1] - base[1]) ? [w[0], base[1]] : [base[0], w[1]];
    else if (base && osnap.polar) {
      const dx = w[0] - base[0], dy = w[1] - base[1];
      if (Math.hypot(dx, dy) * view.scale > 12) {
        const a = Math.atan2(dy, dx) * 180 / Math.PI, k = Math.round(a / POLAR_STEP) * POLAR_STEP;
        if (Math.abs(a - k) <= POLAR_TOL) { w = onRay(base, k, w); tracking = { angle: ((k % 360) + 360) % 360, locked: false }; }
      }
    }
    return { x: w[0], y: w[1], snapped: false };
  }
  function wantsPoint() {
    if (mode3d) return false;
    if (drag && drag.kind === "grip" || hotGrip) return true;
    if (tool === "break" || tool === "startpt" || pickingSide()) return true;
    if (!POINT_TOOLS.has(tool)) return false;
    if (tool === "stretch" && phase !== "points") return false;
    if (SEL_TOOLS.has(tool) && !hasSelection()) return false;
    return true;
  }
  function hasSelection() { return scene.entities.some(e => e.sel); }

  function boxSelect(x0, y0, x1, y1) {
    const crossing = x1 < x0;
    const L = Math.min(x0, x1), R = Math.max(x0, x1), T = Math.min(y0, y1), B = Math.max(y0, y1);
    const ids = [];
    for (const e of scene.entities) {
      const s = e.pts.map(p => toScreen(p, e.z));
      const inside = p => p[0] >= L && p[0] <= R && p[1] >= T && p[1] <= B;
      if (!crossing) { if (s.every(inside)) ids.push(e.id); }
      else {
        if (s.some(inside)) { ids.push(e.id); continue; }
        let hit = false;
        for (let i = 1; i < s.length && !hit; i++) hit = segRect(s[i - 1], s[i], L, T, R, B);
        if (hit) ids.push(e.id);
      }
    }
    return ids;
  }
  function segRect(a, b, L, T, R, B) {
    const edges = [[[L, T], [R, T]], [[R, T], [R, B]], [[R, B], [L, B]], [[L, B], [L, T]]];
    return edges.some(([c, d]) => segInter(a, b, c, d));
  }
  function segInter(a, b, c, d) {
    const o = (p, q, r) => Math.sign((q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0]));
    return o(a, b, c) !== o(a, b, d) && o(c, d, a) !== o(c, d, b);
  }

  // ---- events
  function pos(e) { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
  function onDown(e) {
    canvas.focus({ preventScroll: true });
    const [sx, sy] = pos(e);
    if (e.button === 1 || e.button === 2 || spaceDown) { drag = { kind: "pan", x0: sx, y0: sy, ox: view.ox, oy: view.oy }; e.preventDefault(); return; }
    if (e.button !== 0) return;
    if (mode3d) { drag = { kind: "orbit", x0: sx, y0: sy, yaw: rot.yaw, pitch: rot.pitch, moved: false, shift: e.shiftKey }; return; }
    if (hotGrip) { const p = effectivePoint(sx, sy); const g = hotGrip; hotGrip = null; dotnet.invokeMethodAsync("OnGrip", g.id, g.p[0], g.p[1], p.x, p.y); return; }
    const selectLike = tool === "select" || (SEL_TOOLS.has(tool) && !hasSelection());
    if (selectLike) {
      const g = tool === "select" ? gripAt(sx, sy) : null;
      if (g) { drag = { kind: "grip", id: g.id, from: g.p, x0: sx, y0: sy, moved: false }; return; }
      const id = hitTest(sx, sy);
      if (id >= 0) dotnet.invokeMethodAsync("OnPick", id, e.shiftKey, e.ctrlKey || e.metaKey, "select");
      else drag = { kind: "box", x0: sx, y0: sy, x1: sx, y1: sy, shift: e.shiftKey };
    } else if (tool === "stretch" && phase !== "points") {
      drag = { kind: "box", stretch: true, x0: sx, y0: sy, x1: sx, y1: sy };
    } else if (ENTITY_TOOLS.has(tool) && !pickingSide()) {
      const id = hitTest(sx, sy);
      const p = tool === "break" || tool === "startpt" ? effectivePoint(sx, sy) : (w => ({ x: w[0], y: w[1] }))(toWorld(sx, sy));
      if (id >= 0) dotnet.invokeMethodAsync("OnEntityClick", id, p.x, p.y, tool);
      else if (tool === "chain") dotnet.invokeMethodAsync("OnPick", -1, e.shiftKey, false, tool);
    } else if (POINT_TOOLS.has(tool) || pickingSide()) {
      const p = effectivePoint(sx, sy);
      dotnet.invokeMethodAsync("OnPoint", p.x, p.y, p.snapped, tool);
    }
  }
  function onMove(e) {
    if (!canvas) return;
    const [sx, sy] = pos(e);
    cursorPx = [sx, sy];
    if (drag && drag.kind === "pan") { view.ox = drag.ox + (sx - drag.x0); view.oy = drag.oy + (sy - drag.y0); requestDraw(); return; }
    if (drag && drag.kind === "box") { drag.x1 = sx; drag.y1 = sy; requestDraw(); return; }
    if (drag && drag.kind === "orbit") {
      if (Math.abs(sx - drag.x0) > 2 || Math.abs(sy - drag.y0) > 2) drag.moved = true;
      rot.yaw = drag.yaw + (sx - drag.x0) * 0.01; rot.pitch = Math.max(0, Math.min(Math.PI, drag.pitch + (sy - drag.y0) * 0.01)); requestDraw(); return;
    }
    if (drag && drag.kind === "grip" && (Math.abs(sx - drag.x0) > 3 || Math.abs(sy - drag.y0) > 3)) drag.moved = true;
    if (wantsPoint()) { const p = effectivePoint(sx, sy); cursor = [p.x, p.y]; }
    else { snap = null; tracking = null; cursor = mode3d ? null : toWorld(sx, sy); }
    if (!mode3d && cursor) { const el = document.getElementById("coords"); if (el) el.textContent = `X ${cursor[0].toFixed(4)}  Y ${cursor[1].toFixed(4)}`; }
    const hovering = tool === "select" || (ENTITY_TOOLS.has(tool) && !pickingSide()) || mode3d || (SEL_TOOLS.has(tool) && !hasSelection());
    const h = hovering && !(drag && drag.kind === "grip") ? hitTest(sx, sy) : -1;
    if (h !== hover) hover = h;
    requestDraw();
  }
  function onUp(e) {
    if (!drag) return;
    const [sx, sy] = pos(e);
    if (drag.kind === "box") {
      const tiny = Math.abs(sx - drag.x0) < 3 && Math.abs(sy - drag.y0) < 3;
      if (drag.stretch) {
        if (!tiny) { const a = toWorld(drag.x0, drag.y0), b = toWorld(sx, sy); dotnet.invokeMethodAsync("OnStretchBox", Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[0], b[0]), Math.max(a[1], b[1])); }
      } else if (tiny) { if (!drag.shift) dotnet.invokeMethodAsync("OnPick", -1, false, false, "select"); }
      else { const ids = boxSelect(drag.x0, drag.y0, sx, sy); dotnet.invokeMethodAsync("OnBoxSelect", ids, drag.shift); }
    } else if (drag.kind === "orbit" && !drag.moved) {
      const id = hitTest(sx, sy);
      dotnet.invokeMethodAsync("OnPick", id, drag.shift, false, "select");
    } else if (drag.kind === "grip") {
      if (drag.moved) { const p = effectivePoint(sx, sy); dotnet.invokeMethodAsync("OnGrip", drag.id, drag.from[0], drag.from[1], p.x, p.y); }
      else hotGrip = { id: drag.id, p: drag.from };
    }
    drag = null; draw();
  }
  function onWheel(e) {
    e.preventDefault();
    const [sx, sy] = pos(e);
    const f = e.deltaY < 0 ? 1.15 : 1 / 1.15;
    view.ox = sx + (view.ox - sx) * f; view.oy = sy + (view.oy - sy) * f; view.scale *= f;
    if (cursorPx && wantsPoint()) { const p = effectivePoint(cursorPx[0], cursorPx[1]); cursor = [p.x, p.y]; }
    draw();
  }
  function onKey(e) {
    const dialog = document.querySelector(".modal");
    if (dialog) {
      if (e.key === "Escape") { dotnet.invokeMethodAsync("OnKey", "Escape"); e.preventDefault(); }
      if (e.key === "Tab") {
        const controls = Array.from(dialog.querySelectorAll("button:not(:disabled), input, select, [tabindex='0']"));
        const first = controls[0], last = controls[controls.length - 1];
        if (e.shiftKey && (e.target === first || e.target === dialog)) { last?.focus(); e.preventDefault(); }
        else if (!e.shiftKey && (e.target === last || e.target === dialog)) { first?.focus(); e.preventDefault(); }
      }
      return;
    }
    if (document.querySelector(".start")) {
      if (e.key === "F1") { dotnet.invokeMethodAsync("OnKey", "Help"); e.preventDefault(); }
      return;
    }
    // Tab (in the drawing or the command line): type the angle of the segment instead of its length
    if (e.key === "Tab") {
      const c = document.getElementById("cmd");
      if (!e.shiftKey && anchor && ["line", "move", "copy", "mirror", "stretch", "measure"].includes(tool) && (e.target === c || isCanvasKeyTarget(e))) {
        e.preventDefault();
        if (c) c.focus();
        dotnet.invokeMethodAsync("OnTab", c ? c.value : "");
        return;
      }
    }
    if (isTyping(e)) return;
    // Let buttons, disclosures, file inputs and checkboxes retain normal keyboard behavior.
    if (e.target.closest?.("button, summary, input, a") && !["Escape", "F1", "F7"].includes(e.key)) return;
    const k = e.key, ctrl = e.ctrlKey || e.metaKey;
    if (k === "Escape") { if (hotGrip) { hotGrip = null; draw(); return; } dotnet.invokeMethodAsync("OnKey", "Escape"); }
    else if (k === "Enter") dotnet.invokeMethodAsync("OnKey", "Enter");
    else if (k === "Delete" || k === "Backspace") { dotnet.invokeMethodAsync("OnKey", "Delete"); e.preventDefault(); }
    else if (ctrl && k.toLowerCase() === "z") { dotnet.invokeMethodAsync("OnKey", e.shiftKey ? "Redo" : "Undo"); e.preventDefault(); }
    else if (ctrl && k.toLowerCase() === "y") { dotnet.invokeMethodAsync("OnKey", "Redo"); e.preventDefault(); }
    else if (ctrl && k.toLowerCase() === "a") { dotnet.invokeMethodAsync("OnKey", "SelectAll"); e.preventDefault(); }
    else if (k === "F8") { dotnet.invokeMethodAsync("OnKey", "Ortho"); e.preventDefault(); }
    else if (k === "F7") { dotnet.invokeMethodAsync("OnKey", "Grid"); e.preventDefault(); }
    else if (k === "F10") { dotnet.invokeMethodAsync("OnKey", "Polar"); e.preventDefault(); }
    else if (k === "F1") { dotnet.invokeMethodAsync("OnKey", "Help"); e.preventDefault(); }
    else if (k === "F3") { dotnet.invokeMethodAsync("OnKey", "Snap"); e.preventDefault(); }
    else if (!ctrl && !e.altKey) {
      const l = k.toLowerCase();
      if (l === "f") fitView();
      else if (l === "z") zoomSelection();
      else if (TOOL_KEYS[l]) dotnet.invokeMethodAsync("OnKey", "tool:" + TOOL_KEYS[l]);
      else if (/^[0-9@.\-]$/.test(k)) {
        // typing a coordinate goes to the command line
        const c = document.getElementById("cmd");
        if (c) { c.focus(); c.value += k; c.dispatchEvent(new Event("input", { bubbles: true })); e.preventDefault(); }
      }
    }
  }

  function download(name, text) {
    const blob = new Blob([text], { type: "application/dxf" });
    const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = name; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  function screenOf(x, y) { const r = canvas.getBoundingClientRect(); const s = toScreen([x, y], 0); return [s[0] + r.left, s[1] + r.top]; }

  return { init, setScene, setTool, setAnchor, setArcMid, setLock, setSnaps, highlight, cursor: getCursor, setMode3d, setRulings, resetView3d, fitView, zoomSelection, download, reveal, blurCommand, screenOf };
})();
