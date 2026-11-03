import { useState } from "react";
import { fmtTime, post } from "../api";
import { Badge, Card, Counts, Empty, useBusy, useData, useMe } from "../ui";

export function History() {
  const [kind, setKind] = useState("");
  const hist = useData<any>(`/api/history?limit=50${kind ? `&kind=${kind}` : ""}`, 10000);
  const rings = useData<any[]>("/api/bells/rings?limit=20", 10000);
  const items = hist.data?.items ?? [];
  return (
    <div className="grid">
      <Card title="Announcements" className="span2" actions={
        <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Filter"><option value="">All</option><option value="live">Live</option><option value="recorded">Recorded</option></select>}>
        <p className="dim small">“Completed” is reported by the endpoint after playing. Socket delivery alone is never counted as heard.</p>
        {items.length === 0 ? <Empty>No announcements yet.</Empty> : items.map((a: any) => <Item key={a.announcement_id} a={a} reload={hist.reload} />)}
      </Card>
      <Card title="Manual bells" className="span2">
        {(rings.data ?? []).length === 0 ? <Empty>None.</Empty> : (
          <table><thead><tr><th>When</th><th>Bell</th><th>By</th><th>Audience</th><th>Results</th></tr></thead>
            <tbody>{rings.data!.map((r) => <tr key={r.ring_id}><td>{fmtTime(r.created_at)}</td><td>{r.bell_name}</td><td>{r.requested_by}</td><td>{r.target_label}</td><td><Counts counts={r.counts} /></td></tr>)}</tbody></table>)}
      </Card>
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
