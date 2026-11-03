/* SchoolBell console front-end: vanilla JS against the JSON API. */
"use strict";

const $ = (id) => document.getElementById(id);

const state = {
  data: null,          // last /api/state
  next: null,          // last /api/next
  day: null,           // last /api/day (server's today)
  clients: [],
  log: [],
  view: "today",
  selectedSchedule: null,
  driftMs: 0,          // server_time - local time, refreshed on each /api/next
};

const DAY_NAMES = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const BELL_WORDS = { warn: "Pre-bell", start: "Start", end: "End" };

const esc = (s) => String(s ?? "").replace(/[&<>"']/g,
  (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

function hm(iso) { return iso.slice(11, 16); }     // wall time in the school's tz
function toast(msg, isErr = false) {
  const t = $("toast");
  t.textContent = msg;
  t.classList.toggle("err", isErr);
  t.hidden = false;
  clearTimeout(toast._t);
  toast._t = setTimeout(() => { t.hidden = true; }, 2600);
}

async function api(path, opts = {}) {
  const r = await fetch(path, { headers: { "Content-Type": "application/json" }, ...opts });
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.detail || `request failed (${r.status})`);
  return body;
}

/* ---------------- server clock + countdown ---------------- */

function serverNowMs() {
  if (!state.next) return null;
  return Date.parse(state.next.server_time) + (Date.now() - state.next._fetchedAt);
}

function wallClock(ms, tz) {
  return new Intl.DateTimeFormat("en-GB", {
    timeZone: tz, hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  }).format(ms);
}

function tickClock() {
  if (!state.next) return;
  const nowMs = serverNowMs();
  $("clock-time").textContent = wallClock(nowMs, state.next.timezone);
  $("clock-tz").textContent = state.next.timezone;
  const bells = state.next.bells || [];
  const next = bells[0];
  const cd = $("nb-countdown");
  const label = $("nb-label");
  const sub = $("nb-sub");
  if (!next) {
    label.innerHTML = "Next bell — <strong>none scheduled</strong>";
    cd.textContent = "No upcoming bells";
    cd.classList.add("idle");
    renderNextLabel(sub);
    return;
  }
  cd.classList.remove("idle");
  let ms = Date.parse(next.dt) - nowMs;
  if (ms >= 86400000) {
    cd.textContent = "Tomorrow " + hm(next.dt);
  } else {
    ms = Math.max(0, ms);
    const s = Math.floor(ms / 1000);
    const parts = [Math.floor(s / 3600), Math.floor(s / 60) % 60, s % 60];
    cd.textContent = parts[0]
      ? `${parts[0]}:${String(parts[1]).padStart(2, "0")}:${String(parts[2]).padStart(2, "0")}`
      : `${String(parts[1]).padStart(2, "0")}:${String(parts[2]).padStart(2, "0")}`;
  }
  label.innerHTML = `Next bell — <strong>${BELL_WORDS[next.bell_type]} · ${esc(next.period_name)}</strong> ` +
    `at ${hm(next.dt)}${daySuffix(next.dt, nowMs)}`;
  renderNextLabel(sub);
  if (next.dt.slice(0, 10) !== state.next.server_time.slice(0, 10)) {
    sub.textContent += " · no more bells today";
  }
}

function daySuffix(bellDt, nowMs) {
  if (!state.next) return "";
  const bellDay = bellDt.slice(0, 10);
  const today = state.next.server_time.slice(0, 10);
  if (bellDay === today) return "";
  const d = new Date(Date.parse(bellDt));
  const wd = new Intl.DateTimeFormat("en-GB", {
    timeZone: state.next.timezone, weekday: "short", day: "numeric", month: "short",
  }).format(d);
  return `, ${wd}`;
}

function renderNextLabel(subEl = $("nb-sub")) {
  const day = state.day;
  const sub = subEl;
  if (!day) { sub.textContent = ""; return; }
  if (day.schedule_name) {
    sub.textContent = `Today: ${day.schedule_name} (${day.reason === "weekday" ? "weekly default" : "override"})`;
  } else if (day.reason === "holiday") {
    const hol = (state.data?.holidays || []).find((h) => h.date === day.date);
    sub.textContent = `Today is a holiday${hol ? `: ${hol.name}` : ""} — no bells`;
  } else if (day.reason === "override") {
    sub.textContent = "Today is overridden to no bells";
  } else {
    sub.textContent = "No schedule assigned to today’s weekday yet";
  }
}

/* ---------------- Today view ---------------- */

async function refreshToday() {
  const [next, day, clients] = await Promise.all([
    api("api/next"), api("api/day"), api("api/clients"),
  ]);
  next._fetchedAt = Date.now();
  state.next = next; state.day = day; state.clients = clients.clients;
  const nowMs = serverNowMs();
  const bells = day.bells || [];
  const table = $("today-table"), body = $("today-body"), empty = $("today-empty");
  if (!bells.length) {
    table.hidden = true;
    empty.hidden = false;
    empty.textContent = state.data?.schedules.length
      ? "No bells today. Assign this weekday a schedule in Calendar, or add an override."
      : "No bells yet. Create a schedule in Schedules, then assign it to weekdays in Calendar.";
  } else {
    table.hidden = false; empty.hidden = true;
    let nextRowDone = false;
    body.innerHTML = bells.map((b) => {
      const isPast = Date.parse(b.dt) < nowMs;
      const isNext = !isPast && !nextRowDone;
      if (isNext) nextRowDone = true;
      const cls = isPast ? "past" : isNext ? "next" : "";
      return `<tr class="${cls}"><td class="num">${hm(b.dt)}</td>` +
        `<td><span class="bell-tag ${b.bell_type}">${BELL_WORDS[b.bell_type]}</span></td>` +
        `<td>${esc(b.period_name)}</td></tr>`;
    }).join("");
  }
  const list = $("today-clients"), ce = $("today-clients-empty");
  list.innerHTML = state.clients.map((c) =>
    `<li><span class="dot ${c.online ? "on" : ""}"></span>${esc(c.name)}` +
    `<span class="client-state">${c.online ? "online" : "offline"}</span>` +
    `<span class="mono" style="margin-left:auto">${hm(c.last_seen)}</span></li>`).join("");
  ce.hidden = state.clients.length > 0;
  tickClock();
}

/* ---------------- Schedules view ---------------- */

function schedById(id) { return (state.data?.schedules || []).find((s) => s.id === id); }

function renderSchedules() {
  const list = $("sched-list");
  const schedules = state.data?.schedules || [];
  $("sched-empty").hidden = schedules.length > 0;
  list.innerHTML = schedules.map((s) =>
    `<li><button data-sid="${s.id}" class="${s.id === state.selectedSchedule ? "is-selected" : ""}">` +
    `<span>${esc(s.name)}</span><span class="period-count">${s.periods.length} period${s.periods.length === 1 ? "" : "s"}</span></button></li>`
  ).join("");
  list.querySelectorAll("button").forEach((b) => b.addEventListener("click", () => {
    state.selectedSchedule = Number(b.dataset.sid);
    renderSchedules();
  }));
  const ed = $("sched-editor");
  const sel = schedById(state.selectedSchedule);
  if (!sel) {
    ed.hidden = true;
    $("sched-no-selection").hidden = schedules.length === 0;
    $("sched-no-selection").textContent = schedules.length
      ? "Select a schedule to edit its periods." : "Create a schedule to get started.";
    return;
  }
  ed.hidden = false; $("sched-no-selection").hidden = true;
  $("edit-sched-name").value = sel.name;
  $("edit-sched-note").value = sel.note;
  const body = $("period-body");
  const rows = [...sel.periods].sort((a, b) => a.start_hm.localeCompare(b.start_hm));
  body.innerHTML = rows.map((p, i) =>
    `<tr><td class="num">${i + 1}</td>` +
    `<td><input class="period-name" value="${esc(p.name)}" maxlength="60"></td>` +
    `<td><input class="period-time period-start" type="text" inputmode="numeric" pattern="[0-2]?[0-9]:[0-5][0-9]" maxlength="5" placeholder="HH:MM" value="${esc(p.start_hm)}"></td>` +
    `<td><input class="period-time period-end" type="text" inputmode="numeric" pattern="[0-2]?[0-9]:[0-5][0-9]" maxlength="5" placeholder="HH:MM" value="${esc(p.end_hm)}"></td>` +
    `<td><input class="period-warn" type="number" min="0" max="240" value="${p.warn_min}"></td>` +
    `<td><button class="row-del" aria-label="Remove period" title="Remove period" data-rid="${p.id ?? "new"}">Remove</button></td></tr>`
  ).join("");
  body.querySelectorAll(".row-del").forEach((b) => b.addEventListener("click", () => {
    b.closest("tr").remove();
    body.querySelectorAll("tr td:first-child").forEach((td, i) => { td.textContent = i + 1; });
  }));
}

function collectPeriods() {
  const rows = [...$("period-body").querySelectorAll("tr")];
  return rows.map((tr) => ({
    name: tr.querySelector(".period-name").value.trim(),
    start_hm: tr.querySelector(".period-start").value.trim(),
    end_hm: tr.querySelector(".period-end").value.trim(),
    warn_min: Number(tr.querySelector(".period-warn").value || 0),
  }));
}

function schedError(msg) {
  const el = $("sched-error");
  el.textContent = msg; el.hidden = !msg;
}

async function saveScheduleMeta() {
  const sel = schedById(state.selectedSchedule);
  if (!sel) return;
  const name = $("edit-sched-name").value.trim();
  const note = $("edit-sched-note").value.trim();
  if (!name) { schedError("Schedule name is required."); return; }
  try {
    await api(`api/schedules/${sel.id}`, { method: "PUT", body: JSON.stringify({ name, note }) });
    await reloadState(); renderSchedules();
    toast("Schedule saved");
  } catch (e) { schedError(e.message); }
}

async function init() {
  /* ---- nav ---- */
  $("nav").querySelectorAll(".nav-btn").forEach((b) => b.addEventListener("click", () => {
    state.view = b.dataset.view;
    $("nav").querySelectorAll(".nav-btn").forEach((x) => x.classList.toggle("is-active", x === b));
    document.querySelectorAll(".view").forEach((v) =>
      v.classList.toggle("is-active", v.id === "view-" + state.view));
    renderActiveView();
  }));

  /* ---- schedules handlers ---- */
  $("new-schedule-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const name = $("new-schedule-name").value.trim();
    if (!name) return;
    try {
      const r = await api("api/schedules", { method: "POST", body: JSON.stringify({ name }) });
      $("new-schedule-name").value = "";
      state.selectedSchedule = r.id;
      await reloadState(); renderSchedules();
      toast("Schedule created — add its periods");
    } catch (e2) { toast(e2.message, true); }
  });
  $("edit-sched-name").addEventListener("change", saveScheduleMeta);
  $("edit-sched-note").addEventListener("change", saveScheduleMeta);
  $("add-period").addEventListener("click", () => {
    const body = $("period-body");
    const tr = document.createElement("tr");
    tr.innerHTML = `<td class="num"></td>` +
      `<td><input class="period-name" value="" maxlength="60" placeholder="Period name"></td>` +
      `<td><input class="period-time period-start" type="text" inputmode="numeric" pattern="[0-2]?[0-9]:[0-5][0-9]" maxlength="5" placeholder="HH:MM" value=""></td>` +
      `<td><input class="period-time period-end" type="text" inputmode="numeric" pattern="[0-2]?[0-9]:[0-5][0-9]" maxlength="5" placeholder="HH:MM" value=""></td>` +
      `<td><input class="period-warn" type="number" min="0" max="240" value="0"></td>` +
      `<td><button class="row-del" aria-label="Remove period" title="Remove period">Remove</button></td>`;
    body.appendChild(tr);
    body.querySelectorAll("tr td:first-child").forEach((td, i) => { td.textContent = i + 1; });
    tr.querySelector(".row-del").addEventListener("click", () => {
      tr.remove();
      body.querySelectorAll("tr td:first-child").forEach((td, i) => { td.textContent = i + 1; });
    });
    tr.querySelector(".period-name").focus();
  });
  $("save-periods").addEventListener("click", async () => {
    const sel = schedById(state.selectedSchedule);
    if (!sel) return;
    try {
      await api(`api/schedules/${sel.id}/periods`,
        { method: "PUT", body: JSON.stringify({ periods: collectPeriods() }) });
      await reloadState(); renderSchedules();
      schedError("");
      toast("Periods saved");
    } catch (e) { schedError(e.message); }
  });
  $("del-sched").addEventListener("click", async () => {
    const sel = schedById(state.selectedSchedule);
    if (!sel || !confirm(`Delete schedule “${sel.name}” and all its periods? Weekday slots using it will go blank.`)) return;
    try {
      await api(`api/schedules/${sel.id}`, { method: "DELETE" });
      state.selectedSchedule = null;
      await reloadState(); renderSchedules();
      toast("Schedule deleted");
    } catch (e) { toast(e.message, true); }
  });
  $("dup-sched").addEventListener("click", async () => {
    const sel = schedById(state.selectedSchedule);
    if (!sel) return;
    try {
      let base = `${sel.name} (copy)`, name = base;
      for (let n = 2; ; n++) {
        try {
          const r = await api("api/schedules", { method: "POST", body: JSON.stringify({ name }) });
          await api(`api/schedules/${r.id}/periods`,
            { method: "PUT", body: JSON.stringify({ periods: collectPeriods() }) });
          state.selectedSchedule = r.id;
          await reloadState(); renderSchedules();
          toast("Schedule duplicated");
          return;
        } catch (e) {
          if (!/already exists/.test(e.message)) throw e;
          name = `${base} ${n}`;
        }
      }
    } catch (e) { toast(e.message, true); }
  });

  /* ---- calendar handlers ---- */
  $("save-calendar").addEventListener("click", async () => {
    const weekdays = [...$("weekday-body").querySelectorAll("select")].map((s) =>
      s.value === "" ? null : Number(s.value));
    try {
      await api("api/calendar", { method: "PUT", body: JSON.stringify({ weekdays }) });
      await reloadState(); renderCalendar();
      showCalError(""); toast("Week saved");
    } catch (e) { showCalError(e.message); }
  });
  $("override-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const date = $("override-date").value;
    const v = $("override-sched").value;
    try {
      await api("api/overrides", { method: "POST",
        body: JSON.stringify({ date, schedule_id: v === "" ? null : Number(v) }) });
      await reloadState(); renderCalendar();
      showCalError(""); toast("Override set");
    } catch (e2) { showCalError(e2.message); }
  });
  $("holiday-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      await api("api/holidays", { method: "POST",
        body: JSON.stringify({ date: $("holiday-date").value, name: $("holiday-name").value }) });
      $("holiday-name").value = "";
      await reloadState(); renderCalendar();
      showCalError(""); toast("Holiday added");
    } catch (e2) { showCalError(e2.message); }
  });

  /* ---- settings handlers ---- */
  $("save-settings").addEventListener("click", async () => {
    try {
      await api("api/settings", { method: "PUT", body: JSON.stringify({
        school_name: $("set-school").value,
        timezone: $("set-tz").value.trim(),
        sound_warn: $("set-sound-warn").value,
        sound_start: $("set-sound-start").value,
        sound_end: $("set-sound-end").value,
      }) });
      await reloadState(); renderSettings();
      setErr(""); toast("Settings saved");
    } catch (e) { setErr(e.message); }
  });
  document.querySelectorAll("[data-preview]").forEach((b) => b.addEventListener("click", () => {
    const key = { warn: "set-sound-warn", start: "set-sound-start", end: "set-sound-end" }[b.dataset.preview];
    const a = $("preview-audio");
    a.src = `sounds/${$(key).value}`;
    a.play().catch(() => toast("Could not play sound in this browser", true));
  }));

  /* ---- boot ---- */
  await reloadState();
  await refreshToday();
  renderActiveView();
  setInterval(tickClock, 1000);
  setInterval(async () => {           // slow heartbeat for live views
    if (state.view === "today") { await refreshToday().catch(() => {}); }
    else if (state.view === "activity") { await refreshActivity().catch(() => {}); }
  }, 20000);
}

