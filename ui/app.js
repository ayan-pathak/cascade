"use strict";
// One script for every Cascade surface; ?view= picks which one this window renders.
const VIEW = new URLSearchParams(location.search).get("view") || "desktop";
document.body.dataset.view = VIEW;
const wv = window.chrome && window.chrome.webview;
const send = (type, o = {}) => { const m = Object.assign({ type }, o); if (wv) wv.postMessage(m); else console.log("→", m); };
const $ = id => document.getElementById(id);
const root = $("root");
const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const U = id => `https://images.unsplash.com/photo-${id}?w=2400&q=80`;
const PRESETS = [U("1464457312035-3d7d0e0c058e"), U("1502252430442-aac78f397426"), U("1583904547022-1d2cb87b9565")];
const DEF = {
  wall: PRESETS[0], acc: "#a8b9c7", acc2: "#a8c7af", tintHue: 150, tintSat: .2,
  dark: 55, wb: 0, sat: 100, pa: 66, bl: 14, ti: 60, palette: [],
  iconTint: true, clock24: false, widgets: true, label: "Tahoma · Cascade Range",
  city: "Seattle", lat: 47.61, lon: -122.33, units: "c",
  fontUrl: "", fontName: "", dispClock: true,
  iconColor: 35, dockAutoHide: true, maximizeNew: true, icsUrl: "",
  fontAll: false, calView: "week", calHidden: [], layout: {}
};

const st = {
  settings: Object.assign({}, DEF), config: { apps: [], layouts: [], hotkeys: {} },
  scale: 1, screen: { w: 1440, h: 810 }, rect: { x: 0, y: 0, w: 1440, h: 810 },
  windows: { groups: [], list: [], fg: null }, stats: null, media: null, sessions: [],
  desktops: { count: 1, current: 0 }, icons: {}, wallpapers: [], startApps: null, ready: false
};

// ---------------- colour helpers ----------------
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
function rgb2hsl(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const mx = Math.max(r, g, b), mn = Math.min(r, g, b);
  let h = 0, s = 0; const l = (mx + mn) / 2;
  if (mx !== mn) {
    const d = mx - mn; s = l > .5 ? d / (2 - mx - mn) : d / (mx + mn);
    h = mx === r ? (g - b) / d + (g < b ? 6 : 0) : mx === g ? (b - r) / d + 2 : (r - g) / d + 4; h *= 60;
  }
  return [h, s, l];
}
function hsl2rgb(h, s, l) {
  h = ((h % 360) + 360) % 360 / 360;
  const f = n => { const k = (n + h * 12) % 12, a = s * Math.min(l, 1 - l); return Math.round(255 * (l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1)))); };
  return [f(0), f(8), f(4)];
}
const hex = a => "#" + a.map(v => v.toString(16).padStart(2, "0")).join("");
const hexToRgb = h => [1, 3, 5].map(i => parseInt(h.substr(i, 2), 16) || 0);

// Pulls a palette (5 swatches) and a shadow tint from a photo.
function analyze(img) {
  const c = document.createElement("canvas"); c.width = 96; c.height = 54;
  const x = c.getContext("2d"); x.drawImage(img, 0, 0, 96, 54);
  let d; try { d = x.getImageData(0, 0, 96, 54).data; } catch (e) { return null; }
  const bins = Array.from({ length: 18 }, () => ({ w: 0, hx: 0, hy: 0, s: 0 }));
  const dk = { hx: 0, hy: 0, s: 0, n: 0 };
  for (let i = 0; i < d.length; i += 4) {
    const [h, s, l] = rgb2hsl(d[i], d[i + 1], d[i + 2]); const rad = h * Math.PI / 180;
    if (l < .3) { dk.hx += Math.cos(rad) * s; dk.hy += Math.sin(rad) * s; dk.s += s; dk.n++; }
    if (s < .06 || l < .1 || l > .92) continue;
    const w = s * (1 - Math.abs(l - .5)); const b = bins[Math.floor(h / 20) % 18];
    b.w += w; b.hx += Math.cos(rad) * w; b.hy += Math.sin(rad) * w; b.s += s * w;
  }
  const pal = bins.filter(b => b.w > 0).sort((a, b) => b.w - a.w).map(b => ({ h: (Math.atan2(b.hy, b.hx) * 180 / Math.PI + 360) % 360, s: b.s / b.w }));
  const out = [];
  pal.forEach(p => { if (out.length < 5 && out.every(o => Math.min(Math.abs(o.h - p.h), 360 - Math.abs(o.h - p.h)) > 22)) out.push(p); });
  const th = dk.n ? (Math.atan2(dk.hy, dk.hx) * 180 / Math.PI + 360) % 360 : (out[0] ? out[0].h : 200);
  const ts = dk.n ? clamp(dk.s / dk.n, .05, .4) : .15;
  if (!out.length) out.push({ h: th, s: .12 });
  const swatches = out.map(p => hex(hsl2rgb(p.h, clamp(p.s * 1.25, .22, .62), .72)));
  while (swatches.length < 5) { const b = rgb2hsl(...hexToRgb(swatches[0])); swatches.push(hex(hsl2rgb(b[0] + [150, 40, -40, 200][swatches.length - 1], .45, .72))); }
  return { swatches, tintHue: th, tintSat: ts };
}
function pickSecond(sw) {
  const a = rgb2hsl(...hexToRgb(sw[0]));
  for (const c of sw.slice(1)) { const b = rgb2hsl(...hexToRgb(c)); const dd = Math.min(Math.abs(a[0] - b[0]), 360 - Math.abs(a[0] - b[0])); if (dd > 45) return c; }
  return hex(hsl2rgb(a[0] + 150, .5, .72));
}

// ---------------- theme ----------------
function applyTheme() {
  const S = st.settings, rs = document.documentElement.style;
  const a = rgb2hsl(...hexToRgb(S.acc || DEF.acc));
  const tint = hsl2rgb(S.tintHue, S.tintSat * S.ti / 100, .05 + .03 * S.ti / 100);
  rs.setProperty("--wall", `url("${S.wall}")`);
  rs.setProperty("--acc", S.acc); rs.setProperty("--acc2", S.acc2);
  rs.setProperty("--tint", tint.join(","));
  rs.setProperty("--txt", hex(hsl2rgb(a[0], .14, .91)));
  rs.setProperty("--fog", hex(hsl2rgb(a[0], .1, .64)));
  rs.setProperty("--dim", hex(hsl2rgb(a[0], .1, .41)));
  const k = S.dark / 100;
  rs.setProperty("--grade", `rgba(${tint.join(",")},${k})`);
  rs.setProperty("--mid", `rgba(0,0,0,${(k * .45).toFixed(2)})`);
  rs.setProperty("--wb", S.wb + "px"); rs.setProperty("--ws", S.sat / 100);
  rs.setProperty("--pa", S.pa / 100); rs.setProperty("--bl", S.bl + "px");
  document.body.classList.toggle("tint", !!S.iconTint);
  document.body.classList.toggle("dispOn", S.dispClock !== false);
  document.body.classList.toggle("fontAll", !!S.fontAll);
  document.body.classList.toggle("custFont", !!S.fontUrl);
  // an imported display font (e.g. Harmond) becomes "CascadeDisplay"
  let ff = $("ff");
  if (!ff) { ff = document.createElement("style"); ff.id = "ff"; document.head.appendChild(ff); }
  const css = S.fontUrl ? `@font-face{font-family:"CascadeDisplay";src:url("${S.fontUrl}");font-display:swap}` : "";
  if (ff.textContent !== css) ff.textContent = css;
  // duotone for dock icons: shadows -> tint, highlights -> accent
  const acc = hexToRgb(S.acc), lo = tint.map(v => Math.min(255, v + 40));
  const keep = clamp((S.iconColor == null ? 35 : S.iconColor) / 100, 0, 1);
  $("duoMix").setAttribute("k2", keep.toFixed(2)); $("duoMix").setAttribute("k3", (1 - keep).toFixed(2));
  ["duoR", "duoG", "duoB"].forEach((id, i) => $(id).setAttribute("tableValues", `${(lo[i] / 255).toFixed(3)} ${(acc[i] / 255).toFixed(3)} ${Math.min(1, acc[i] / 255 + .12).toFixed(3)}`));
}

