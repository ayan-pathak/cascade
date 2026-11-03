"use strict";
// Full calendar window: day, 4 days, week, month, year and schedule views (uses helpers from app.js).
const CV = { view: "week", date: new Date(), mini: null, cache: new Map(), key: "", events: [], scrolled: false };
const HOUR = 46;
const VIEWS = [["day", "Day", "D"], ["4day", "4 days", "X"], ["week", "Week", "W"], ["month", "Month", "M"], ["year", "Year", "Y"], ["schedule", "Schedule", "A"]];
const sod = d => new Date(d.getFullYear(), d.getMonth(), d.getDate());
const addDays = (d, n) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const isoDay = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}T00:00:00`;
const weekStart = d => addDays(sod(d), -((d.getDay() + 6) % 7));
const allDayish = e => e.allDay || (e.en - e.s) >= 864e5;

function calMap() {
  const m = {};
  ((st.calSources && st.calSources.calendars) || (st.events && st.events.calendars) || []).forEach(c => m[c.id] = c);
  return m;
}
function evColor(e) { const c = calMap()[e.cal]; return e.color || (c && c.color) || "var(--acc)"; }
const hiddenSet = () => new Set(st.settings.calHidden || []);
function plainText(html) {
  if (!html) return "";
  const doc = new DOMParser().parseFromString(String(html).replace(/<br\s*\/?>/gi, "\n").replace(/<\/p>/gi, "\n"), "text/html");
  return (doc.body.textContent || "").trim();
}
const hourLabel = h => { if (st.settings.clock24) return String(h).padStart(2, "0") + ":00"; const ap = h >= 12 ? "pm" : "am"; return (h % 12 || 12) + " " + ap; };
const rangeText = e => e.allDay ? "All day" : `${shortTime(e.s)} – ${shortTime(e.en)}`;

function cvRange() {
  const d = sod(CV.date);
  switch (CV.view) {
    case "day": return [d, addDays(d, 1)];
    case "4day": return [d, addDays(d, 4)];
    case "week": { const s = weekStart(d); return [s, addDays(s, 7)]; }
    case "month": { const s = weekStart(new Date(d.getFullYear(), d.getMonth(), 1)); return [s, addDays(s, 42)]; }
    case "year": return [new Date(d.getFullYear(), 0, 1), new Date(d.getFullYear() + 1, 0, 1)];
    default: return [d, addDays(d, 60)];
  }
}
function cvStep(dir) {
  const d = CV.date;
  switch (CV.view) {
    case "day": CV.date = addDays(d, dir); break;
    case "4day": CV.date = addDays(d, 4 * dir); break;
    case "week": CV.date = addDays(d, 7 * dir); break;
    case "month": CV.date = new Date(d.getFullYear(), d.getMonth() + dir, 1); break;
    case "year": CV.date = new Date(d.getFullYear() + dir, 0, 1); break;
    default: CV.date = addDays(d, 30 * dir);
  }
  CV.mini = null; cvRender();
}
function cvTitle() {
  const [a, b] = cvRange(), last = addDays(b, -1);
  const o = (d, x) => d.toLocaleDateString(undefined, x);
  switch (CV.view) {
    case "day": return o(a, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
    case "month": return o(CV.date, { month: "long", year: "numeric" });
    case "year": return String(CV.date.getFullYear());
    case "schedule": return "From " + o(a, { day: "numeric", month: "long", year: "numeric" });
    default: return a.getMonth() === last.getMonth()
      ? `${o(a, { month: "long" })} ${a.getDate()} – ${last.getDate()}, ${a.getFullYear()}`
      : `${o(a, { month: "short", day: "numeric" })} – ${o(last, { month: "short", day: "numeric" })}, ${last.getFullYear()}`;
  }
}
function cvSetView(v) { CV.view = v; CV.scrolled = false; setSettings({ calView: v }); cvRender(); }
function cvGoDay(d) { CV.date = d; CV.view = "day"; CV.scrolled = false; setSettings({ calView: "day" }); cvRender(); }

function renderCalendarView() {
  root.innerHTML = `<div id="calv">
    <aside id="cside">
      <button class="cv-create" id="cvCreate"><i class="ti ti-plus"></i>Create</button>
      <div id="cvMini"></div>
      <div id="cvCals"></div>
      <div class="cv-foot"><button id="cvRefresh"><i class="ti ti-refresh"></i>Refresh</button><button id="cvSetup"><i class="ti ti-user-cog"></i>Accounts</button></div>
    </aside>
    <section id="cmain">
      <header id="chead">
        <button class="cv-btn" id="cvToday">Today</button>
        <button class="cv-ic" id="cvPrev" title="Previous (K)"><i class="ti ti-chevron-left"></i></button>
        <button class="cv-ic" id="cvNext" title="Next (J)"><i class="ti ti-chevron-right"></i></button>
        <h2 class="cv-title" id="cvTitle"></h2><span class="cv-load" id="cvLoad"></span>
        <div class="cv-seg" id="cvSeg">${VIEWS.map(v => `<button data-v="${v[0]}" title="${v[1]} (${v[2]})">${v[1]}</button>`).join("")}</div>
        <button class="cv-ic" id="cvClose" title="Close (Esc)"><i class="ti ti-x"></i></button>
      </header>
      <div id="cbody"></div>
    </section>
    <div id="cvPop"></div></div>`;
  $("cvToday").onclick = () => { CV.date = new Date(); CV.mini = null; CV.scrolled = false; cvRender(); };
  $("cvPrev").onclick = () => cvStep(-1);
  $("cvNext").onclick = () => cvStep(1);
  $("cvClose").onclick = () => send("hide");
  $("cvSeg").querySelectorAll("button").forEach(b => b.onclick = () => cvSetView(b.dataset.v));
  $("cvCreate").onclick = () => send("openUrl", { url: "https://calendar.google.com/calendar/r/eventedit" });
  $("cvRefresh").onclick = () => { CV.cache.clear(); send("calRefresh"); cvLoad(); };
  $("cvSetup").onclick = () => send("open", { view: "personalize" });
  $("cbody").addEventListener("click", e => {
    const ev = e.target.closest("[data-i]");
    if (ev) { e.stopPropagation(); cvPop(+ev.dataset.i, e.clientX, e.clientY); return; }
    const day = e.target.closest("[data-day]");
    if (day && (e.target.closest(".cv-daynum") || e.target.closest(".cv-more") || CV.view === "year")) cvGoDay(new Date(day.dataset.day));
  });
  document.addEventListener("mousedown", e => { if (!e.target.closest("#cvPop") && !e.target.closest("[data-i]")) cvPopClose(); });
  document.addEventListener("keydown", e => {
    if (e.target.matches && e.target.matches("input,textarea")) return;
    const k = e.key.toLowerCase();
    if (k === "escape") { if ($("cvPop").classList.contains("on")) cvPopClose(); else send("hide"); }
    else if (k === "t") $("cvToday").click();
    else if (k === "j" || k === "n" || k === "arrowright") cvStep(1);
    else if (k === "k" || k === "p" || k === "arrowleft") cvStep(-1);
    else { const v = VIEWS.find(x => x[2].toLowerCase() === k); if (v) cvSetView(v[0]); }
  });
  CV.view = st.settings.calView || "week";
  cvRender();
}

function cvRender() {
  if (!$("cvTitle")) return;
  $("cvTitle").textContent = cvTitle();
  $("cvSeg").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.v === CV.view));
  cvMini(); cvCals(); cvLoad();
}

function cvLoad() {
  const [a, b] = cvRange();
  const key = `${isoDay(a)}|${isoDay(b)}|${[...hiddenSet()].sort().join(",")}`;
  CV.key = key;
  CV.events = CV.cache.get(key) || [];
  cvBody();
  const configured = st.events && st.events.configured;
  $("cvLoad").textContent = configured ? "Loading…" : "";
  if (configured) send("calRange", { key, from: isoDay(a), to: isoDay(b) });
}
function calOnData(m) {
  const list = (m.list || []).map(e => Object.assign({}, e, { s: new Date(e.start), en: new Date(e.end) }));
  CV.cache.set(m.key, list);
  if (m.key !== CV.key || !$("cvLoad")) return;
  CV.events = list;
  $("cvLoad").textContent = m.error ? "Some calendars didn't load" : "";
  cvBody();
}

function cvMini() {
  const box = $("cvMini"); if (!box) return;
  const m = CV.mini || new Date(CV.date.getFullYear(), CV.date.getMonth(), 1);
  CV.mini = m;
  const start = weekStart(m), [ra, rb] = cvRange(), today = new Date();
  let h = `<div class="mm-h"><b>${m.toLocaleDateString(undefined, { month: "long", year: "numeric" })}</b><button data-mm="-1"><i class="ti ti-chevron-left"></i></button><button data-mm="1"><i class="ti ti-chevron-right"></i></button></div><div class="mm-g">`;
  h += ["M", "T", "W", "T", "F", "S", "S"].map(x => `<span class="h">${x}</span>`).join("");
  for (let i = 0; i < 42; i++) {
    const d = addDays(start, i);
    const cls = [d.getMonth() !== m.getMonth() ? "o" : "", sameDay(d, today) ? "today" : "", ["day", "4day", "week"].includes(CV.view) && d >= ra && d < rb ? "in" : ""].join(" ");
    h += `<button class="${cls}" data-md="${isoDay(d)}">${d.getDate()}</button>`;
  }
  box.innerHTML = h + "</div>";
  box.querySelectorAll("[data-mm]").forEach(b => b.onclick = () => { CV.mini = new Date(m.getFullYear(), m.getMonth() + +b.dataset.mm, 1); cvMini(); });
  box.querySelectorAll("[data-md]").forEach(b => b.onclick = () => { CV.date = new Date(b.dataset.md); CV.scrolled = false; if (CV.view === "year") CV.view = "day"; cvRender(); });
}

function cvCals() {
  const box = $("cvCals"); if (!box) return;
  const cals = (st.calSources && st.calSources.calendars) || (st.events && st.events.calendars) || [];
  if (!cals.length) {
    box.innerHTML = `<div class="cv-empty-side">No calendars connected yet.<button class="cv-btn" id="cvConnect">Connect Google Calendar</button></div>`;
    $("cvConnect").onclick = () => send("open", { view: "personalize" });
    return;
  }
  const hidden = hiddenSet();
  const group = (title, list) => !list.length ? "" : `<div class="lbl" style="margin:16px 0 6px">${title}</div>` + list.map(c =>
    `<label class="cv-cal" title="${esc(c.name)}"><input type="checkbox" data-id="${esc(c.id)}" ${hidden.has(c.id) ? "" : "checked"}><span class="box" style="--c:${esc(c.color)}"></span><span class="nm">${esc(c.name)}</span></label>`).join("");
  box.innerHTML = group("My calendars", cals.filter(c => c.access === "owner")) + group("Other calendars", cals.filter(c => c.access !== "owner"));
  box.querySelectorAll("input[data-id]").forEach(i => i.onchange = () => {
    const h = hiddenSet();
    if (i.checked) h.delete(i.dataset.id); else h.add(i.dataset.id);
    setSettings({ calHidden: [...h] });
    cvLoad();
  });
}

function evsOn(day) {
  const a = sod(day), b = addDays(a, 1);
  return CV.events.filter(e => e.s < b && e.en > a);
}
function chip(e, timed) {
  const i = CV.events.indexOf(e);
  return `<div class="cv-chip ${timed ? "t" : ""}" data-i="${i}" style="--c:${evColor(e)}">${timed ? `<i></i><span class="tm">${shortTime(e.s)}</span>` : ""}${esc(e.title)}</div>`;
}

function cvBody() {
  const body = $("cbody"); if (!body) return;
  if (!(st.events && st.events.configured)) {
    body.innerHTML = `<div class="cv-empty"><i class="ti ti-calendar"></i><h3>Connect your calendars</h3><p>Sign in to Google Calendar in Accounts to see all your calendars, including ones shared with you.</p><button class="cv-btn" id="cvConnect2">Open Accounts</button></div>`;
    $("cvConnect2").onclick = () => send("open", { view: "personalize" });
    return;
  }
  const [a] = cvRange();
  if (CV.view === "day") body.innerHTML = gridView([a]);
  else if (CV.view === "4day") body.innerHTML = gridView([0, 1, 2, 3].map(i => addDays(a, i)));
  else if (CV.view === "week") body.innerHTML = gridView([0, 1, 2, 3, 4, 5, 6].map(i => addDays(a, i)));
  else if (CV.view === "month") body.innerHTML = monthView();
  else if (CV.view === "year") body.innerHTML = yearView();
  else body.innerHTML = scheduleView();
  const sc = $("cgScroll");
  if (sc) {
    if (!CV.scrolled) { const now = new Date(); sc.scrollTop = Math.max(0, (now.getHours() - 2) * HOUR); CV.scrolled = true; }
    else if (CV.lastScroll != null) sc.scrollTop = CV.lastScroll;
    sc.onscroll = () => CV.lastScroll = sc.scrollTop;
  }
}

function gridView(days) {
  const now = new Date(), cols = `56px repeat(${days.length},minmax(0,1fr))`;
  let h = `<div class="cg-head" style="grid-template-columns:${cols}"><div></div>` + days.map(d =>
    `<div class="cg-dh ${sameDay(d, now) ? "today" : ""}" data-day="${isoDay(d)}"><span>${d.toLocaleDateString(undefined, { weekday: "short" }).toUpperCase()}</span><b class="cv-daynum">${d.getDate()}</b></div>`).join("") + `</div>`;
  h += `<div class="cg-all" style="grid-template-columns:${cols}"><div class="cg-alllbl">all day</div>` + days.map(d =>
    `<div class="cg-allc">${evsOn(d).filter(allDayish).map(e => chip(e, false)).join("")}</div>`).join("") + `</div>`;
  let hours = "";
  for (let i = 1; i < 24; i++) hours += `<span style="top:${i * HOUR}px">${hourLabel(i)}</span>`;
  h += `<div class="cg-scroll" id="cgScroll"><div class="cg-body" style="grid-template-columns:${cols};height:${24 * HOUR}px;--hr:${HOUR}px"><div class="cg-hours">${hours}</div>` +
    days.map(d => `<div class="cg-col ${sameDay(d, now) ? "today" : ""}">${timedBlocks(d)}${sameDay(d, now) ? `<div class="cg-now" style="top:${(now.getHours() * 60 + now.getMinutes()) / 60 * HOUR}px"></div>` : ""}</div>`).join("") +
    `</div></div>`;
  return `<div class="cg">${h}</div>`;
}

// Lays overlapping events side by side, like Google Calendar.
function timedBlocks(day) {
  const d0 = sod(day), d1 = addDays(d0, 1);
  const items = evsOn(day).filter(e => !allDayish(e))
    .map(e => ({ e, a: Math.max(e.s, d0), b: Math.min(e.en, d1) }))
    .sort((x, y) => x.a - y.a || (y.b - y.a) - (x.b - x.a));
  let out = "", cluster = [], cols = [], end = 0;
  const flush = () => {
    cluster.forEach(it => {
      const top = (it.a - d0) / 36e5 * HOUR, height = Math.max((it.b - it.a) / 36e5 * HOUR - 2, 18), n = cols.length;
      const i = CV.events.indexOf(it.e), short = height < 34;
      out += `<div class="cg-ev ${short ? "short" : ""}" data-i="${i}" style="top:${top}px;height:${height}px;left:calc(${it.col / n * 100}% + 2px);width:calc(${100 / n}% - 4px);--c:${evColor(it.e)}"><b>${esc(it.e.title)}</b>${short ? `<span>, ${shortTime(it.e.s)}</span>` : `<span>${rangeText(it.e)}</span>`}${!short && it.e.location ? `<span>${esc(it.e.location)}</span>` : ""}</div>`;
    });
    cluster = []; cols = [];
  };
  items.forEach(it => {
    if (it.a >= end && cluster.length) flush();
    let c = cols.findIndex(last => last <= it.a);
    if (c < 0) { c = cols.length; cols.push(0); }
    cols[c] = it.b; it.col = c; cluster.push(it); end = Math.max(end, it.b);
  });
  flush();
  return out;
}

function monthView() {
  const [a] = cvRange(), month = CV.date.getMonth(), today = new Date();
  let h = `<div class="cm"><div class="cm-head">${["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map(x => `<span>${x.toUpperCase()}</span>`).join("")}</div><div class="cm-grid">`;
  for (let i = 0; i < 42; i++) {
    const d = addDays(a, i);
    const evs = evsOn(d).sort((x, y) => (allDayish(y) - allDayish(x)) || (x.s - y.s));
    const shown = evs.slice(0, 3), more = evs.length - shown.length;
    h += `<div class="cm-cell ${d.getMonth() !== month ? "o" : ""} ${sameDay(d, today) ? "today" : ""}" data-day="${isoDay(d)}"><b class="cv-daynum">${d.getDate() === 1 ? d.toLocaleDateString(undefined, { month: "short" }) + " " : ""}${d.getDate()}</b>` +
      shown.map(e => chip(e, !allDayish(e))).join("") + (more > 0 ? `<div class="cv-more">+${more} more</div>` : "") + `</div>`;
  }
  return h + `</div></div>`;
}

function yearView() {
  const y = CV.date.getFullYear(), today = new Date(), busy = {};
  CV.events.forEach(e => { for (let d = sod(e.s), n = 0; d < e.en && n < 62; d = addDays(d, 1), n++) busy[isoDay(d)] = (busy[isoDay(d)] || 0) + 1; });
  let h = `<div class="cy">`;
  for (let m = 0; m < 12; m++) {
    const first = new Date(y, m, 1), start = weekStart(first);
    h += `<div class="cy-m"><b>${first.toLocaleDateString(undefined, { month: "long" })}</b><div class="mm-g">` + ["M", "T", "W", "T", "F", "S", "S"].map(x => `<span class="h">${x}</span>`).join("");
    for (let i = 0; i < 42; i++) {
      const d = addDays(start, i), k = isoDay(d), cnt = busy[k] || 0;
      if (d.getMonth() !== m) { h += `<span></span>`; continue; }
      h += `<span class="cy-d ${sameDay(d, today) ? "today" : ""} ${cnt ? "busy" : ""}" style="${cnt ? `--n:${Math.min(cnt, 4)}` : ""}" data-day="${k}" title="${cnt ? cnt + " event" + (cnt > 1 ? "s" : "") : ""}">${d.getDate()}</span>`;
    }
    h += `</div></div>`;
  }
  return h + `</div>`;
}

function scheduleView() {
  const [a, b] = cvRange(), today = new Date();
  let h = `<div class="cs">`, any = false;
  for (let d = a; d < b; d = addDays(d, 1)) {
    const evs = evsOn(d).sort((x, y) => (allDayish(y) - allDayish(x)) || (x.s - y.s));
    if (!evs.length) continue;
    any = true;
    h += `<div class="cs-day ${sameDay(d, today) ? "today" : ""}"><div class="cs-date"><b class="cv-daynum">${d.getDate()}</b><span>${d.toLocaleDateString(undefined, { month: "short", weekday: "short" }).toUpperCase()}</span></div><div class="cs-list">` +
      evs.map(e => `<div class="cs-row" data-i="${CV.events.indexOf(e)}"><span class="dot" style="background:${evColor(e)}"></span><span class="cs-t">${allDayish(e) ? "All day" : rangeText(e)}</span><span class="cs-n">${esc(e.title)}${e.location ? `<small>${esc(e.location)}</small>` : ""}</span></div>`).join("") + `</div></div>`;
  }
  return any ? h + `</div>` : `<div class="cv-empty"><i class="ti ti-mood-smile"></i><h3>Nothing scheduled</h3><p>Your next 60 days are clear.</p></div>`;
}

function cvPop(i, x, y) {
  const e = CV.events[i], pop = $("cvPop");
  if (!e) return;
  const c = calMap()[e.cal];
  const dayTxt = d => d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" });
  const lastDay = addDays(sod(e.en), e.allDay ? -1 : 0);
  const when = e.allDay
    ? (sameDay(e.s, lastDay) ? dayTxt(e.s) : `${dayTxt(e.s)} – ${dayTxt(lastDay)}`) + " · All day"
    : sameDay(e.s, e.en) ? `${dayTxt(e.s)} · ${rangeText(e)}` : `${dayTxt(e.s)}, ${shortTime(e.s)} – ${dayTxt(e.en)}, ${shortTime(e.en)}`;
  const desc = plainText(e.desc);
  pop.innerHTML = `<div class="pp-h"><span class="sq" style="background:${evColor(e)}"></span><b>${esc(e.title)}</b><button class="cv-ic" id="ppX"><i class="ti ti-x"></i></button></div>
    <div class="pp-r"><i class="ti ti-clock"></i><span>${esc(when)}</span></div>
    ${e.location ? `<div class="pp-r"><i class="ti ti-map-pin"></i><span>${esc(e.location)}</span></div>` : ""}
    ${c ? `<div class="pp-r"><i class="ti ti-calendar"></i><span>${esc(c.name)}</span></div>` : ""}
    ${desc ? `<div class="pp-d">${esc(desc)}</div>` : ""}
    <div class="pbtn">${e.meet ? `<button class="pri" data-url="${esc(e.meet)}"><i class="ti ti-video"></i>Join meeting</button>` : ""}${e.link ? `<button data-url="${esc(e.link)}"><i class="ti ti-external-link"></i>Open in Google Calendar</button>` : ""}</div>`;
  pop.classList.add("on");
  pop.querySelectorAll("[data-url]").forEach(b => b.onclick = () => send("openUrl", { url: b.dataset.url }));
  $("ppX").onclick = cvPopClose;
  const r = pop.getBoundingClientRect();
  pop.style.left = Math.max(10, Math.min(x + 14, innerWidth - r.width - 14)) + "px";
  pop.style.top = Math.max(10, Math.min(y - 20, innerHeight - r.height - 14)) + "px";
}
function cvPopClose() { const p = $("cvPop"); if (p) p.classList.remove("on"); }