function showCalError(msg) { const el = $("cal-error"); el.textContent = msg; el.hidden = !msg; }
function setErr(msg) { const el = $("set-error"); el.textContent = msg; el.hidden = !msg; }

async function reloadState() {
  state.data = await api("api/state");
  $("school-name").textContent = state.data.settings.school_name || "";
  $("eff-tz").textContent = state.data.timezone;
}

/* ---------------- Calendar view ---------------- */

function scheduleOptions(selected) {
  return `<option value="">No bells</option>` + (state.data?.schedules || []).map((s) =>
    `<option value="${s.id}" ${s.id === selected ? "selected" : ""}>${esc(s.name)}</option>`).join("");
}

function renderCalendar() {
  const wd = state.data?.weekday_defaults || [];
  $("weekday-body").innerHTML = DAY_NAMES.map((d, i) =>
    `<tr><td>${d}</td><td><select data-day="${i}">${scheduleOptions(wd[i])}</select></td></tr>`).join("");
  const ov = state.data?.overrides || [];
  $("override-table").hidden = ov.length === 0;
  $("override-empty").hidden = ov.length > 0;
  $("override-body").innerHTML = ov.map((o) =>
    `<tr><td class="num">${o.date}</td><td>${esc(o.schedule_name ?? "No bells")}</td>` +
    `<td><button class="row-del" aria-label="Remove override" data-del-override="${o.id}">Remove</button></td></tr>`).join("");
  $("override-body").querySelectorAll("[data-del-override]").forEach((b) =>
    b.addEventListener("click", async () => {
      try { await api(`api/overrides/${b.dataset.delOverride}`, { method: "DELETE" }); await reloadState(); renderCalendar(); }
      catch (e) { showCalError(e.message); }
    }));
  $("override-sched").innerHTML = scheduleOptions("");
  const hol = state.data?.holidays || [];
  $("holiday-table").hidden = hol.length === 0;
  $("holiday-empty").hidden = hol.length > 0;
  $("holiday-body").innerHTML = hol.map((h) =>
    `<tr><td class="num">${h.date}</td><td>${esc(h.name)}</td>` +
    `<td><button class="row-del" aria-label="Remove holiday" data-del-holiday="${h.id}">Remove</button></td></tr>`).join("");
  $("holiday-body").querySelectorAll("[data-del-holiday]").forEach((b) =>
    b.addEventListener("click", async () => {
      try { await api(`api/holidays/${b.dataset.delHoliday}`, { method: "DELETE" }); await reloadState(); renderCalendar(); }
      catch (e) { showCalError(e.message); }
    }));
}

