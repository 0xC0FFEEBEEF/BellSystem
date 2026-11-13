import { useState } from "react";
import { del, fmtTime, post, put, upload } from "../api";
import { Badge, Card, Empty, Field, useBusy, useData, useMe } from "../ui";
import { RingNow } from "./Dashboard";

type Period = { name: string; start_hm: string; end_hm: string; warn_min: number; sound_id: string; volume_policy: string; duration_s: number };
type Template = { id?: number; name: string; target: any; periods: Period[]; weekdays: number[] };
const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const today = () => new Date().toISOString().slice(0, 10);
const plusDays = (n: number) => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

export function Bells() {
  const me = useMe();
  const [tab, setTab] = useState<"ring" | "schedules" | "calendar" | "sounds">("ring");
  return (
    <div>
      <nav className="tabs" aria-label="Bells sections">
        {(["ring", "schedules", "calendar", "sounds"] as const).map((t) => (
          <button key={t} className={tab === t ? "on" : ""} onClick={() => setTab(t)}>
            {{ ring: "Ring now", schedules: "Schedules", calendar: "Calendar & publish", sounds: "Bell sounds" }[t]}</button>
        ))}
      </nav>
      {tab === "ring" && (me.role === "viewer" ? <Empty>Viewers cannot ring bells.</Empty> : <div className="grid"><RingNow /><RecentRings /></div>)}
      {tab === "schedules" && <Schedules />}
      {tab === "calendar" && <Calendar />}
      {tab === "sounds" && <Sounds />}
    </div>
  );
}

function RecentRings() {
  const rings = useData<any[]>("/api/bells/rings?limit=10", 4000);
  return (
    <Card title="Recent manual bells">
      {(rings.data ?? []).length === 0 ? <Empty>None yet.</Empty> : (rings.data ?? []).map((r) => (
        <details key={r.ring_id}>
          <summary>{fmtTime(r.created_at)} · <b>{r.bell_name}</b> ({r.sound_id}) by {r.requested_by} → {r.target_label} · {Object.entries(r.counts).map(([k, v]) => <span key={k}> <Badge state={k} /> {String(v)}</span>)}</summary>
          <table><tbody>{r.devices.map((d: any) => <tr key={d.device_id}><td>{d.label}</td><td><Badge state={d.state} /></td><td className="dim">{d.error_code ?? ""}</td></tr>)}</tbody></table>
        </details>
      ))}
    </Card>
  );
}

