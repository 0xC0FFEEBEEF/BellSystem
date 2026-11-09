import { useState } from "react";
import { fmtTime, post } from "../api";
import { Badge, Card, Counts, Empty, useBusy, useData, useMe } from "../ui";

export function History() {
  const [kind, setKind] = useState("");
  const hist = useData<any>(`/api/history?limit=50${kind ? `&kind=${kind}` : ""}`, 10000);
  const rings = useData<any[]>("/api/bells/rings?limit=20", 10000);
  const scheduled = useData<any[]>("/api/recent-bells?past=true&limit=60", 15000);
  const [openBell, setOpenBell] = useState<string | null>(null);
  const [openRing, setOpenRing] = useState<string | null>(null);
  const items = hist.data?.items ?? [];
  return (
    <div className="grid">
      <Card title="Announcements" className="span2" actions={
        <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Filter"><option value="">All</option><option value="live">Live</option><option value="recorded">Recorded</option></select>}>
        <p className="dim small">“Completed” is reported by the endpoint after playing. Socket delivery alone is never counted as heard.</p>
        {items.length === 0 ? <Empty>No announcements yet.</Empty> : items.map((a: any) => <Item key={a.announcement_id} a={a} reload={hist.reload} />)}
      </Card>
      <Card title="Scheduled bells" className="span2">
        <p className="dim small">Select a bell to see each computer's result. A bell more than 6 seconds late is recorded as expired, not rung late.</p>
        {(scheduled.data ?? []).length === 0 ? <Empty>None yet.</Empty> : (
          <table className="hover"><thead><tr><th>When</th><th>Bell</th><th>Schedule</th><th>Results</th></tr></thead>
            <tbody>{scheduled.data!.map((b) => {
              const rows = latestPerDevice(b.delivery ?? []);
              const counts = countStates(rows);
              const missing: string[] = b.no_report ?? [];
              const open = openBell === b.occurrence_id;
              return [
                <tr key={b.occurrence_id} className={open ? "open" : ""} onClick={() => setOpenBell(open ? null : b.occurrence_id)}>
                  <td>{fmtTime(b.starts_at)}</td><td>{b.bell_name}</td><td className="dim">{b.schedule_template}</td>
                  <td>{rows.length ? <Counts counts={counts} /> : null}{missing.length > 0 && <span className="dim"> {rows.length ? "· " : ""}{missing.length} no report</span>}
                    {!rows.length && !missing.length && <span className="dim">No computer reported it</span>}</td></tr>,
                open && <tr key={b.occurrence_id + "-d"} className="detail"><td colSpan={4}><ComputerResults rows={rows} noReport={missing} /></td></tr>,
              ];
            })}</tbody></table>)}
      </Card>
      <Card title="Manual bells" className="span2">
        {(rings.data ?? []).length === 0 ? <Empty>None.</Empty> : (
          <table className="hover"><thead><tr><th>When</th><th>Bell</th><th>By</th><th>Audience</th><th>Results</th></tr></thead>
            <tbody>{rings.data!.map((r) => {
              const open = openRing === r.ring_id;
              const rows = (r.devices ?? []).map((d: any) => ({ device_id: d.device_id, label: d.label, state: d.state, at: d.updated_at, error_code: d.error_code }));
              return [
                <tr key={r.ring_id} className={open ? "open" : ""} onClick={() => setOpenRing(open ? null : r.ring_id)}>
                  <td>{fmtTime(r.created_at)}</td><td>{r.bell_name}</td><td>{r.requested_by}</td><td>{r.target_label}</td><td><Counts counts={r.counts} /></td></tr>,
                open && <tr key={r.ring_id + "-d"} className="detail"><td colSpan={5}><ComputerResults rows={sortRows(rows)} /></td></tr>,
              ];
            })}</tbody></table>)}
      </Card>
    </div>
  );
}

type Row = { device_id: string; label: string; state: string; at?: number; error_code?: string | null };

/** Each computer's latest reported state (a bell reports started, then completed). */
function latestPerDevice(delivery: Row[]): Row[] {
  const latest = new Map<string, Row>();
  for (const d of delivery) {
    const cur = latest.get(d.device_id);
    if (!cur || (d.at ?? 0) > (cur.at ?? 0) || (d.at === cur.at && d.state !== "started")) latest.set(d.device_id, d);
  }
  return sortRows([...latest.values()]);
}

const ORDER = ["failed", "expired", "no_response", "unknown", "suppressed", "offered", "started", "ready", "completed"];
const sortRows = (rows: Row[]) => [...rows].sort((a, b) =>
  (ORDER.indexOf(a.state) + 1 || 99) - (ORDER.indexOf(b.state) + 1 || 99) || a.label.localeCompare(b.label));

function countStates(rows: Row[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const { state } of rows) counts[state] = (counts[state] ?? 0) + 1;
  return counts;
}

const REASONS: Record<string, string> = {
  missed_bell: "missed: the computer was off, asleep, offline or more than 6 s late",
  ring_expired: "reached the computer after the bell's time had passed",
  sound_missing: "the bell sound was not on this computer",
  sound_unreadable: "the bell sound file could not be read",
  output_missing: "no speaker found",
  renderer: "playback error",
  renderer_timeout: "playback did not finish in time",
  renderer_lost: "the tray app closed during playback",
  no_renderer: "nothing available to play audio",
  live_announcement: "a live announcement was playing",
  pre_login_audio_unverified: "no one has signed in since the computer started; may have been silent",
  ready_timeout: "did not get ready in time",
  offer_expired: "was offline",
};
function reasonText(code?: string | null): string {
  if (!code) return "";
  const retry = /^played_on_retry_(\d+)$/.exec(code);
  if (retry) return `played on retry ${retry[1]}`;
  return REASONS[code] ?? code.replace(/_/g, " ");
}
const fmtClock = (ms?: number) => ms ? new Date(ms).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" }) : "";

function ComputerResults({ rows, noReport }: { rows: Row[]; noReport?: string[] }) {
  return (
    <div>
      {rows.length > 0 && (
        <table><thead><tr><th>Computer</th><th>Result</th><th>Time</th><th>Detail</th></tr></thead>
          <tbody>{rows.map((r) => <tr key={r.device_id}><td>{r.label}</td><td><Badge state={r.state} /></td>
            <td className="dim">{fmtClock(r.at)}</td><td className="dim">{reasonText(r.error_code)}</td></tr>)}</tbody></table>)}
      {noReport && noReport.length > 0 && (
        <p className="small"><b>No report from {noReport.length} computer(s)</b> (off, or not connected since): {noReport.join(", ")}</p>)}
      {rows.length === 0 && !noReport?.length && <Empty>No results yet.</Empty>}
    </div>
  );
}

function Item({ a, reload }: { a: any; reload: () => void }) {
  const me = useMe();
  const [open, setOpen] = useState(false);
  const detail = useData<any>(open ? `/api/announcements/${a.announcement_id}` : null);
  const [text, setText] = useState<string | null>(null);
  const { busy, run } = useBusy();
  const t = detail.data?.transcript;
  const hasRecording = Boolean(a.recording?.audio_url || detail.data?.recording?.audio_url);
  return (
    <details className="history-item" onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
      <summary>
        <span>{fmtTime(a.started_at ?? a.created_at)}</span> <b>{a.kind === "live" ? "Live" : "Recorded"}</b> by {a.created_by} → {a.target_label}
        {a.recording?.duration_ms ? <span className="dim"> · {(a.recording.duration_ms / 1000).toFixed(1)} s</span> : null}
        {" "}<Counts counts={a.final_counts} /> <span className="dim">transcript: {a.transcript_status}</span>
      </summary>
      {a.recording?.audio_url && <audio controls preload="none" src={`/${a.recording.audio_url}`} />}
      <table><thead><tr><th>Endpoint</th><th>Result</th><th>Steps</th></tr></thead>
        <tbody>{(a.delivery ?? []).map((d: any) => <tr key={d.device_id}><td>{d.label}</td><td><Badge state={d.state} /> <span className="dim">{reasonText(d.error_code)}</span></td><td className="dim">{(d.states ?? []).join(" → ")}</td></tr>)}</tbody></table>
      {(t || hasRecording) && (
        <div className="transcript">
          <p><b>Transcript</b> ({t?.status ?? "none"}{t?.corrected_by ? `, corrected by ${t.corrected_by}` : ""})</p>
          {t?.transcript_original && t.corrected_by && <p className="dim">Automatic: {t.transcript_original}</p>}
          {text === null ? <p>{t?.transcript_current ?? <span className="dim">{t ? "The transcript is pending or unavailable." : "No transcript has been generated."}</span>}</p>
            : <textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} aria-label="Corrected transcript" />}
          {me.role !== "viewer" && (text === null
            ? <div className="row">{t && <button onClick={() => setText(t.transcript_current ?? "")}>Correct transcript</button>}
              <button disabled={busy || t?.status === "pending"} onClick={() => {
                if (!confirm("Redo this transcript from the stored recording? The current text stays visible until Whisper succeeds.")) return;
                run(async () => { await post(`/api/history/${a.announcement_id}/retranscribe`, {}); detail.reload(); reload(); }, "Transcript queued for Whisper");
              }}>Redo with Whisper</button></div>
            : <><button className="primary" disabled={busy} onClick={() => run(async () => { await post(`/api/history/${a.announcement_id}/correct`, { text }); setText(null); detail.reload(); reload(); }, "Correction saved (original kept)")}>Save correction</button> <button onClick={() => setText(null)}>Cancel</button></>)}
        </div>)}
    </details>
  );
}