let pushTimer = 0;
function setSettings(patch, push = true) {
  Object.assign(st.settings, patch);
  applyTheme();
  if (VIEW === "personalize") renderPersDyn();
  if (VIEW === "desktop") renderDesktopDyn();
  if (push) { clearTimeout(pushTimer); pushTimer = setTimeout(() => send("settings", { settings: st.settings }), 40); }
}

// Loads a wallpaper; when auto is on, re-derives the colours from it.
function setWall(url, auto = true) {
  setSettings({ wall: url }, !auto);
  if (!auto) return;
  const img = new Image(); img.crossOrigin = "anonymous";
  img.onload = () => {
    const r = analyze(img);
    if (r) setSettings({ palette: r.swatches, acc: r.swatches[0], acc2: pickSecond(r.swatches), tintHue: r.tintHue, tintSat: r.tintSat, auto: true });
    else setSettings({});
  };
  img.onerror = () => setSettings({});
  img.src = url;
}

function placeBackdrop() {
  const bd = $("bd"), s = st.scale;
  if (VIEW === "ambient") { Object.assign(bd.style, { left: 0, top: 0, width: "100vw", height: "100vh" }); return; }
  Object.assign(bd.style, { left: -st.rect.x / s + "px", top: -st.rect.y / s + "px", width: st.screen.w / s + "px", height: st.screen.h / s + "px" });
}

const allWalls = () => PRESETS.concat(st.wallpapers || []);
const thumbUrl = u => u.startsWith("https://images.unsplash.com") ? u.replace("w=2400", "w=400") : u;
function fmtTime(d, withAmPm) {
  if (st.settings.clock24) return { t: `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`, ap: "" };
  let h = d.getHours(); const ap = h >= 12 ? "PM" : "AM"; h = h % 12 || 12;
  return { t: `${h}:${String(d.getMinutes()).padStart(2, "0")}`, ap };
}
const layoutSvg = l => `<svg viewBox="0 0 24 16">${(l.slots || []).map(s => { const r = s.rect || [0, 0, 1, 1]; return `<rect x="${(r[0] * 24 + .5).toFixed(1)}" y="${(r[1] * 16 + .5).toFixed(1)}" width="${Math.max(1, r[2] * 24 - 1).toFixed(1)}" height="${Math.max(1, r[3] * 16 - 1).toFixed(1)}" rx="1.5"/>`; }).join("")}</svg>`;

// ================= desktop =================
const WCODES = { 0: "Clear", 1: "Mostly clear", 2: "Partly cloudy", 3: "Overcast", 45: "Fog", 48: "Freezing fog", 51: "Light drizzle", 53: "Drizzle", 55: "Heavy drizzle", 61: "Light rain", 63: "Rain", 65: "Heavy rain", 66: "Freezing rain", 67: "Freezing rain", 71: "Light snow", 73: "Snow", 75: "Heavy snow", 77: "Snow grains", 80: "Rain showers", 81: "Rain showers", 82: "Heavy showers", 85: "Snow showers", 86: "Snow showers", 95: "Thunderstorm", 96: "Thunderstorm", 99: "Thunderstorm" };
const WICON = c => c === 0 || c === 1 ? "ti-sun" : c <= 3 ? "ti-cloud" : c <= 48 ? "ti-mist" : c <= 67 || (c >= 80 && c <= 82) ? "ti-cloud-rain" : c <= 86 ? "ti-snowflake" : "ti-cloud-storm";

function renderDesktop() {
  root.innerHTML = `
  <div class="p" id="rail">
    <button data-cmd="terminal" title="Terminal"><i class="ti ti-terminal-2"></i></button>
    <button data-cmd="files" title="Files"><i class="ti ti-folder"></i></button>
    <button data-cmd="code" title="VS Code"><i class="ti ti-code"></i></button>
    <button data-cmd="launcher" title="Launcher"><i class="ti ti-search"></i></button>
    <button data-cmd="personalize" title="Personalize: wallpaper and colours"><i class="ti ti-palette"></i></button>    <button data-cmd="settings" title="Windows settings"><i class="ti ti-settings"></i></button>
    <div class="gap"></div>
    <button data-cmd="lock" title="Lock"><i class="ti ti-lock"></i></button>
    <button id="power" title="Power"><i class="ti ti-power"></i></button>
  </div>
  <div id="area" style="position:absolute;left:0;right:0;top:40px;bottom:80px">
    <div class="p wgt" id="clock"><div class="lbl" id="clbl"></div><div class="t" id="ct"></div><div class="d" id="cd"></div></div>
    <div class="p wgt" id="cal"><div><div class="lbl" id="cwk"></div><div class="day" id="cday"></div><div class="mon" id="cmon"></div></div><div class="grid" id="cgrid"></div></div>
    <div class="p wgt" id="weather" title="Change city in Personalize"><div class="city"><span id="wcity"></span><i class="ti ti-navigation" style="font-size:10px"></i></div>
      <div class="temp" id="wtemp">–</div><div class="desc" id="wdesc">Loading weather</div><div class="bars" id="wbars"></div></div>
    <div class="p wgt" id="sys"><div class="lbl">System</div>
      <div class="row"><span class="k">CPU</span><span class="tr"><i id="scpu"></i></span><span class="v" id="vcpu">–</span></div>
      <div class="row"><span class="k">RAM</span><span class="tr"><i id="smem"></i></span><span class="v" id="vmem">–</span></div>
      <div class="row"><span class="k">DISK</span><span class="tr"><i id="sdisk"></i></span><span class="v" id="vdisk">–</span></div>
      <div class="row" id="rbat"><span class="k">BAT</span><span class="tr"><i id="sbat"></i></span><span class="v" id="vbat">–</span></div>
      <div class="row"><span class="k">UP</span><span style="flex:1"></span><span class="v" id="vup">–</span></div></div>
    <div class="p wgt" id="music">
      <div style="display:flex;gap:9px;align-items:center;min-width:0"><div class="art" id="mart"><i class="ti ti-music"></i></div>
        <div style="min-width:0"><div class="tt" id="mtt">Nothing playing</div><div class="ar" id="mar">Spotify or YouTube</div></div></div>
      <div class="ctl"><button data-m="prev"><i class="ti ti-player-skip-back"></i></button><button data-m="play"><i class="ti ti-player-play" id="mpp"></i></button><button data-m="next"><i class="ti ti-player-skip-forward"></i></button></div>
      <div class="viz" id="viz"></div></div>
    <div class="p wgt" id="rings"></div>
    <div class="p wgt" id="claude" title="Open Claude"><div class="lbl">Claude Code · recent sessions</div><div id="sess"><div class="empty">No sessions yet</div></div></div>
    <div class="p wgt" id="usage" title="Open Claude usage settings"><div class="lbl">Claude · plan limits</div><div id="ulim"><div class="empty">Reading usage…</div></div><div class="foot" id="ufoot"></div></div>
    <div class="p wgt" id="live">
      <svg class="spark" id="spark" viewBox="-50 -50 100 100">${Array.from({ length: 12 }, (_, i) => `<path d="M0 -8 L3 -42 L0 -46 L-3 -42 Z" transform="rotate(${i * 30}) scale(${i % 2 ? .78 : 1})"/>`).join("")}<circle r="9"/></svg>
      <div class="ln">Claude</div><div class="ls" id="lstate">hover to peek</div></div>
    <div class="p" id="lpop"><div class="lbl"><span>Claude · live</span><span class="lt" id="ltitle"></span></div><div class="lv" id="lv"><div class="empty" id="lempty">Claude isn't open. Click to open it.</div></div></div>
  </div>`;
  root.querySelectorAll("#rail [data-cmd]").forEach(b => b.onclick = () => {
    const c = b.dataset.cmd; if (c === "launcher" || c === "personalize" || c === "ambient") send("open", { view: c }); else send("cmd", { name: c });
  });
  $("power").onclick = () => send("powerMenu");
  $("weather").onclick = () => send("open", { view: "personalize" });
  $("claude").onclick = $("live").onclick = $("lpop").onclick = () => send("openClaude");
  $("usage").onclick = () => send("openClaudeUsage");
  for (const id of ["live", "lpop"]) {
    $(id).addEventListener("mouseenter", () => peek(true));
    $(id).addEventListener("mouseleave", () => peek(false));
  }
  initArrange();
  $("cal").onclick = () => send("openGoogleCalendar");
  $("cal").style.cursor = "pointer";
  root.querySelectorAll("#music [data-m]").forEach(b => b.onclick = e => { e.stopPropagation(); send("media", { action: b.dataset.m }); });
  for (let i = 0; i < 22; i++) $("viz").appendChild(document.createElement("span"));
  $("rings").innerHTML = ["ti-cpu", "ti-database", "ti-battery-3", "ti-device-floppy"].map((ic, i) =>
    `<div class="ring"><svg viewBox="0 0 36 36"><circle cx="18" cy="18" r="15" fill="none" stroke="rgba(255,255,255,.08)" stroke-width="3"/><circle class="v" id="ring${i}" cx="18" cy="18" r="15" fill="none" stroke="var(--acc)" stroke-width="3" stroke-linecap="round" stroke-dasharray="0 94.2"/></svg><i class="ti ${ic}"></i></div>`).join("");
  document.addEventListener("contextmenu", e => { e.preventDefault(); send("desktopMenu"); });
  tickClock(); setInterval(tickClock, 1000);
  setInterval(tickViz, 260);
  renderDesktopDyn();
}
function nextWall() {
  const w = allWalls(); if (w.length < 2) return null;
  const i = w.indexOf(st.settings.wall); return w[(i + 1) % w.length];
}
let weatherKey = "";
function renderDesktopDyn() {
  if (!$("clbl")) return;
  $("clbl").textContent = st.settings.label || "";
  root.querySelectorAll(".wgt").forEach(w => w.classList.toggle("off", !st.settings.widgets));
  $("wcity").textContent = st.settings.city;
  const key = `${st.settings.lat},${st.settings.lon},${st.settings.units}`;
  if (key !== weatherKey) { weatherKey = key; loadWeather(); }
  renderCalendar(); renderStats(); renderMedia(); renderSessions(); renderUsage(); applyArrange();
  if (!st.settings.widgets) peek(false);
}
// Hovering the Claude tile opens a live view beside it; the host draws the thumbnail into #lv.
let peekTimer = 0, peekOn = false, liveKey = "";
function peek(on) {
  clearTimeout(peekTimer);
  if (on && (dragging || !st.settings.widgets)) return;
  peekTimer = setTimeout(() => {
    if (on === peekOn) return;
    peekOn = on;
    const pop = $("lpop"); if (!pop) return;
    if (on) placePeek();
    pop.classList.toggle("on", on);
    sendLiveRect();
  }, on ? 140 : 220);
}
function placePeek() {
  const t = $("live").getBoundingClientRect(), a = $("area").getBoundingClientRect(), pop = $("lpop");
  const W = 560, H = 360, g = 12;
  // beside the tile on whichever side has room, vertically centred on it and kept inside the area
  let x = t.right + g + W <= a.right ? t.right + g : t.left - g - W;
  x = clamp(x, a.left + 8, a.right - W - 8);
  const y = clamp(t.top + t.height / 2 - H / 2, a.top + 8, a.bottom - H - 8);
  Object.assign(pop.style, { left: x - a.left + "px", top: y - a.top + "px", width: W + "px", height: H + "px" });
}
function sendLiveRect() {
  const el = $("lv"); if (!el) return;
  const r = el.getBoundingClientRect();
  const rect = peekOn ? { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) } : null;
  const k = JSON.stringify(rect); if (k === liveKey) return;
  liveKey = k; send("liveRect", { rect });
}

