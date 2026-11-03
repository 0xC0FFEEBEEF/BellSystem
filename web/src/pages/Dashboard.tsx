import { useState } from "react";
import { fmtAgo, fmtTime, post, type Device } from "../api";
import { AudienceSelector, audienceTarget, audienceValid, Badge, Card, Counts, Empty, useBusy, useData, useMe, type Audience } from "../ui";

const ORDER = ["ready_to_play", "playing", "muted", "connected", "missing_speaker", "no_renderer", "playback_failed", "stale", "offline", "revoked"];

export function Dashboard({ nav }: { nav: (p: string) => void }) {
  const me = useMe();
  const devices = useData<{ total: number; items: Device[] }>("/api/devices?page_size=200", 5000);
  const active = useData<any>("/api/announcements/active", 5000);
  const rings = useData<any[]>("/api/bells/rings?limit=5", 5000);
  const preview = useData<any>(`/api/schedules/preview?from=${new Date().toISOString().slice(0, 10)}&to=${new Date(Date.now() + 2 * 86400000).toISOString().slice(0, 10)}`, 60000);
  const items = devices.data?.items ?? [];
  const byState: Record<string, number> = {};
  for (const d of items) byState[d.state] = (byState[d.state] ?? 0) + 1;
  const problems = items.filter((d) => ["missing_speaker", "no_renderer", "playback_failed", "stale"].includes(d.state));
  const nextBells = (preview.data?.occurrences ?? []).filter((o: any) => o.starts_at > Date.now())
    .sort((a: any, b: any) => a.starts_at - b.starts_at).slice(0, 6);
  const live = (active.data?.announcements ?? []).filter((a: any) => a.kind === "live");

  return (
    <div className="grid">
      <Card title="Endpoints" className="span2" actions={<button onClick={() => nav("/endpoints")}>Open endpoints</button>}>
        <div className="tiles">
          <div className="tile"><b>{items.length}</b><span>enrolled</span></div>
          <div className="tile ok"><b>{items.filter((d) => d.connected).length}</b><span>connected</span></div>
          <div className="tile ok"><b>{(byState.ready_to_play ?? 0) + (byState.playing ?? 0)}</b><span>ready to play</span></div>
          <div className="tile warn"><b>{byState.muted ?? 0}</b><span>muted (auto-unmute)</span></div>
          <div className="tile bad"><b>{problems.length}</b><span>need attention</span></div>
          <div className="tile muted"><b>{byState.offline ?? 0}</b><span>offline</span></div>
        </div>
        <p className="dim small">Connected does not prove sound: “Ready” means a renderer and speaker are present. A completed result comes only from the endpoint itself.</p>
        <div className="state-row">{ORDER.filter((s) => byState[s]).map((s) => <span key={s}><Badge state={s} /> {byState[s]}</span>)}</div>
      </Card>

      {me.role !== "viewer" && <RingNow />}

      <Card title="Live now">
        {live.length === 0 ? <Empty>No live announcement.</Empty> : live.map((a: any) => (
          <p key={a.announcement_id}><b>{a.created_by}</b> → {a.target_label} · {a.elapsed_s}s · <Counts counts={a.counts} /></p>
        ))}
        {me.role !== "viewer" && <button className="primary" onClick={() => nav("/announce")}>Make an announcement</button>}
      </Card>

      <Card title="Next bells" actions={<button onClick={() => nav("/bells")}>Schedules</button>}>
        {preview.error ? <p className="dim">{preview.error}</p> : nextBells.length === 0 ? <Empty>No bells in the next two days.</Empty> : (
          <ul className="plain">{nextBells.map((o: any) => <li key={o.occurrence_id}><b>{o.local_hm}</b> {o.bell_name} <span className="dim">({o.local_date}, {o.schedule_template})</span></li>)}</ul>
        )}
      </Card>

      <Card title="Recent manual bells">
        {(rings.data ?? []).length === 0 ? <Empty>None yet.</Empty> : (
          <ul className="plain">{rings.data!.map((r) => <li key={r.ring_id}>{fmtTime(r.created_at)} · <b>{r.bell_name}</b> by {r.requested_by} → {r.target_label} <Counts counts={r.counts} /></li>)}</ul>
        )}
      </Card>

      {problems.length > 0 && (
        <Card title="Endpoints needing attention" className="span2">
          <table><tbody>{problems.slice(0, 12).map((d) => (
            <tr key={d.device_id}><td>{d.label}</td><td><Badge state={d.state} /></td><td className="dim">last contact {fmtAgo(d.last_seen)}</td></tr>
          ))}</tbody></table>
        </Card>
      )}
    </div>
  );
}

export function RingNow({ fixedDevice }: { fixedDevice?: string }) {
  const me = useMe();
  const sounds = useData<any[]>("/api/sounds");
  const [aud, setAud] = useState<Audience>({ mode: fixedDevice ? "devices" : me.role === "admin" || me.schoolwide_broadcast ? "all" : "groups", groups: [], devices: fixedDevice ? [fixedDevice] : [] });
  const [sound, setSound] = useState("std-bell");
  const [name, setName] = useState("");
  const { busy, run } = useBusy();
  const [last, setLast] = useState<any>();
  const ring = () => run(async () => {
    const r = await post("/api/bells/ring", { target: audienceTarget(aud), sound_id: sound, bell_name: name || undefined });
    setLast(r);
  }, "Bell sent");
  return (
    <Card title={fixedDevice ? "Test this endpoint" : "Ring a bell now"}>
      {!fixedDevice && <AudienceSelector value={aud} onChange={setAud} allowAll={me.role === "admin" || me.schoolwide_broadcast} />}
      <div className="row">
        <label className="field"><span>Sound</span>
          <select value={sound} onChange={(e) => setSound(e.target.value)}>
            {(sounds.data ?? [{ id: "std-bell", name: "std-bell" }]).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select></label>
        {!fixedDevice && <label className="field"><span>Label (optional)</span><input value={name} placeholder="Bell (rung by the office)" onChange={(e) => setName(e.target.value)} /></label>}
      </div>
      <button className={`primary big ${aud.mode === "all" ? "danger" : ""}`} disabled={busy || !audienceValid(aud)} onClick={ring}>
        🔔 {fixedDevice ? "Play test bell on this endpoint" : aud.mode === "all" ? "Ring bell everywhere" : "Ring bell"}</button>
      {last && <p className="dim small">Sent to {last.device_count} endpoint(s). It never rings later than {last.expires_in_s}s after this press; results appear below and on the History page.</p>}
    </Card>
  );
}
