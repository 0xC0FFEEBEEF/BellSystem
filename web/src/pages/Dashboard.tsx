import { useEffect, useRef, useState } from "react";
import { post, type Device, type Target } from "../api";
import { LiveSession, type LiveStatus } from "../audio/live";
import { Card, Empty, Meter, useBusy, useData, useMe } from "../ui";

/** The main page: who, talk, ring, next bells. Everything else lives on the other pages. */
export function Dashboard() {
  const me = useMe();
  const canAct = me.role !== "viewer";
  const canAll = me.role === "admin" || me.schoolwide_broadcast;
  const groups = useData<{ id: number; name: string; member_count: number }[]>("/api/groups-list");
  const devices = useData<{ total: number; items: Device[] }>("/api/devices?page_size=200", 10000);
  const [who, setWho] = useState<string>(canAll ? "all" : "");
  const [picked, setPicked] = useState<string[]>([]);

  const items = devices.data?.items ?? [];
  const target: Target | null =
    who === "all" ? { all: true }
    : who.startsWith("g:") ? { groups: [Number(who.slice(2))] }
    : who === "pick" && picked.length > 0 ? { devices: picked }
    : null;
  const inTarget = who === "all" ? items
    : who.startsWith("g:") ? items.filter((d) => d.groups.includes(Number(who.slice(2))))
    : items.filter((d) => picked.includes(d.device_id));
  const online = inTarget.filter((d) => d.connected).length;

  return (
    <div className="home">
      {canAct && (
        <Card className="home-main">
          <label className="field">
            <span>Who</span>
            <select value={who} onChange={(e) => setWho(e.target.value)} aria-label="Who hears it">
              {canAll && <option value="all">Everyone</option>}
              {!canAll && <option value="">Choose a group…</option>}
              {(groups.data ?? []).map((g) => <option key={g.id} value={`g:${g.id}`}>{g.name}</option>)}
              <option value="pick">Choose computers…</option>
            </select>
          </label>
          {who === "pick" && (
            <div className="checklist scroll">
              {items.map((d) => (
                <label key={d.device_id}><input type="checkbox" checked={picked.includes(d.device_id)}
                  onChange={(e) => setPicked(e.target.checked ? [...picked, d.device_id] : picked.filter((x) => x !== d.device_id))} /> {d.label}</label>
              ))}
            </div>
          )}
          <p className="dim small">{target ? `${online} of ${inTarget.length} computer(s) connected` : "Pick who should hear it."}</p>
          <TalkButton target={target} />
          <RingButton target={target} />
        </Card>
      )}
      <NextBells />
    </div>
  );
}

function TalkButton({ target }: { target: Target | null }) {
  const session = useRef(new LiveSession());
  const [st, setSt] = useState<LiveStatus>(session.current.status);
  useEffect(() => {
    session.current.onChange = (s) => setSt({ ...s });
    const end = () => session.current.stop();
    window.addEventListener("beforeunload", end);
    return () => { window.removeEventListener("beforeunload", end); session.current.stop(); };
  }, []);
  const active = ["mic", "leasing", "preparing", "ready", "ending"].includes(st.phase);
  const begin = () => { if (target && !active) session.current.start(target, true); };
  const finish = () => session.current.stop();
  const label = st.phase === "ready" ? "Speak now — release to finish"
    : st.phase === "preparing" || st.phase === "leasing" || st.phase === "mic" ? "Getting ready…"
    : st.phase === "ending" ? "Finishing…" : "Hold to talk";
  return (
    <div>
      <button className={`talk ${st.phase}`} disabled={!target || st.phase === "ending"} aria-pressed={active}
        onPointerDown={(e) => { e.currentTarget.setPointerCapture(e.pointerId); begin(); }}
        onPointerUp={finish} onPointerCancel={finish}
        onKeyDown={(e) => { if ((e.key === " " || e.key === "Enter") && !e.repeat) { e.preventDefault(); begin(); } }}
        onKeyUp={(e) => { if (e.key === " " || e.key === "Enter") { e.preventDefault(); finish(); } }}>
        <span className="talk-main">🎙 {label}</span>
      </button>
      {active && <Meter level={st.level} />}
      {(st.phase === "error" || st.phase === "ended") && <p className={`status-line ${st.phase}`} role="status">{st.message}</p>}
    </div>
  );
}

function RingButton({ target }: { target: Target | null }) {
  const { busy, run } = useBusy();
  return (
    <button className="ring" disabled={!target || busy}
      onClick={() => run(() => post("/api/bells/ring", { target }), "Bell rung")}>🔔 Ring bell</button>
  );
}

function NextBells() {
  const from = new Date().toISOString().slice(0, 10);
  const to = new Date(Date.now() + 3 * 86400000).toISOString().slice(0, 10);
  const preview = useData<any>(`/api/schedules/preview?from=${from}&to=${to}`, 60000);
  const next = (preview.data?.occurrences ?? []).filter((o: any) => o.starts_at > Date.now())
    .sort((a: any, b: any) => a.starts_at - b.starts_at).slice(0, 8);
  const today = new Date().toDateString();
  return (
    <Card title="Next bells" className="home-bells">
      {preview.error ? <p className="dim">{preview.error}</p> : next.length === 0 ? <Empty>No bells scheduled.</Empty> : (
        <ul className="plain bells">{next.map((o: any) => {
          const d = new Date(o.starts_at);
          return <li key={o.occurrence_id}><b>{d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</b> {o.bell_name}
            {d.toDateString() !== today && <span className="dim"> · {d.toLocaleDateString([], { weekday: "short" })}</span>}</li>;
        })}</ul>
      )}
    </Card>
  );
}

/** Ring panel reused on the Bells and Endpoints pages. */
export function RingNow({ fixedDevice }: { fixedDevice?: string }) {
  const me = useMe();
  const sounds = useData<any[]>("/api/sounds");
  const groups = useData<{ id: number; name: string }[]>("/api/groups-list");
  const canAll = me.role === "admin" || me.schoolwide_broadcast;
  const [who, setWho] = useState(fixedDevice ? "device" : canAll ? "all" : "");
  const [sound, setSound] = useState("std-bell");
  const [name, setName] = useState("");
  const { busy, run } = useBusy();
  const target: Target | null = fixedDevice ? { devices: [fixedDevice] } : who === "all" ? { all: true } : who.startsWith("g:") ? { groups: [Number(who.slice(2))] } : null;
  return (
    <Card title={fixedDevice ? "Test this computer" : "Ring a bell"}>
      <div className="row">
        {!fixedDevice && <label className="field"><span>Who</span>
          <select value={who} onChange={(e) => setWho(e.target.value)}>
            {canAll && <option value="all">Everyone</option>}
            {!canAll && <option value="">Choose a group…</option>}
            {(groups.data ?? []).map((g) => <option key={g.id} value={`g:${g.id}`}>{g.name}</option>)}
          </select></label>}
        <label className="field"><span>Sound</span>
          <select value={sound} onChange={(e) => setSound(e.target.value)}>
            {(sounds.data ?? [{ id: "std-bell", name: "std-bell" }]).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select></label>
        {!fixedDevice && <label className="field"><span>Label (optional)</span><input value={name} placeholder="Bell" onChange={(e) => setName(e.target.value)} /></label>}
      </div>
      <button className="ring" disabled={busy || !target} onClick={() => run(() => post("/api/bells/ring", { target, sound_id: sound, bell_name: name || undefined }), "Bell rung")}>
        🔔 {fixedDevice ? "Play a test bell here" : "Ring bell"}</button>
    </Card>
  );
}