// Drag any home-screen tile to move it; positions are saved in settings.layout.
let dragging = null;
function initArrange() {
  const area = $("area");
  area.addEventListener("pointerdown", e => {
    const w = e.target.closest(".wgt");
    if (!w || e.button !== 0 || e.target.closest("button,input,a")) return;
    const r = w.getBoundingClientRect(), a = area.getBoundingClientRect();
    dragging = { w, id: w.id, sx: e.clientX, sy: e.clientY, ox: r.left - a.left, oy: r.top - a.top, moved: false };
    w.setPointerCapture(e.pointerId);
  });
  area.addEventListener("pointermove", e => {
    const d = dragging; if (!d) return;
    const dx = e.clientX - d.sx, dy = e.clientY - d.sy;
    if (!d.moved && Math.hypot(dx, dy) < 6) return;
    if (!d.moved) { d.moved = true; d.w.classList.add("drag"); peek(false); }
    const a = area.getBoundingClientRect(), r = d.w.getBoundingClientRect();
    const x = clamp(Math.round((d.ox + dx) / 8) * 8, 0, a.width - r.width), y = clamp(Math.round((d.oy + dy) / 8) * 8, 0, a.height - r.height);
    Object.assign(d.w.style, { left: x + "px", top: y + "px", right: "auto", bottom: "auto" });
  });
  const end = () => {
    const d = dragging; dragging = null; if (!d) return;
    d.w.classList.remove("drag");
    if (!d.moved) return;
    // swallow the click that follows a drag so the tile doesn't open anything
    addEventListener("click", ev => ev.stopPropagation(), { capture: true, once: true });
    const layout = Object.assign({}, st.settings.layout || {}, { [d.id]: { x: parseInt(d.w.style.left), y: parseInt(d.w.style.top) } });
    setSettings({ layout });
  };
  area.addEventListener("pointerup", end);
  area.addEventListener("pointercancel", end);
}
function applyArrange() {
  const L = st.settings.layout || {};
  document.querySelectorAll("#area .wgt").forEach(w => {
    if (dragging && dragging.w === w) return;
    const p = L[w.id];
    Object.assign(w.style, p ? { left: p.x + "px", top: p.y + "px", right: "auto", bottom: "auto" } : { left: "", top: "", right: "", bottom: "" });
  });
}
const fmtTok = n => n >= 1e6 ? (n / 1e6).toFixed(1) + "M" : n >= 1e3 ? Math.round(n / 1e3) + "k" : String(n);
function resetLabel(iso) {
  if (!iso) return "";
  const d = new Date(iso), mins = Math.round((d - Date.now()) / 6e4);
  if (!(mins > 0)) return "resets now";
  if (mins < 60) return `resets in ${mins}m`;
  if (mins < 24 * 60) return `resets in ${Math.floor(mins / 60)}h ${mins % 60}m`;
  return "resets " + d.toLocaleDateString(undefined, { weekday: "short" }) + " " + shortTime(d);
}
function renderUsage() {
  const el = $("ulim"), u = st.usage; if (!el || !u) return;
  const L = u.limits || [], loc = u.local || {};
  const bar = (label, pct, sub) => `<div class="ul"><div class="ur"><span>${esc(label)}</span><b>${pct == null ? "" : Math.round(pct) + "%"}</b></div>` +
    (pct == null ? "" : `<div class="ub"><i style="width:${clamp(pct, 0, 100)}%" class="${pct >= 90 ? "hot" : pct >= 70 ? "warm" : ""}"></i></div>`) +
    `<div class="us">${esc(sub)}</div></div>`;
  if (L.length) el.innerHTML = L.slice(0, 3).map(l => bar(l.label, l.pct, resetLabel(l.resets))).join("");
  else el.innerHTML = [["Last 5 hours", loc.hour5], ["Last 7 days", loc.week]].map(([k, v]) =>
    bar(k, null, v ? `${v.messages} replies · ${fmtTok(v.tokens)} tokens` : "–")).join("");
  const w = loc.week || {}, h = loc.hour5 || {};
  $("ufoot").textContent =
    u.note === "expired" ? "Couldn't renew the sign-in. Run claude in a terminal, then /login." :
    u.note === "signin" ? "Exact limits: run claude in a terminal, then /login." :
    u.note === "offline" ? "Couldn't reach Anthropic. Showing your last numbers." :
    `Last 5h: ${h.messages || 0} replies · ${fmtTok(h.tokens || 0)} tokens · week ${fmtTok(w.tokens || 0)}`;
}
setInterval(() => { if (VIEW === "desktop") renderUsage(); }, 60000);
let lastDay = -1;
function tickClock() {
  const d = new Date(), f = fmtTime(d);
  $("ct").classList.toggle("disp", !!st.settings.dispClock);
  $("ct").innerHTML = `${f.t}${f.ap ? `<small>${f.ap}</small>` : ""}`;
  $("cd").innerHTML = `${d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" })} · <b>${esc(st.settings.city)}</b>`;
  if (d.getDate() !== lastDay) { lastDay = d.getDate(); renderCalendar(); }
}
function renderCalendar() {
  const d = new Date();
  $("cwk").textContent = d.toLocaleDateString(undefined, { weekday: "long" });
  $("cday").textContent = d.getDate();
  $("cmon").textContent = "● " + d.toLocaleDateString(undefined, { month: "short", year: "numeric" }).toUpperCase();
  $("cgrid").className = "grid";
  const first = new Date(d.getFullYear(), d.getMonth(), 1), start = (first.getDay() + 6) % 7;
  const days = new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate(), prev = new Date(d.getFullYear(), d.getMonth(), 0).getDate();
  let h = ["M", "T", "W", "T", "F", "S", "S"].map(x => `<span class="h">${x}</span>`).join("");
  for (let i = 0; i < 42; i++) {
    const n = i - start + 1;
    if (n < 1) h += `<span class="o">${prev + n}</span>`;
    else if (n > days) { if (i % 7 === 0 && i >= 35) break; h += `<span class="o">${n - days}</span>`; }
    else h += `<span class="${n === d.getDate() ? "today" : ""}">${n}</span>`;
  }
  $("cgrid").innerHTML = h;
}
function upcoming() {
  const now = new Date();
  return ((st.events || {}).list || []).map(e => Object.assign({}, e, { s: new Date(e.start), en: new Date(e.end) }))
    .filter(e => e.en > now).sort((a, b) => a.s - b.s);
}
const shortTime = x => { const f = fmtTime(x); return f.t + (f.ap ? " " + f.ap.toLowerCase() : ""); };
function whenLabel(e) {
  const now = new Date(), today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days = Math.round((new Date(e.s.getFullYear(), e.s.getMonth(), e.s.getDate()) - today) / 864e5);
  const day = days <= 0 ? "Today" : days === 1 ? "Tomorrow" : e.s.toLocaleDateString(undefined, { weekday: "short", day: "numeric" });
  if (e.allDay) return day + " · all day";
  if (days <= 0 && e.s <= now) return "Now · until " + shortTime(e.en);
  return (days <= 0 ? "" : day + " · ") + shortTime(e.s) + " – " + shortTime(e.en);
}
function calStatus() {
  const ev = st.events;
  if (!ev || !ev.configured) return "NOT CONNECTED";
  if (ev.error && !(ev.list || []).length) return "COULDN'T LOAD";
  return `CONNECTED · ${upcoming().length} UPCOMING`;
}
async function loadWeather() {
  const S = st.settings;
  try {
    const u = `https://api.open-meteo.com/v1/forecast?latitude=${S.lat}&longitude=${S.lon}&current=temperature_2m,weather_code&hourly=precipitation_probability&forecast_hours=12&timezone=auto${S.units === "f" ? "&temperature_unit=fahrenheit" : ""}`;
    const r = await (await fetch(u)).json();
    const c = r.current.weather_code, p = r.hourly.precipitation_probability || [];
    $("wtemp").textContent = Math.round(r.current.temperature_2m) + "°";
    const soon = p.slice(0, 3).some(v => v >= 50);
    $("wdesc").innerHTML = `<i class="ti ${WICON(c)}"></i> ${WCODES[c] || "—"}${soon && c < 51 ? "<br>Rain likely soon" : ""}`;
    $("wbars").innerHTML = p.map(v => `<span style="height:${Math.max(6, v)}%"></span>`).join("");
  } catch (e) { $("wdesc").textContent = "Weather offline"; }
}
setInterval(() => { if (VIEW === "desktop") loadWeather(); }, 15 * 60 * 1000);
function setRing(i, v) { const e = $("ring" + i); if (e) e.setAttribute("stroke-dasharray", `${(clamp(v, 0, 100) / 100 * 94.2).toFixed(1)} 94.2`); }
function renderStats() {
  const s = st.stats; if (!s || !$("scpu")) return;
  $("scpu").style.width = s.cpu + "%"; $("vcpu").textContent = s.cpu + "%";
  $("smem").style.width = s.mem + "%"; $("vmem").textContent = s.memGb + "G";
  $("sdisk").style.width = s.disk + "%"; $("vdisk").textContent = s.disk + "%";
  $("rbat").style.display = s.battery < 0 ? "none" : "";
  $("sbat").style.width = Math.max(0, s.battery) + "%"; $("vbat").textContent = s.battery + "%" + (s.charging ? " ⚡" : "");
  $("vup").textContent = s.uptime;
  setRing(0, s.cpu); setRing(1, s.mem); setRing(2, s.battery < 0 ? 100 : s.battery); setRing(3, s.disk);
}
function renderMedia() {
  const m = st.media; if (!$("mtt")) return;
  const has = m && m.title;
  $("mtt").textContent = has ? m.title : "Nothing playing";
  $("mar").textContent = has ? m.artist : "Spotify or YouTube";
  $("mpp").className = "ti " + (m && m.playing ? "ti-player-pause" : "ti-player-play");
}
function tickViz() {
  const on = st.media && st.media.playing, t = Date.now();
  [...$("viz").children].forEach((s, i) => s.style.height = on ? (15 + Math.abs(Math.sin(t / 400 + i * .7)) * 60 + Math.random() * 25) + "%" : "8%");
}
function renderSessions() {
  const el = $("sess"); if (!el) return;
  const l = st.sessions || [];
  const busy = l.some(s => s.working);
  $("live").classList.toggle("busy", busy);
  $("lstate").textContent = busy ? "working…" : "hover to peek";
  el.innerHTML = l.length ? l.map(s => `<div class="sess"><span class="dot ${s.working ? "on" : ""}"></span><b>${esc(s.title)}</b>${s.working ? "working" : esc(s.ago)}</div>`).join("") : `<div class="empty">No sessions yet</div>`;
}

