import { useState } from "react";
import { fmtAgo, fmtTime, put, type Device } from "../api";
import { Badge, Card, Empty, Field, useBusy, useData, useMe } from "../ui";
import { RingNow } from "./Dashboard";

export function Endpoints() {
  const [q, setQ] = useState("");
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | null>(null);
  const list = useData<{ total: number; page: number; page_size: number; items: Device[] }>(`/api/devices?q=${encodeURIComponent(q)}&page=${page}&page_size=50`, 5000);
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.page_size)) : 1;
  return (
    <div className="grid">
      <Card title={`Endpoints (${list.data?.total ?? "…"})`} className="span2"
        actions={<input type="search" placeholder="Search user, computer, room, group, device id" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search endpoints" />}>
        <table className="hover">
          <thead><tr><th>Endpoint</th><th>State</th><th>Speaker</th><th>Volume</th><th>Schedule</th><th>Version</th><th>Last contact</th></tr></thead>
          <tbody>{(list.data?.items ?? []).map((d) => <Row key={d.device_id} d={d} open={open === d.device_id} toggle={() => setOpen(open === d.device_id ? null : d.device_id)} />)}</tbody>
        </table>
        {list.data?.items.length === 0 && <Empty>No endpoints match.</Empty>}
        <div className="pager"><button disabled={page <= 1} onClick={() => setPage(page - 1)}>‹ Prev</button> page {page} of {pages} <button disabled={page >= pages} onClick={() => setPage(page + 1)}>Next ›</button></div>
      </Card>
    </div>
  );
}

function Row({ d, open, toggle }: { d: Device; open: boolean; toggle: () => void }) {
  const s = (d as any).status_report;
  return (
    <>
      <tr onClick={toggle} className={open ? "open" : ""} tabIndex={0} onKeyDown={(e) => e.key === "Enter" && toggle()}>
        <td><b>{d.label}</b>{d.revoked && <span className="dim"> (revoked)</span>}</td>
        <td><Badge state={d.state} /></td>
        <td className="dim">{s?.output_device ?? "—"}</td>
        <td>{s ? <>{s.master_muted ? "muted" : `${s.master_volume}%`}{s.session_muted ? " · app muted" : ""}</> : "—"}</td>
        <td>rev {d.accepted_revision}</td>
        <td className="dim">{(d as any).client_version ?? ""}</td>
        <td className="dim">{fmtAgo(d.last_seen)}</td>
      </tr>
      {open && <tr className="detail"><td colSpan={7}><Detail d={d} /></td></tr>}
    </>
  );
}

function Detail({ d }: { d: Device }) {
  const me = useMe();
  const settings = useData<any>(me.role === "admin" ? `/api/admin/devices/${d.device_id}/settings` : null);
  const st = (d as any).status_report;
  const upd = (d as any).update;
  return (
    <div className="grid detail-grid">
      <Card title="Status reported by the computer">
        {!st ? <Empty>No status report yet.</Empty> : (
          <dl className="kv">
            <dt>Computer</dt><dd>{st.computer_name}</dd>
            <dt>Signed-in user</dt><dd>{st.active_user ?? "No signed-in user"}</dd>
            <dt>Renderer</dt><dd>{st.renderer}{st.renderer_reason ? ` (${st.renderer_reason})` : ""} · played by {st.renderer_owner ?? "companion"}</dd>
            <dt>Speaker</dt><dd>{st.output_device ?? "none"} {st.output_present ? "" : <Badge state="missing_speaker" />}</dd>
            <dt>Master volume</dt><dd>{st.master_muted ? "muted" : `${st.master_volume}%`}</dd>
            <dt>SchoolBell app volume</dt><dd>{st.session_muted ? "muted" : `${st.session_volume}%`}</dd>
            <dt>Keep awake</dt><dd>{st.keep_awake?.desired ? `on until ${fmtTime(st.keep_awake.expires_at)} (${st.keep_awake.applied ? "applied" : "not applied"})` : "off"}</dd>
            <dt>Schedule cache</dt><dd>revision {st.cache_revision}, expires {fmtTime(st.cache_expires_at)}</dd>
            <dt>Clock offset</dt><dd>{st.clock_offset_ms} ms vs server</dd>
            <dt>Last playback</dt><dd>{st.last_playback ? <><Badge state={st.last_playback.state} /> {fmtTime(st.last_playback.at)}</> : "—"}</dd>
            <dt>Tray app</dt><dd>{st.companion}</dd>
            <dt>Client version</dt><dd>{(d as any).client_version ?? "?"}{upd ? <> · last update: <Badge state={upd.state} /> {upd.version} {upd.error ?? ""}</> : null}</dd>
            <dt>Device id</dt><dd><code>{d.device_id}</code></dd>
          </dl>)}
      </Card>
      {me.role !== "viewer" && <RingNow fixedDevice={d.device_id} />}
      {me.role === "admin" && settings.data && <Settings d={d} s={settings.data} outputs={st?.available_outputs ?? []} reload={settings.reload} />}
    </div>
  );
}

function Settings({ d, s, outputs, reload }: { d: Device; s: any; outputs: string[]; reload: () => void }) {
  const [out, setOut] = useState<string>(s.output_device ?? "");
  const [vol, setVol] = useState<number>(s.announcement_volume);
  const [cap, setCap] = useState<number>(s.volume_cap);
  const [room, setRoom] = useState<string>(s.room ?? "");
  const { busy, run } = useBusy();
  return (
    <Card title="Endpoint settings">
      <Field label="Classroom speaker" hint="“Windows default” follows the computer's default output. A named speaker that is unplugged is reported as missing — SchoolBell never silently plays somewhere else.">
        <select value={out} onChange={(e) => setOut(e.target.value)}>
          <option value="">Windows default output</option>
          {[...new Set([...(outputs ?? []), ...(s.output_device ? [s.output_device] : [])])].map((o) => <option key={o} value={o}>{o}</option>)}
        </select></Field>
      <Field label={`Announcement volume: ${vol}%`}><input type="range" min={10} max={100} value={vol} onChange={(e) => setVol(Number(e.target.value))} /></Field>
      <Field label={`Volume cap: ${cap}%`}><input type="range" min={10} max={100} value={cap} onChange={(e) => setCap(Number(e.target.value))} /></Field>
      <Field label="Room"><input value={room} onChange={(e) => setRoom(e.target.value)} placeholder="e.g. Room 204" /></Field>
      <button className="primary" disabled={busy} onClick={() => run(async () => {
        await put(`/api/admin/devices/${d.device_id}/settings`, { output_device: out || undefined, use_default_output: !out, announcement_volume: vol, volume_cap: cap, room: room || undefined });
        reload();
      }, "Settings sent to the endpoint")}>Save</button>
    </Card>
  );
}