function Schedules() {
  const me = useMe();
  const draft = useData<any>("/api/schedules/draft");
  const sounds = useData<any[]>("/api/sounds");
  const groups = useData<any[]>("/api/groups-list");
  const [edit, setEdit] = useState<Template | null>(null);
  const { busy, run } = useBusy();
  const canEdit = me.role !== "viewer";

  const blank: Template = { name: "New schedule", target: { all: true }, weekdays: [1, 2, 3, 4, 5],
    periods: [{ name: "Period 1", start_hm: "08:30", end_hm: "09:20", warn_min: 0, sound_id: "std-bell", volume_policy: "announcement", duration_s: 3 }] };

  const save = () => run(async () => {
    const body = { name: edit!.name, target: edit!.target, weekdays: edit!.weekdays, periods: edit!.periods };
    if (edit!.id) await put(`/api/schedules/${edit!.id}`, body); else await post("/api/schedules", body);
    setEdit(null); draft.reload();
  }, "Schedule saved (publish it on the Calendar tab)");

  if (edit) {
    const setP = (i: number, p: Partial<Period>) => setEdit({ ...edit, periods: edit.periods.map((x, j) => (j === i ? { ...x, ...p } : x)) });
    return (
      <Card title={edit.id ? `Edit “${edit.name}”` : "New schedule"} actions={<><button onClick={() => setEdit(null)}>Cancel</button><button className="primary" disabled={busy} onClick={save}>Save</button></>}>
        <div className="row">
          <Field label="Name"><input value={edit.name} onChange={(e) => setEdit({ ...edit, name: e.target.value })} /></Field>
          <Field label="Rings on">
            <select value={edit.target.all ? "all" : `g:${edit.target.groups?.[0] ?? ""}`} onChange={(e) => setEdit({ ...edit, target: e.target.value === "all" ? { all: true } : { groups: [Number(e.target.value.slice(2))] } })}>
              <option value="all">All endpoints</option>
              {(groups.data ?? []).map((g) => <option key={g.id} value={`g:${g.id}`}>Group: {g.name}</option>)}
            </select></Field>
        </div>
        <fieldset className="days"><legend>Normal weekdays for this schedule</legend>
          {DAYS.map((d, i) => <label key={d}><input type="checkbox" checked={edit.weekdays.includes(i + 1)}
            onChange={(e) => setEdit({ ...edit, weekdays: e.target.checked ? [...edit.weekdays, i + 1].sort() : edit.weekdays.filter((x) => x !== i + 1) })} /> {d}</label>)}
        </fieldset>
        <table className="edit">
          <thead><tr><th>Bell / period</th><th>Start</th><th>End</th><th>Warning (min before)</th><th>Sound</th><th>Seconds</th><th>Volume</th><th /></tr></thead>
          <tbody>{edit.periods.map((p, i) => (
            <tr key={i}>
              <td><input value={p.name} onChange={(e) => setP(i, { name: e.target.value })} aria-label="Period name" /></td>
              <td><input type="time" value={p.start_hm} onChange={(e) => setP(i, { start_hm: e.target.value })} aria-label="Start" /></td>
              <td><input type="time" value={p.end_hm} onChange={(e) => setP(i, { end_hm: e.target.value })} aria-label="End" /></td>
              <td><input type="number" min={0} max={30} value={p.warn_min} onChange={(e) => setP(i, { warn_min: Number(e.target.value) })} aria-label="Warning minutes" /></td>
              <td><select value={p.sound_id} onChange={(e) => setP(i, { sound_id: e.target.value })} aria-label="Sound">
                {(sounds.data ?? []).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></td>
              <td><input type="number" min={1} max={60} value={p.duration_s} onChange={(e) => setP(i, { duration_s: Number(e.target.value) })} aria-label="Duration" /></td>
              <td><select value={p.volume_policy} onChange={(e) => setP(i, { volume_policy: e.target.value })} aria-label="Volume policy">
                <option value="announcement">Announcement level (unmute)</option><option value="inherit">Leave PC volume alone</option></select></td>
              <td><button className="link" onClick={() => setEdit({ ...edit, periods: edit.periods.filter((_, j) => j !== i) })}>Remove</button></td>
            </tr>))}</tbody>
        </table>
        <button onClick={() => setEdit({ ...edit, periods: [...edit.periods, { ...edit.periods[edit.periods.length - 1] ?? blank.periods[0], name: `Period ${edit.periods.length + 1}` }] })}>+ Add period</button>
        <p className="dim small">Each period rings at its start and end; a warning bell rings the given minutes before the end. Editing a time moves the bell — it never creates a duplicate.</p>
      </Card>
    );
  }

  return (
    <Card title="Schedules (normal, late start, early release, exams…)" actions={canEdit && <button className="primary" onClick={() => setEdit(blank)}>+ New schedule</button>}>
      {(draft.data?.templates ?? []).length === 0 ? <Empty>No schedules yet.</Empty> : (
        <table><thead><tr><th>Name</th><th>Weekdays</th><th>Bells</th><th>Target</th><th /></tr></thead>
          <tbody>{draft.data.templates.map((t: Template) => (
            <tr key={t.id}><td><b>{t.name}</b></td><td>{t.weekdays.map((d) => DAYS[d - 1]).join(" ") || <span className="dim">overrides only</span>}</td>
              <td>{t.periods.map((p) => `${p.start_hm}`).join(", ")}</td><td>{t.target.all ? "All" : `Groups ${t.target.groups?.join(", ")}`}</td>
              <td>{canEdit && <><button onClick={() => setEdit(t)}>Edit</button> <button className="link" onClick={() => run(async () => { await del(`/api/schedules/${t.id}`); draft.reload(); }, "Deleted")}>Delete</button></>}</td></tr>))}
          </tbody></table>
      )}
    </Card>
  );
}

function Calendar() {
  const me = useMe();
  const draft = useData<any>("/api/schedules/draft");
  const preview = useData<any>(`/api/schedules/preview?from=${today()}&to=${plusDays(6)}`);
  const acceptance = useData<any>("/api/schedules/acceptance", 10000);
  const { busy, run } = useBusy();
  const [od, setOd] = useState({ date: plusDays(1), template_id: "" });
  const [hd, setHd] = useState({ date: plusDays(1), name: "" });
  const [pub, setPub] = useState<any>();
  const canEdit = me.role !== "viewer";
  const byDate: Record<string, any[]> = {};
  for (const o of [...(preview.data?.occurrences ?? [])].sort((a: any, b: any) => a.starts_at - b.starts_at)) (byDate[o.local_date] ??= []).push(o);
  const latest = acceptance.data?.latest_revision ?? 0;
  const accepted = (acceptance.data?.devices ?? []).filter((d: any) => d.accepted_revision >= latest && latest > 0).length;
  const needUpdate = (acceptance.data?.devices ?? []).filter((d: any) => d.needs_client_update).length;

  return (
    <div className="grid">
      <Card title="Publish to endpoints" className="span2" actions={canEdit && <button className="primary" disabled={busy}
        onClick={() => run(async () => { setPub(await post("/api/schedules/publish")); acceptance.reload(); }, "Published")}>Publish next 7 days</button>}>
        <p>Published revision <b>{latest || "none"}</b> · accepted by <b>{accepted}</b> of {(acceptance.data?.devices ?? []).length} endpoint(s).</p>
        {needUpdate > 0 && <p><b>{needUpdate}</b> endpoint(s) run a SchoolBell client too old to receive this schedule. Roll out the current client in Admin &gt; Windows client updates.</p>}
        {pub && <p className="dim">Revision {pub.revision}: {pub.occurrence_count} bell(s), sent to {pub.devices_notified} connected endpoint(s); others receive it when they reconnect.</p>}
        {acceptance.data?.renewal_error ? <p className="error">Automatic renewal needs attention: {acceptance.data.renewal_error}</p>
          : <p className="dim small">The published schedule renews automatically every day. Saved edits take effect when you publish.</p>}
        <p className="dim small">Endpoints ring from their own cached copy, so bells keep working through network or server outages for up to 7 days. An offline computer cannot learn about a cancellation — check “accepted revision” below.</p>
        <details><summary>Endpoints by accepted revision</summary>
          <table><tbody>{(acceptance.data?.devices ?? []).map((d: any) => (
            <tr key={d.device_id}><td>{d.label}</td><td>revision {d.accepted_revision}</td><td>{d.needs_client_update
              ? <Badge state="needs_client_update" title={`SchoolBell ${d.client_version ?? "(old version)"} on this computer cannot receive a schedule this large. Update it in Admin > Windows client updates.`} />
              : d.stale ? <Badge state="stale" /> : <Badge state="completed" title="up to date" />}</td><td className="dim">cache expires {fmtTime(d.cache_expires_at)}</td></tr>))}</tbody></table>
        </details>
      </Card>

      <Card title="Next 7 days (preview of what will be published)" className="span2">
        {preview.error ? <p className="error">{preview.error}</p> : Object.keys(byDate).length === 0 ? <Empty>No bells in the next 7 days.</Empty> : (
          <div className="days-grid">{Object.entries(byDate).map(([d, os]) => (
            <div key={d} className="day"><h3>{new Date(d + "T12:00:00").toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" })}</h3>
              <p className="dim small">{os[0]?.schedule_template}</p>
              <ul className="plain">{os.map((o) => <li key={o.occurrence_id}><b>{o.local_hm}</b> {o.bell_name}</li>)}</ul></div>))}
          </div>)}
        {(preview.data?.report?.skipped_nonexistent ?? []).length > 0 && <p className="warn-text">Daylight-saving: some local times do not exist and are skipped.</p>}
      </Card>

      <Card title="Date overrides (late start, early release…)">
        <ul className="plain">{(draft.data?.overrides ?? []).map((o: any) => (
          <li key={o.date}>{o.date} → {(draft.data.templates.find((t: any) => t.id === o.template_id) ?? { name: `#${o.template_id}` }).name}
            {canEdit && <button className="link" onClick={() => run(async () => { await del(`/api/calendar/overrides/${o.date}`); draft.reload(); preview.reload(); })}>remove</button>}</li>))}</ul>
        {canEdit && <div className="row"><input type="date" value={od.date} onChange={(e) => setOd({ ...od, date: e.target.value })} aria-label="Override date" />
          <select value={od.template_id} onChange={(e) => setOd({ ...od, template_id: e.target.value })} aria-label="Schedule for that date">
            <option value="">Choose schedule…</option>{(draft.data?.templates ?? []).map((t: any) => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
          <button disabled={!od.template_id} onClick={() => run(async () => { await post("/api/calendar/overrides", { date: od.date, template_id: Number(od.template_id) }); draft.reload(); preview.reload(); }, "Override added")}>Add</button></div>}
      </Card>

      <Card title="Holidays / no school">
        <ul className="plain">{(draft.data?.holidays ?? []).map((h: any) => (
          <li key={h.date}>{h.date} — {h.name}{canEdit && <button className="link" onClick={() => run(async () => { await del(`/api/calendar/holidays/${h.date}`); draft.reload(); preview.reload(); })}>remove</button>}</li>))}</ul>
        {canEdit && <div className="row"><input type="date" value={hd.date} onChange={(e) => setHd({ ...hd, date: e.target.value })} aria-label="Holiday date" />
          <input placeholder="Name" value={hd.name} onChange={(e) => setHd({ ...hd, name: e.target.value })} aria-label="Holiday name" />
          <button disabled={!hd.name} onClick={() => run(async () => { await post("/api/calendar/holidays", hd); draft.reload(); preview.reload(); }, "Holiday added")}>Add</button></div>}
      </Card>
    </div>
  );
}

function Sounds() {
  const me = useMe();
  const sounds = useData<any[]>("/api/sounds");
  const { busy, run } = useBusy();
  const [name, setName] = useState("");
  const canEdit = me.role !== "viewer";
  const send = (file: File, id?: string) => run(async () => {
    const f = new FormData(); f.append("file", file); if (name) f.append("name", name);
    await upload(id ? `/api/sounds/${id}` : "/api/sounds", f, id ? "PUT" : "POST");
    setName(""); sounds.reload();
  }, id ? "Sound replaced — publish the schedule so endpoints cache the new audio" : "Sound added");

  return (
    <Card title="Bell sounds" actions={canEdit && <><input placeholder="Name for a new sound" value={name} onChange={(e) => setName(e.target.value)} aria-label="New sound name" />
      <label className="file-btn primary">Upload new WAV…<input type="file" accept=".wav,audio/wav" disabled={busy} onChange={(e) => { const f = e.target.files?.[0]; if (f) send(f); e.target.value = ""; }} /></label></>}>
      <p className="dim small">WAV, 16- or 24-bit PCM, mono or stereo, up to 60 s. “Replace” keeps the same sound id, so every schedule using it (including the default <code>std-bell</code>) picks up the new audio at the next publish; “Ring now” uses it immediately.</p>
      <table><thead><tr><th>Sound</th><th>Length</th><th>Listen</th><th>Used by</th><th>Updated</th><th /></tr></thead>
        <tbody>{(sounds.data ?? []).map((s) => (
          <tr key={s.id}><td><b>{s.name}</b> <span className="dim">{s.id}</span>{s.builtin && <span className="dim"> · built-in</span>}{s.needs_publish && <> <Badge state="pending" title="publish the schedule to update endpoints" /></>}</td>
            <td>{s.duration_ms ? `${(s.duration_ms / 1000).toFixed(1)} s` : "—"}</td>
            <td><audio controls preload="none" src={`/${s.url}`} /></td>
            <td>{s.used_by_periods} period(s)</td><td className="dim">{s.updated_at ? `${fmtTime(s.updated_at)} by ${s.updated_by}` : "—"}</td>
            <td>{canEdit && <label className="file-btn">Replace…<input type="file" accept=".wav,audio/wav" onChange={(e) => { const f = e.target.files?.[0]; if (f) send(f, s.id); e.target.value = ""; }} /></label>}
              {me.role === "admin" && !s.builtin && <button className="link" onClick={() => run(async () => { await del(`/api/sounds/${s.id}`); sounds.reload(); }, "Deleted")}>Delete</button>}</td></tr>))}</tbody></table>
    </Card>
  );
}