// ================= top bar =================
function renderTopbar() {
  root.innerHTML = `<div class="p" id="bar">
    <div class="ws" id="ws"></div>
    <div class="fgapp" id="fg"></div>
    <div class="bc" id="bc"><b id="bt"></b><span id="bdt"></span><span id="nev" style="color:var(--acc)"></span></div>
    <div class="br">
      <button id="tmedia" title="Play / pause"><i class="ti ti-music"></i><span class="song" id="tsong"></span></button>
      <button id="tnet" title="Quick settings"><i class="ti ti-wifi" id="inet"></i><i class="ti ti-volume"></i></button>
      <button id="tbat" title="Quick settings"><i class="ti ti-battery-3" id="ibat"></i><span id="vbat"></span></button>
      <button id="tnote" title="Notifications"><i class="ti ti-bell"></i></button>
      <button id="tpers" title="Personalize: wallpaper and colours"><i class="ti ti-palette"></i></button>      <button id="tsearch" title="Launcher"><i class="ti ti-search"></i></button>
    </div></div>`;
  $("bc").onclick = () => send("openGoogleCalendar");
  $("bc").title = "Open Google Calendar (Ctrl+Alt+C)";
  $("tnote").onclick = () => send("keys", { name: "notify" });
  $("tnet").onclick = $("tbat").onclick = () => send("keys", { name: "quick" });
  $("tmedia").onclick = () => send("media", { action: "play" });
  $("tsearch").onclick = () => send("open", { view: "launcher" });
  $("tpers").onclick = () => send("open", { view: "personalize" });  document.addEventListener("contextmenu", e => { e.preventDefault(); send("desktopMenu"); });
  const tick = () => { const d = new Date(), f = fmtTime(d); $("bt").textContent = f.t + (f.ap ? " " + f.ap : ""); $("bdt").textContent = d.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" }); };
  tick(); setInterval(tick, 1000);
  setInterval(renderNextEvent, 30000);
  renderTopbarDyn();
}
// "· Standup in 12m" next to the clock when something starts within three hours.
function renderNextEvent() {
  const el = $("nev"); if (!el) return;
  const now = new Date(), e = upcoming().find(x => !x.allDay && x.s - now < 3 * 36e5);
  if (!e) { el.textContent = ""; return; }
  const mins = Math.round((e.s - now) / 6e4);
  const when = mins <= 0 ? "now" : mins < 60 ? `in ${mins}m` : `at ${shortTime(e.s)}`;
  el.textContent = ` · ${e.title.length > 34 ? e.title.slice(0, 33) + "…" : e.title} ${when}`;
}
function renderTopbarDyn() {
  if (!$("ws")) return;
  const d = st.desktops;
  $("ws").innerHTML = Array.from({ length: Math.max(1, d.count) }, (_, i) => `<button class="${i === d.current ? "on" : ""}" data-i="${i}">${i + 1}</button>`).join("");
  $("ws").querySelectorAll("button").forEach(b => b.onclick = () => send("desktop", { index: +b.dataset.i }));
  const fg = st.windows.fg;
  $("fg").innerHTML = fg ? `<i class="ti ti-point-filled"></i>${esc(fg.app)}<span style="color:var(--dim)"> — ${esc(fg.title)}</span>` : "";
  const m = st.media;
  $("tmedia").style.display = m && m.title ? "" : "none";
  if (m && m.title) $("tsong").textContent = `${m.title} — ${m.artist}`;
  const s = st.stats;
  if (s) {
    $("inet").className = "ti " + (s.net === "wifi" ? "ti-wifi" : s.net === "ethernet" ? "ti-network" : "ti-wifi-off");
    $("tbat").style.display = s.battery < 0 ? "none" : "";
    $("ibat").className = "ti " + (s.charging ? "ti-battery-charging" : s.battery > 75 ? "ti-battery-4" : s.battery > 50 ? "ti-battery-3" : s.battery > 25 ? "ti-battery-2" : "ti-battery-1");
    $("vbat").textContent = s.battery + "%";
  }
}

// ================= dock =================
let dockSig = "";
function renderDock() {
  const g = st.windows.groups || [], L = st.config.layouts || [];
  const sig = JSON.stringify([g.map(x => [x.key, x.name, x.count, x.active]), L.map(l => l.name), Object.keys(st.icons).length]);
  if (sig === dockSig && $("dock")) return;
  dockSig = sig;
  let h = `<div class="ic" data-cmd="home" data-n="Home  Win+D"><div class="b"><i class="ti ti-home"></i></div></div><div class="sep"></div>`, sepDone = false;
  g.forEach(x => {
    if (!x.pinned && !sepDone && x !== g[0]) { h += `<div class="sep"></div>`; sepDone = true; }
    const ic = st.icons[x.key];
    const face = ic ? `<img src="${ic}" alt="">` : `<span class="mg">${esc((x.name || "?")[0].toUpperCase())}</span>`;
    const dots = x.count ? `<div class="run">${x.active ? "<span></span>" : "<span></span>".repeat(Math.min(3, x.count))}</div>` : "";
    h += `<div class="ic ${x.active ? "act" : ""}" data-key="${esc(x.key)}" data-n="${esc(x.name)}"><div class="b">${face}</div>${dots}</div>`;
  });
  if (L.length) h += `<div class="sep"></div>` + L.map((l, i) => `<div class="lay ic" data-l="${i}" data-n="${esc(l.name + (l.hotkey ? "  " + l.hotkey : ""))}">${layoutSvg(l)}</div>`).join("");
  const hk = st.config.hotkeys || {};
  h += `<div class="sep"></div>` +
    `<div class="ic" data-cmd="launcher" data-n="Search  ${esc(hk.launcher || "Ctrl+Alt+K")}"><div class="b"><i class="ti ti-search"></i></div></div>` +
    `<div class="ic" data-cmd="personalize" data-n="Wallpaper and colours  ${esc(hk.personalize || "Ctrl+Alt+P")}"><div class="b"><i class="ti ti-palette"></i></div></div>`;
  if (!$("dock")) {
    root.innerHTML = `<div class="p" id="dock"></div>`;
    const d = $("dock");
    d.addEventListener("mousemove", e => { mx = e.clientX; wave(); });
    d.addEventListener("mouseleave", () => { mx = null; wave(); send("tip", { text: null }); tipFor = null; clearTimeout(pvTimer); pvOpen = false; });
    d.addEventListener("click", e => {
      const t = e.target.closest(".ic"); if (!t) return;
      if (t.dataset.key) send("dockClick", { key: t.dataset.key });
      else if (t.dataset.l) send("layout", { index: +t.dataset.l });
      else if (t.dataset.cmd === "home") send("cmd", { name: "home" });
      else if (t.dataset.cmd) send("open", { view: t.dataset.cmd });
    });
    // the host sizes the parked dock window to exactly this pill
    let lastSize = "";
    new ResizeObserver(() => {
      const r = d.getBoundingClientRect(), s = `${Math.ceil(r.width)}x${Math.ceil(r.height)}`;
      if (s !== lastSize) { lastSize = s; send("dockSize", { w: Math.ceil(r.width), h: Math.ceil(r.height) }); }
    }).observe(d);
    d.addEventListener("auxclick", e => { const t = e.target.closest(".ic"); if (e.button === 1 && t && t.dataset.key) send("dockNew", { key: t.dataset.key }); });
    d.addEventListener("contextmenu", e => { e.preventDefault(); const t = e.target.closest(".ic"); if (t && t.dataset.key) send("dockMenu", { key: t.dataset.key }); });
  }
  $("dock").innerHTML = h;
  wave();
}
let mx = null, raf = 0, tipFor = null;
function wave() { if (!raf) raf = requestAnimationFrame(waveFrame); }
function waveFrame() {
  raf = 0;
  const items = [...document.querySelectorAll("#dock .ic")];
  let best = null, bd = 1e9;
  const parked = document.body.classList.contains("parked"), amp = parked ? .14 : .28, lift = parked ? 8 : 22;
  items.forEach(ic => {
    const r = ic.getBoundingClientRect(), c = r.left + r.width / 2, d = mx == null ? 999 : c - mx;
    const s = 1 + amp * Math.exp(-d * d / (2 * 44 * 44));
    const b = ic.querySelector(".b") || ic.querySelector("svg");
    if (b) b.style.transform = `translateY(${-(s - 1) * lift}px) scale(${s})`;
    if (Math.abs(d) < bd) { bd = Math.abs(d); best = [ic, c, r.top]; }
  });
  if (mx != null && best && best[0] !== tipFor) {
    tipFor = best[0];
    const top = $("dock").getBoundingClientRect().top;
    send("tip", { text: best[0].dataset.n, x: best[1], top: top - 14 });
    hoverPreview(best[0], best[1], top);
  }
}
// Resting on a running app opens live previews of its windows; once open, they follow the pointer.
let pvTimer = 0, pvOpen = false;
function hoverPreview(ic, x, top) {
  clearTimeout(pvTimer);
  const g = (st.windows.groups || []).find(z => z.key === ic.dataset.key);
  if (!g || !g.count) { if (pvOpen) { pvOpen = false; send("preview", { key: null }); } return; }
  const go = () => { pvOpen = true; send("preview", { key: g.key, x, top }); };
  if (pvOpen) go(); else pvTimer = setTimeout(go, 420);
}

// ================= window preview strip =================
function renderPreview() {
  root.innerHTML = `<div id="pv"></div>`;
  const pv = $("pv");
  pv.addEventListener("click", e => {
    const c = e.target.closest(".pc"); if (!c) return;
    send(e.target.closest(".x") ? "previewClose" : "previewActivate", { hwnd: +c.dataset.h });
  });
  pv.addEventListener("auxclick", e => { const c = e.target.closest(".pc"); if (e.button === 1 && c) send("previewClose", { hwnd: +c.dataset.h }); });
  document.addEventListener("contextmenu", e => e.preventDefault());
}
function drawPreview(m) {
  $("pv").innerHTML = m.items.map(i => `<div class="pc ${i.active ? "act" : ""}" data-h="${i.hwnd}">
    <div class="pt"><span>${esc(i.title || m.name)}</span><button class="x" title="Close window"><i class="ti ti-x"></i></button></div>
    <div class="th">${i.min ? `<em>Minimised</em>` : ""}</div></div>`).join("");
  requestAnimationFrame(() => send("thumbRects", {
    rects: [...document.querySelectorAll("#pv .pc")].map(c => {
      const r = c.querySelector(".th").getBoundingClientRect();
      return { hwnd: +c.dataset.h, x: r.left, y: r.top, w: r.width, h: r.height };
    })
  }));
}

// ================= launcher =================
let lsel = 0, lshown = [];
const requested = new Set();
const CMDS = [
  ["Ask Claude (new chat)", "ti-message-plus", () => send("askClaude")],
  ["Google Calendar", "ti-calendar", () => send("openGoogleCalendar")],
  ["Cascade calendar view", "ti-calendar-week", () => send("open", { view: "calendar" })],
  ["Personalize", "ti-palette", () => send("open", { view: "personalize" })],
  ["Add wallpaper photo", "ti-photo-plus", () => send("addPhoto")],
  ["Fullscreen clock", "ti-clock", () => send("open", { view: "ambient" })],
  ["Next wallpaper", "ti-photo", null],
  ["Show or hide Windows taskbar", "ti-layout-bottombar", () => send("cmd", { name: "toggleTaskbar" })],
  ["Task Manager", "ti-activity", () => send("cmd", { name: "taskManager" })],
  ["Windows settings", "ti-settings", () => send("cmd", { name: "settings" })],
  ["Lock", "ti-lock", () => send("cmd", { name: "lock" })],
  ["Sleep", "ti-moon", () => send("cmd", { name: "sleep" })],
  ["Edit Cascade config", "ti-file-code", () => send("cmd", { name: "editConfig" })],
  ["Reload Cascade", "ti-refresh", () => send("cmd", { name: "reload" })],
  ["Quit Cascade", "ti-logout", () => send("cmd", { name: "quit" })]
];
function renderLauncher() {
  root.innerHTML = `<div id="launch">
    <div class="q"><i class="ti ti-search"></i><input id="lq" placeholder="Where to? Apps, windows, layouts, sessions, commands" autocomplete="off" spellcheck="false"></div>
    <div id="res"></div>
    <div class="foot"><span>↵ open</span><span>↑↓ move</span><span>esc close</span><span style="margin-left:auto">CASCADE</span></div></div>`;
  $("lq").addEventListener("input", () => { lsel = 0; filterLauncher(); });
  document.addEventListener("keydown", e => {
    if (e.key === "Escape") { send("hide"); e.preventDefault(); }
    else if (e.key === "ArrowDown") { lsel = Math.min(lsel + 1, lshown.length - 1); drawResults(); e.preventDefault(); }
    else if (e.key === "ArrowUp") { lsel = Math.max(lsel - 1, 0); drawResults(); e.preventDefault(); }
    else if (e.key === "Enter") { runResult(lsel); e.preventDefault(); }
  });
  send("startApps");
  filterLauncher();
}
function score(q, t) {
  t = (t || "").toLowerCase();
  const i = t.indexOf(q);
  if (i === 0) return 1000 - t.length;
  if (i > 0) return (/[^a-z0-9]/.test(t[i - 1]) ? 800 : 500) - i - t.length / 4;
  let ti = 0, gaps = 0, last = -1;
  for (const c of q) { const f = t.indexOf(c, ti); if (f < 0) return 0; if (last >= 0) gaps += f - last - 1; last = f; ti = f + 1; }
  return Math.max(1, 300 - gaps * 6 - t.length / 4);
}
function allItems() {
  const items = [];
  (st.config.layouts || []).forEach((l, i) => items.push({ title: l.name, sub: "Arrange windows" + (l.hotkey ? " · " + l.hotkey : ""), kind: "LAYOUT", svg: layoutSvg(l), bonus: 60, run: () => send("layout", { index: i }) }));
  (st.windows.list || []).forEach(w => items.push({ title: w.title, sub: "Switch to window", kind: "WINDOW", key: w.key, bonus: 40, run: () => send("activate", { hwnd: w.hwnd }) }));
  (st.sessions || []).forEach(s => items.push({ title: s.title, sub: "Claude Code" + (s.folder ? " · " + s.folder : ""), kind: "SESSION", glyph: "ti-sparkles", bonus: 30, run: () => send("openClaude") }));
  const names = new Set();
  (st.config.apps || []).forEach(a => { names.add(a.name.toLowerCase()); items.push({ title: a.name, sub: "App", kind: "APP", key: "app:" + a.id, bonus: 50, run: () => send("launch", { id: a.id }) }); });
  (st.startApps || []).forEach(a => { if (!names.has(a.name.toLowerCase())) items.push({ title: a.name, sub: "App", kind: "APP", key: "start:" + a.id, path: "shell:AppsFolder\\" + a.id, bonus: 0, run: () => send("launchStart", { id: a.id }) }); });
  CMDS.forEach(([t, g, f]) => items.push({ title: t, sub: "Command", kind: "COMMAND", glyph: g, bonus: 10, run: f || (() => { const n = nextWall(); if (n) setWall(n, true); }) }));
  return items;
}
function filterLauncher() {
  const q = ($("lq").value || "").trim().toLowerCase(), items = allItems();
  if (!q) lshown = items.filter(i => i.kind === "LAYOUT" || i.kind === "WINDOW").slice(0, 8);
  else {
    items.forEach(i => { const s = Math.max(score(q, i.title), score(q, i.sub) * .5); i.score = s > 0 ? s + i.bonus : 0; });
    lshown = items.filter(i => i.score > 0).sort((a, b) => b.score - a.score).slice(0, 8);
  }
  drawResults();
}
function drawResults() {
  lsel = clamp(lsel, 0, Math.max(0, lshown.length - 1));
  $("res").innerHTML = lshown.length ? lshown.map((r, i) => {
    let ico;
    if (r.svg) ico = r.svg;
    else if (r.glyph) ico = `<i class="ti ${r.glyph}"></i>`;
    else if (r.key && st.icons[r.key]) ico = `<img src="${st.icons[r.key]}" alt="">`;
    else { ico = `<i class="ti ti-app-window"></i>`; if (r.path && !requested.has(r.key)) { requested.add(r.key); send("icon", { key: r.key, path: r.path }); } }
    return `<div class="res ${i === lsel ? "sel" : ""}" data-i="${i}"><div class="ico">${ico}</div><div class="tx"><div>${esc(r.title)}</div><small>${esc(r.sub)}</small></div><em>${r.kind}</em></div>`;
  }).join("") : `<div class="res"><div class="tx"><div>Nothing on this trail</div><small>Try an app name, a window title or a command</small></div></div>`;
  $("res").querySelectorAll(".res[data-i]").forEach(el => {
    el.onmousemove = () => { const i = +el.dataset.i; if (i !== lsel) { lsel = i; drawResults(); } };
    el.onclick = () => runResult(+el.dataset.i);
  });
}
function runResult(i) { const r = lshown[i]; if (!r) return; send("hide"); r.run(); }

// ================= personalize =================
function renderPers() {
  root.innerHTML = `<div id="pers">
    <h4><i class="ti ti-palette"></i>Personalize<button class="x" id="px" title="Close"><i class="ti ti-x"></i></button></h4>
    <div class="sec"><div class="lbl"><span>Wallpaper</span></div><div class="thumbs" id="thumbs"></div></div>
    <div class="sec"><div class="lbl"><span>Accent</span><span class="auto" id="autoTag"></span></div>
      <div class="sw" id="sw"></div>
      <div class="pick"><label><input type="color" id="c1">Primary</label><label><input type="color" id="c2">Secondary</label></div></div>
    <div class="sec"><div class="lbl"><span>Photo</span></div>
      <div class="sl"><span>Darkness</span><input type="range" id="s_dark" min="0" max="85" step="1"><output id="o_dark"></output></div>
      <div class="sl"><span>Blur</span><input type="range" id="s_wb" min="0" max="20" step="1"><output id="o_wb"></output></div>
      <div class="sl"><span>Saturation</span><input type="range" id="s_sat" min="0" max="150" step="5"><output id="o_sat"></output></div></div>
    <div class="sec"><div class="lbl"><span>Panels</span></div>
      <div class="sl"><span>Opacity</span><input type="range" id="s_pa" min="30" max="98" step="1"><output id="o_pa"></output></div>
      <div class="sl"><span>Glass blur</span><input type="range" id="s_bl" min="0" max="30" step="1"><output id="o_bl"></output></div>
      <div class="sl"><span>Tint</span><input type="range" id="s_ti" min="0" max="100" step="1"><output id="o_ti"></output></div>
      <label class="tg"><span>Tint dock icons</span><input type="checkbox" id="t_iconTint"><span class="s"></span></label>
      <div class="sl"><span>Icon colour</span><input type="range" id="s_iconColor" min="0" max="100" step="5"><output id="o_iconColor"></output></div>
      <label class="tg"><span>Show widgets</span><input type="checkbox" id="t_widgets"><span class="s"></span></label>
      <label class="tg"><span>24-hour clock</span><input type="checkbox" id="t_clock24"><span class="s"></span></label></div>
    <div class="sec"><div class="lbl"><span>Display font</span><span class="auto" id="fname"></span></div>
      <div class="disp" id="fprev" style="font-size:44px;line-height:1;color:var(--txt)">9:41</div>
      <div class="pbtn" style="margin-top:10px"><button id="fimp"><i class="ti ti-typography"></i>Import font file</button><button id="fclr">Use default</button></div>
      <label class="tg"><span>Use for clocks, dates and headings</span><input type="checkbox" id="t_dispClock"><span class="s"></span></label>
      <label class="tg"><span>Use for all text</span><input type="checkbox" id="t_fontAll"><span class="s"></span></label>
</div>
    <div class="sec"><div class="lbl"><span>Windows and dock</span></div>
      <label class="tg"><span>Open apps full screen</span><input type="checkbox" id="t_maximizeNew"><span class="s"></span></label>
      <label class="tg"><span>Hide dock until mouse hits bottom</span><input type="checkbox" id="t_dockAutoHide"><span class="s"></span></label></div>
    <div class="sec"><div class="lbl"><span>Google Calendar</span><span class="auto" id="calst"></span></div>
      <div id="gbox"></div>
      <details class="help"><summary>One-time Google setup (about 5 minutes)</summary><ol>
        <li>Open the <a data-url="https://console.cloud.google.com/projectcreate">Google Cloud console</a> and create a project, for example "Cascade".</li>
        <li>Turn on the <a data-url="https://console.cloud.google.com/apis/library/calendar-json.googleapis.com">Google Calendar API</a> for that project.</li>
        <li>Open <a data-url="https://console.cloud.google.com/auth/overview">Google Auth Platform</a> and choose Get started: app name Cascade, your email, audience External. Then under Audience, add your Gmail as a test user.</li>
        <li>Open <a data-url="https://console.cloud.google.com/auth/clients">Clients</a>, choose Create client, pick Desktop app, then download the JSON file.</li>
        <li>Here, choose Pick client file and select that JSON, then Connect Google. Google will warn that the app isn't verified. It's your own app, so choose Continue.</li>
      </ol></details>
      <div class="lbl" style="margin:16px 0 8px"><span>Or private iCal links</span></div>
      <textarea class="txtin" id="ics" rows="2" spellcheck="false" style="resize:none;font-family:var(--mono);font-size:10.5px" placeholder="Paste your secret iCal address"></textarea>
      <div class="empty" style="line-height:1.5;margin-top:6px">In Google Calendar on the web: Settings, pick your calendar under "Settings for my calendars", then Integrate calendar, and copy "Secret address in iCal format". One link per line for more calendars.</div>
      <div class="pbtn" style="margin-top:8px"><button class="pri" id="icsSave"><i class="ti ti-calendar"></i>Connect</button><button id="icsClear">Disconnect</button></div></div>
    <div class="sec"><div class="lbl"><span>Weather and label</span></div>
      <input class="txtin" id="city" placeholder="Search a city" autocomplete="off" spellcheck="false"><div class="cities" id="cities"></div>
      <label class="tg"><span>Fahrenheit</span><input type="checkbox" id="t_f"><span class="s"></span></label>
      <input class="txtin" id="label" style="margin-top:9px" placeholder="Clock label" spellcheck="false"></div>
    <div class="pbtn"><button id="reAuto"><i class="ti ti-wand"></i>Re-pick from photo</button><button class="pri" id="done">Done</button></div></div>`;
  $("px").onclick = $("done").onclick = () => send("hide");
  $("c1").oninput = e => setSettings({ acc: e.target.value, auto: false });
  $("c2").oninput = e => setSettings({ acc2: e.target.value, auto: false });
  ["dark", "wb", "sat", "pa", "bl", "ti", "iconColor"].forEach(k => $("s_" + k).oninput = e => setSettings({ [k]: +e.target.value }));
  ["iconTint", "widgets", "clock24", "dispClock", "maximizeNew", "dockAutoHide"].forEach(k => $("t_" + k).onchange = e => setSettings({ [k]: e.target.checked }));
  $("icsSave").onclick = () => { setSettings({ icsUrl: $("ics").value.trim() }); $("calst").textContent = "CONNECTING"; };
  $("icsClear").onclick = () => { $("ics").value = ""; setSettings({ icsUrl: "" }); };
  $("pers").querySelectorAll("a[data-url]").forEach(a => { a.href = "#"; a.onclick = e => { e.preventDefault(); send("openUrl", { url: a.dataset.url }); }; });
  $("t_fontAll").onchange = e => setSettings({ fontAll: e.target.checked });
  renderGoogle();
  send("calSources");
  $("fimp").onclick = () => send("addFont");
  $("fclr").onclick = () => setSettings({ fontUrl: "", fontName: "" });  $("t_f").onchange = e => setSettings({ units: e.target.checked ? "f" : "c" });
  $("label").oninput = e => setSettings({ label: e.target.value });
  $("reAuto").onclick = () => setWall(st.settings.wall, true);
  let ct = 0;
  $("city").oninput = e => {
    clearTimeout(ct); const q = e.target.value.trim();
    if (q.length < 2) { $("cities").innerHTML = ""; return; }
    ct = setTimeout(async () => {
      try {
        const r = await (await fetch(`https://geocoding-api.open-meteo.com/v1/search?count=5&name=${encodeURIComponent(q)}`)).json();
        $("cities").innerHTML = (r.results || []).map((c, i) => `<button data-i="${i}">${esc(c.name)}<span style="color:var(--dim)"> · ${esc([c.admin1, c.country].filter(Boolean).join(", "))}</span></button>`).join("");
        $("cities").querySelectorAll("button").forEach(b => b.onclick = () => { const c = r.results[+b.dataset.i]; setSettings({ city: c.name, lat: c.latitude, lon: c.longitude }); $("city").value = ""; $("cities").innerHTML = ""; });
      } catch (err) { $("cities").innerHTML = `<div class="empty">Couldn't reach the city search</div>`; }
    }, 300);
  };
  document.addEventListener("keydown", e => { if (e.key === "Escape") send("hide"); });
  renderPersDyn();
}
// Google account box: pick client file → connect → connected.
function renderGoogle() {
  const box = $("gbox"); if (!box) return;
  const s = st.calSources || {}, g = s.google || {}, n = (s.calendars || []).filter(c => c.source === "google").length;
  let h;
  if (g.connected) h = `<div class="gline"><i class="ti ti-circle-check"></i>Connected${g.email ? " as " + esc(g.email) : ""} · ${n} calendar${n === 1 ? "" : "s"}</div>
      <div class="pbtn" style="margin-top:8px"><button class="pri" id="gOpen"><i class="ti ti-calendar"></i>Open calendar</button><button id="gOff">Disconnect</button></div>`;
  else if (g.hasClient) h = `<div class="pbtn"><button class="pri" id="gOn"><i class="ti ti-brand-google"></i>Connect Google</button><button id="gFile">Change client file</button></div>`;
  else h = `<div class="pbtn"><button class="pri" id="gFile"><i class="ti ti-file-upload"></i>Pick client file</button></div>`;
  if (s.message) h += `<div class="empty" style="margin-top:8px">${esc(s.message)}</div>`;
  else if (s.error) h += `<div class="empty" style="margin-top:8px">${esc(s.error)}</div>`;
  box.innerHTML = h;
  const on = (id, f) => { if ($(id)) $(id).onclick = f; };
  on("gFile", () => send("gClientFile"));
  on("gOn", () => send("gConnect"));
  on("gOff", () => send("gDisconnect"));
  on("gOpen", () => send("open", { view: "calendar" }));
}
// True when a font family really exists: its text measures differently from both fallbacks.
function fontInstalled(name) {
  const c = document.createElement("canvas").getContext("2d"), s = "mmmmmllli 0123456789";
  return ["monospace", "serif"].every(f => { c.font = `40px ${f}`; const a = c.measureText(s).width; c.font = `40px "${name}", ${f}`; return c.measureText(s).width !== a; });
}
function renderPersDyn() {
  if (!$("thumbs")) return;
  const S = st.settings;
  $("thumbs").innerHTML = allWalls().map(u => `<div class="thumb ${u === S.wall ? "on" : ""}" data-u="${esc(u)}" style="background-image:url('${esc(thumbUrl(u))}')">${PRESETS.includes(u) ? "" : `<span class="rm" title="Remove">×</span>`}</div>`).join("") +
    `<div class="thumb add" id="addp" title="Add your own photo"><i class="ti ti-plus"></i></div>`;
  $("thumbs").querySelectorAll(".thumb[data-u]").forEach(t => t.onclick = e => {
    if (e.target.classList.contains("rm")) { send("removePhoto", { url: t.dataset.u }); if (t.dataset.u === S.wall) setWall(PRESETS[0], true); return; }
    setWall(t.dataset.u, true);
  });
  $("addp").onclick = () => send("addPhoto");
  $("autoTag").textContent = S.auto === false ? "CUSTOM" : "AUTO FROM PHOTO";
  const pal = S.palette && S.palette.length ? S.palette : [S.acc];
  $("sw").innerHTML = pal.map(c => `<span style="background:${c}" data-c="${c}" class="${c === S.acc ? "on" : ""}"></span>`).join("");
  $("sw").querySelectorAll("span").forEach(e => e.onclick = () => setSettings({ acc: e.dataset.c, acc2: pickSecond([e.dataset.c, ...pal.filter(x => x !== e.dataset.c)]), auto: true }));
  if (document.activeElement !== $("c1")) $("c1").value = S.acc;
  if (document.activeElement !== $("c2")) $("c2").value = S.acc2;
  if (document.activeElement !== $("ics")) $("ics").value = S.icsUrl || "";
  $("calst").textContent = calStatus();
  [["dark", "%"], ["wb", "px"], ["sat", "%"], ["pa", "%"], ["bl", "px"], ["ti", "%"], ["iconColor", "%"]].forEach(([k, u]) => { if (document.activeElement !== $("s_" + k)) $("s_" + k).value = S[k]; $("o_" + k).textContent = S[k] + u; });
  ["iconTint", "widgets", "clock24", "dispClock", "maximizeNew", "dockAutoHide"].forEach(k => $("t_" + k).checked = !!S[k]);
  $("fname").textContent = (S.fontName || (fontInstalled("Harmond") || fontInstalled("Harmond Display") ? "Harmond (installed)" : "Stand-in serif")).toUpperCase();
  $("fprev").textContent = fmtTime(new Date()).t;
  $("t_f").checked = S.units === "f";
  $("t_fontAll").checked = !!S.fontAll;
  if (document.activeElement !== $("label")) $("label").value = S.label || "";
  $("city").placeholder = `Search a city (now ${S.city})`;
}

// ================= ambient (fullscreen clock) =================
let ambTimer = 0, hintTimer = 0;
function renderAmbient() {
  root.innerHTML = `<div id="amb">
    <div class="wx" id="awx"></div><div class="hint" id="ahint">ESC OR CLICK TO RETURN</div>
    <div class="lbl mono" id="albl"></div>
    <div class="big" id="abig"></div>
    <div class="sub"><span id="aday"></span><i class="sepd"></i><span id="adate"></span></div>
    <div class="sec"><i id="asec"></i></div></div>`;
  const leave = () => send("hide");
  document.addEventListener("keydown", leave);
  document.addEventListener("mousedown", leave);
  document.addEventListener("contextmenu", e => e.preventDefault());
  tickAmbient(); clearInterval(ambTimer); ambTimer = setInterval(tickAmbient, 1000);
}
function tickAmbient() {
  if (!$("abig")) return;
  const d = new Date(), f = fmtTime(d);
  $("abig").innerHTML = `${f.t}${f.ap ? `<small>${f.ap}</small>` : ""}`;
  $("albl").textContent = (st.settings.label || "").toUpperCase();
  $("aday").textContent = d.toLocaleDateString(undefined, { weekday: "long" });
  $("adate").textContent = d.toLocaleDateString(undefined, { day: "numeric", month: "long", year: "numeric" });
  $("asec").style.width = (d.getSeconds() / 59 * 100) + "%";
}
async function ambientWeather() {
  const S = st.settings;
  try {
    const r = await (await fetch(`https://api.open-meteo.com/v1/forecast?latitude=${S.lat}&longitude=${S.lon}&current=temperature_2m,weather_code&timezone=auto${S.units === "f" ? "&temperature_unit=fahrenheit" : ""}`)).json();
    $("awx").textContent = `${S.city.toUpperCase()} · ${Math.round(r.current.temperature_2m)}° · ${(WCODES[r.current.weather_code] || "").toUpperCase()}`;
  } catch (e) { $("awx").textContent = S.city.toUpperCase(); }
}

// ================= host messages =================
function onMsg(m) {
  switch (m.type) {
    case "init": {
      Object.assign(st, { scale: m.scale, screen: m.screen, rect: m.rect, config: m.config || st.config, wallpapers: m.wallpapers || [] });
      const fresh = !m.settings;
      st.settings = Object.assign({}, DEF, m.settings || {});
      placeBackdrop(); applyTheme();
      ({ desktop: renderDesktop, topbar: renderTopbar, dock: renderDock, launcher: renderLauncher, personalize: renderPers, ambient: renderAmbient, calendar: renderCalendarView, preview: renderPreview })[VIEW]();
      st.ready = true;
      if (fresh && VIEW === "personalize") setWall(DEF.wall, true);
      break;
    }
    case "rect": st.rect = m.rect; st.screen = m.screen; st.scale = m.scale; placeBackdrop(); break;
    case "settings":
      st.settings = Object.assign({}, DEF, m.settings); applyTheme();
      if (VIEW === "personalize") renderPersDyn();
      if (VIEW === "desktop") renderDesktopDyn();
      if (VIEW === "calendar") cvCals();
      break;
    case "config": st.config = m.config; dockSig = ""; if (VIEW === "dock") renderDock(); break;
    case "windows": st.windows = m; if (VIEW === "dock") renderDock(); if (VIEW === "topbar") renderTopbarDyn(); if (VIEW === "launcher" && $("lq") && !$("lq").value) filterLauncher(); break;
    case "icons": Object.assign(st.icons, m.map); if (VIEW === "dock") renderDock(); if (VIEW === "launcher" && $("res")) drawResults(); break;
    case "stats": st.stats = m; if (VIEW === "desktop") renderStats(); if (VIEW === "topbar") renderTopbarDyn(); break;
    case "media": st.media = m; if (VIEW === "desktop") renderMedia(); if (VIEW === "topbar") renderTopbarDyn(); break;
    case "sessions": st.sessions = m.list; if (VIEW === "desktop") renderSessions(); break;
    case "usage": st.usage = m; if (VIEW === "desktop") renderUsage(); break;
    case "live":
      if (VIEW === "desktop" && $("lv")) { $("lempty").style.display = m.has ? "none" : ""; $("ltitle").textContent = m.has ? m.title || "" : ""; }
      break;
    case "resetLayout": if (VIEW === "desktop") setSettings({ layout: {} }); break;
    case "preview": if (VIEW === "preview") drawPreview(m); break;
    case "desktops": st.desktops = m; if (VIEW === "topbar") renderTopbarDyn(); break;
    case "startApps": st.startApps = m.list; if (VIEW === "launcher" && $("lq") && $("lq").value) filterLauncher(); break;
    case "wallpapers":
      st.wallpapers = m.list || [];
      if (VIEW === "personalize") { renderPersDyn(); if (m.added) setWall(m.added, true); }
      if (VIEW === "desktop") renderDesktopDyn();
      break;
    case "show":
      if (VIEW === "launcher") { $("lq").value = ""; lsel = 0; filterLauncher(); setTimeout(() => $("lq").focus(), 30); }
      if (VIEW === "personalize") { renderPersDyn(); send("calSources"); }
      if (VIEW === "calendar") { CV.date = new Date(); CV.mini = null; CV.scrolled = false; cvRender(); }
      if (VIEW === "ambient") {
        tickAmbient(); ambientWeather();
        $("ahint").style.opacity = 1; clearTimeout(hintTimer); hintTimer = setTimeout(() => $("ahint").style.opacity = 0, 3000);
      }
      break;
    case "fontAdded": setSettings({ fontUrl: m.url, fontName: m.name }); break;
    case "dockMode": document.body.classList.toggle("parked", !!m.parked); wave(); break;
    case "events":
      st.events = m;
      if (VIEW === "desktop") renderCalendar();
      if (VIEW === "topbar") renderNextEvent();
      if (VIEW === "personalize" && $("calst")) { $("calst").textContent = calStatus(); renderGoogle(); }
      if (VIEW === "calendar") { CV.cache.clear(); cvRender(); }
      break;
    case "calSources":
      st.calSources = m;
      if (VIEW === "personalize") renderGoogle();
      if (VIEW === "calendar") cvCals();
      break;
    case "calData": if (VIEW === "calendar") calOnData(m); break;
  }
}
if (wv) wv.addEventListener("message", e => { try { onMsg(e.data); } catch (err) { console.error(err); } });
send("ready");
if (!wv) onMsg({ type: "init", scale: 1, screen: { w: innerWidth, h: innerHeight }, rect: { x: 0, y: 0, w: innerWidth, h: innerHeight }, config: { apps: [], layouts: [] } });