/* ---------------- Activity view ---------------- */

async function refreshActivity() {
  const [clients, log] = await Promise.all([api("api/clients"), api("api/log")]);
  state.clients = clients.clients; state.log = log.log;
  const ct = $("clients-table"), ce = $("clients-empty");
  ct.hidden = state.clients.length === 0; ce.hidden = state.clients.length > 0;
  $("clients-body").innerHTML = state.clients.map((c) =>
    `<tr><td>${esc(c.name)}</td><td>${c.online ? "online" : "offline"}</td>` +
    `<td class="num">${c.last_seen.slice(0, 10)} ${hm(c.last_seen)}</td></tr>`).join("");
  const lt = $("log-table"), le = $("log-empty");
  lt.hidden = state.log.length === 0; le.hidden = state.log.length > 0;
  $("log-body").innerHTML = state.log.map((l) =>
    `<tr><td>${esc(l.client)}</td><td class="num">${l.date}</td><td class="num">${l.time_hm}</td>` +
    `<td><span class="bell-tag ${l.bell_type}">${BELL_WORDS[l.bell_type] || esc(l.bell_type)}</span></td>` +
    `<td>${esc(l.period_name ?? "")}${l.period_num ? ` (${l.period_num})` : ""}</td></tr>`).join("");
}

/* ---------------- Settings view ---------------- */

function renderSettings() {
  const s = state.data?.settings || {};
  $("set-school").value = s.school_name || "";
  $("set-tz").value = s.timezone || "";
  const sounds = state.data?.valid_sounds || [];
  for (const [selId, key] of [["set-sound-warn", "sound_warn"], ["set-sound-start", "sound_start"], ["set-sound-end", "sound_end"]]) {
    $(selId).innerHTML = sounds.map((f) =>
      `<option ${s[key] === f ? "selected" : ""}>${f}</option>`).join("");
  }
}

/* ---------------- routing between views ---------------- */

async function renderActiveView() {
  try {
    if (state.view === "today") await refreshToday();
    else if (state.view === "schedules") renderSchedules();
    else if (state.view === "calendar") renderCalendar();
    else if (state.view === "activity") await refreshActivity();
    else if (state.view === "settings") renderSettings();
  } catch (e) { toast(e.message, true); }
}

init().catch((e) => {
  $("nb-label").textContent = "Server unreachable";
  $("nb-sub").textContent = e.message;
});
