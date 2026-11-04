import { useState } from "react";
import { fmtTime, post } from "../api";
import { Badge, Card, Counts, Empty, useBusy, useData, useMe } from "../ui";

export function History() {
  const [kind, setKind] = useState("");
  const hist = useData<any>(`/api/history?limit=50${kind ? `&kind=${kind}` : ""}`, 10000);
  const rings = useData<any[]>("/api/bells/rings?limit=20", 10000);
  const scheduled = useData<any[]>("/api/recent-bells?past=true&limit=60", 15000);
  const items = hist.data?.items ?? [];
  return (
    <div className="grid">
      <Card title="Announcements" className="span2" actions={
        <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Filter"><option value="">All</option><option value="live">Live</option><option value="recorded">Recorded</option></select>}>
        <p className="dim small">“Completed” is reported by the endpoint after playing. Socket delivery alone is never counted as heard.</p>
        {items.length === 0 ? <Empty>No announcements yet.</Empty> : items.map((a: any) => <Item key={a.announcement_id} a={a} reload={hist.reload} />)}
      </Card>
      <Card title="Scheduled bells" className="span2">
        {(scheduled.data ?? []).length === 0 ? <Empty>None yet.</Empty> : (
          <table><thead><tr><th>When</th><th>Bell</th><th>Schedule</th><th>Results</th></tr></thead>
            <tbody>{scheduled.data!.map((b) => {
              const counts = latestStates(b.delivery ?? []);
              return <tr key={b.occurrence_id}><td>{fmtTime(b.starts_at)}</td><td>{b.bell_name}</td><td className="dim">{b.schedule_template}</td>
                <td>{Object.keys(counts).length ? <Counts counts={counts} /> : <span className="dim">No computer reported it</span>}</td></tr>;
            })}</tbody></table>)}
      </Card>
      <Card title="Manual bells" className="span2">
        {(rings.data ?? []).length === 0 ? <Empty>None.</Empty> : (
          <table><thead><tr><th>When</th><th>Bell</th><th>By</th><th>Audience</th><th>Results</th></tr></thead>
            <tbody>{rings.data!.map((r) => <tr key={r.ring_id}><td>{fmtTime(r.created_at)}</td><td>{r.bell_name}</td><td>{r.requested_by}</td><td>{r.target_label}</td><td><Counts counts={r.counts} /></td></tr>)}</tbody></table>)}
      </Card>
    </div>
  );
}

/** Each computer's latest reported state (a bell reports started, then completed), counted by state. */
function latestStates(delivery: { device_id: string; state: string; at: number }[]): Record<string, number> {
  const latest = new Map<string, { state: string; at: number }>();
  for (const d of delivery) {
    const cur = latest.get(d.device_id);
    if (!cur || d.at > cur.at || (d.at === cur.at && d.state !== "started")) latest.set(d.device_id, d);
  }
  const counts: Record<string, number> = {};
  for (const { state } of latest.values()) counts[state] = (counts[state] ?? 0) + 1;
  return counts;
}

function Item({ a, reload }: { a: any; reload: () => void }) {
  const me = useMe();
  const [open, setOpen] = useState(false);
  const detail = useData<any>(open ? `/api/announcements/${a.announcement_id}` : null);
  const [text, setText] = useState<string | null>(null);
  const { busy, run } = useBusy();
  const t = detail.data?.transcript;
  return (
    <details className="history-item" onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
      <summary>
        <span>{fmtTime(a.started_at ?? a.created_at)}</span> <b>{a.kind === "live" ? "Live" : "Recorded"}</b> by {a.created_by} → {a.target_label}
        {a.recording?.duration_ms ? <span className="dim"> · {(a.recording.duration_ms / 1000).toFixed(1)} s</span> : null}
        {" "}<Counts counts={a.final_counts} /> <span className="dim">transcript: {a.transcript_status}</span>
      </summary>
      {a.recording?.audio_url && <audio controls preload="none" src={`/${a.recording.audio_url}`} />}
      <table><thead><tr><th>Endpoint</th><th>Result</th><th>Steps</th></tr></thead>
        <tbody>{(a.delivery ?? []).map((d: any) => <tr key={d.device_id}><td>{d.label}</td><td><Badge state={d.state} /> <span className="dim">{d.error_code ?? ""}</span></td><td className="dim">{(d.states ?? []).join(" → ")}</td></tr>)}</tbody></table>
      {t && (
        <div className="transcript">
          <p><b>Transcript</b> ({t.status}{t.corrected_by ? `, corrected by ${t.corrected_by}` : ""})</p>
          {t.original && t.corrected_by && <p className="dim">Automatic: {t.original}</p>}
          {text === null ? <p>{t.current ?? <span className="dim">No transcript text (transcription is off for this pilot).</span>}</p>
            : <textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} aria-label="Corrected transcript" />}
          {me.role !== "viewer" && (text === null
            ? <button onClick={() => setText(t.current ?? "")}>Correct transcript</button>
            : <><button className="primary" disabled={busy} onClick={() => run(async () => { await post(`/api/history/${a.announcement_id}/correct`, { text }); setText(null); detail.reload(); reload(); }, "Correction saved (original kept)")}>Save correction</button> <button onClick={() => setText(null)}>Cancel</button></>)}
        </div>)}
    </details>
  );
}
