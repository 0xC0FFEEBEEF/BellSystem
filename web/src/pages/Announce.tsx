import { useEffect, useRef, useState } from "react";
import { upload } from "../api";
import { LiveSession, Recorder, type LiveStatus } from "../audio/live";
import { listMics } from "../audio/mic";
import { encodeWav, fileTo16kMono } from "../audio/wav";
import { AudienceSelector, audienceTarget, audienceValid, Card, Counts, Field, Meter, useBusy, useMe, type Audience } from "../ui";

export function Announce() {
  const me = useMe();
  const canAll = me.role === "admin" || me.schoolwide_broadcast;
  const [aud, setAud] = useState<Audience>({ mode: canAll ? "all" : "groups", groups: [], devices: [] });
  const [mics, setMics] = useState<MediaDeviceInfo[]>([]);
  const [mic, setMic] = useState("");
  const [chime, setChime] = useState(true);
  const [latch, setLatch] = useState(false);
  const session = useRef(new LiveSession());
  const [st, setSt] = useState<LiveStatus>(session.current.status);

  useEffect(() => {
    session.current.onChange = (s) => setSt({ ...s });
    listMics().then(setMics);
    const end = () => { session.current.stop(); };
    window.addEventListener("beforeunload", end);
    return () => { window.removeEventListener("beforeunload", end); session.current.stop(); };
  }, []);

  const active = ["mic", "leasing", "preparing", "ready", "ending"].includes(st.phase);
  const begin = () => { if (audienceValid(aud) && !active) session.current.start(audienceTarget(aud), chime, mic || undefined); };
  const finish = () => { session.current.stop(); };

  // Space bar = hold to talk (when the talk button has focus or nothing editable does)
  useEffect(() => {
    const down = (e: KeyboardEvent) => {
      if (e.code !== "Space" || e.repeat || latch) return;
      const t = e.target as HTMLElement;
      if (t && ["INPUT", "TEXTAREA", "SELECT"].includes(t.tagName)) return;
      e.preventDefault(); begin();
    };
    const up = (e: KeyboardEvent) => { if (e.code === "Space" && !latch) { e.preventDefault(); finish(); } };
    window.addEventListener("keydown", down); window.addEventListener("keyup", up);
    return () => { window.removeEventListener("keydown", down); window.removeEventListener("keyup", up); };
  });

  const phaseText: Record<string, string> = {
    idle: latch ? "Click to start talking" : "Hold to talk (or hold Space)",
    mic: "Opening microphone…", leasing: "Requesting the talk lease…", preparing: "Preparing endpoints… wait",
    ready: latch ? "Speak now — click to finish" : "Speak now — release to finish", ending: "Finishing…",
    ended: latch ? "Click to talk again" : "Hold to talk again", error: "Try again",
  };

  return (
    <div className="grid">
      <Card title="1. Choose who hears it" className="span2">
        <AudienceSelector value={aud} onChange={setAud} allowAll={canAll} />
      </Card>

      <Card title="2. Live announcement (push-to-talk)" className="span2">
        <div className="row">
          <Field label="Microphone">
            <select value={mic} onChange={(e) => setMic(e.target.value)} disabled={active}>
              <option value="">Default microphone</option>
              {mics.map((m) => <option key={m.deviceId} value={m.deviceId}>{m.label || "Microphone"}</option>)}
            </select>
          </Field>
          <label className="check"><input type="checkbox" checked={chime} onChange={(e) => setChime(e.target.checked)} disabled={active} /> Play a chime first</label>
          <label className="check"><input type="checkbox" checked={latch} onChange={(e) => setLatch(e.target.checked)} disabled={active} /> Latch (click on / click off)</label>
        </div>
        <button
          className={`talk ${st.phase}`}
          disabled={!audienceValid(aud) || st.phase === "ending"}
          aria-pressed={active}
          onPointerDown={latch ? undefined : (e) => { e.currentTarget.setPointerCapture(e.pointerId); begin(); }}
          onPointerUp={latch ? undefined : finish}
          onPointerCancel={latch ? undefined : finish}
          onClick={latch ? () => (active ? finish() : begin()) : undefined}
        >
          <span className="talk-main">{phaseText[st.phase]}</span>
          <span className="talk-sub">{aud.mode === "all" ? "to everyone" : aud.mode === "groups" ? `to ${aud.groups.length} group(s)` : `to ${aud.devices.length} computer(s)`}</span>
        </button>
        <Meter level={st.level} />
        <p className={`status-line ${st.phase}`} role="status">{st.message || "Choose the audience, then hold the button. Your microphone opens only while you talk."}</p>
        {st.captureRate ? <p className="dim small">Capturing at {st.captureRate} Hz, sending 16 kHz mono in 20 ms frames · {st.framesSent} frames sent</p> : null}
        {st.counts && <p>Endpoints: <Counts counts={st.counts} /></p>}
        {st.phase === "preparing" && <button onClick={finish}>Cancel</button>}
      </Card>

      <RecordedAnnouncement aud={aud} mic={mic} chime={chime} />
    </div>
  );
}

function RecordedAnnouncement({ aud, mic, chime }: { aud: Audience; mic: string; chime: boolean }) {
  const rec = useRef(new Recorder());
  const [recording, setRecording] = useState(false);
  const [level, setLevel] = useState(0);
  const [pcm, setPcm] = useState<Int16Array>();
  const [url, setUrl] = useState<string>();
  const { busy, run } = useBusy();
  const [result, setResult] = useState<any>();

  useEffect(() => { rec.current.onLevel = setLevel; }, []);
  useEffect(() => () => { if (url) URL.revokeObjectURL(url); }, [url]);

  const setAudio = (s: Int16Array) => { setPcm(s); setUrl(URL.createObjectURL(encodeWav(s))); setResult(undefined); };
  const seconds = pcm ? (pcm.length / 16000).toFixed(1) : "0";

  return (
    <Card title="3. Record, preview, then send" className="span2">
      <p className="dim small">Not live: record (or upload a file), listen to it, and send it when it is right. Endpoints that are offline receive it when they reconnect, until the offer expires.</p>
      <div className="row">
        {!recording ? <button onClick={() => run(async () => { await rec.current.start(mic || undefined); setRecording(true); })}>● Record</button>
          : <button className="danger" onClick={() => { setAudio(rec.current.stop()); setRecording(false); setLevel(0); }}>■ Stop recording</button>}
        <label className="file-btn">Upload audio file…
          <input type="file" accept="audio/*,.wav,.mp3,.m4a,.ogg" onChange={(e) => {
            const f = e.target.files?.[0]; if (!f) return;
            run(async () => setAudio(await fileTo16kMono(f)));
            e.target.value = "";
          }} /></label>
      </div>
      {recording && <Meter level={level} />}
      {url && <div className="row"><audio controls src={url} /> <span className="dim">{seconds} s</span></div>}
      <button className="primary" disabled={!pcm || busy || !audienceValid(aud) || recording}
        onClick={() => run(async () => {
          const form = new FormData();
          form.append("file", encodeWav(pcm!), "announcement.wav");
          form.append("target", JSON.stringify(audienceTarget(aud)));
          form.append("chime", chime ? "1" : "0");
          setResult(await upload("/api/announcements/recorded", form));
        }, "Recorded announcement sent")}>Send recording</button>
      {result && <p>Sent to {result.device_count} endpoint(s) ({result.target_label}); results appear on the History page.</p>}
    </Card>
  );
}
