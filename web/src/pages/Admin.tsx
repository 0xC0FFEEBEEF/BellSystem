import { useState } from "react";
import { api, del, fmtTime, get, post, put, upload, type Device } from "../api";
import { AudienceSelector, audienceTarget, audienceValid, Badge, Card, Empty, Field, useBusy, useData, useMe, type Audience } from "../ui";

const TABS = { users: "Users", groups: "Groups", enroll: "Enrollment", keepawake: "Keep awake", settings: "Settings", updates: "Updates", backup: "Backup", log: "Log", diag: "Diagnostics" } as const;
type Tab = keyof typeof TABS;

export function Admin() {
  const me = useMe();
  const [tab, setTab] = useState<Tab>("users");
  if (me.role !== "admin") return <Card title="Administration"><Empty>Administrator role required.</Empty></Card>;
  return (
    <div>
      <nav className="tabs" aria-label="Admin sections">
        {(Object.keys(TABS) as Tab[]).map((t) => <button key={t} className={tab === t ? "on" : ""} onClick={() => setTab(t)}>{TABS[t]}</button>)}
      </nav>
      {tab === "users" && <Users />}
      {tab === "groups" && <Groups />}
      {tab === "enroll" && <Enrollment />}
      {tab === "keepawake" && <KeepAwake />}
      {tab === "settings" && <Settings />}
      {tab === "updates" && <Updates />}
      {tab === "backup" && <Backup />}
      {tab === "log" && <ServerLog />}
      {tab === "diag" && <Diagnostics />}
    </div>
  );
}

function Users() {
  const users = useData<any[]>("/api/admin/users");
  const groups = useData<any[]>("/api/groups-list");
  const { busy, run } = useBusy();
  const [n, setN] = useState({ username: "", password: "", role: "operator", schoolwide_broadcast: false, group_scope: [] as number[] });
  const gname = (id: number) => groups.data?.find((g) => g.id === id)?.name ?? `#${id}`;
  return (
    <div className="grid">
      <Card title="Office accounts" className="span2">
        <table><thead><tr><th>User</th><th>Role</th><th>Group scope</th><th>Schoolwide</th><th /></tr></thead>
          <tbody>{(users.data ?? []).map((u) => (
            <tr key={u.id}><td><b>{u.username}</b></td>
              <td><select value={u.role} onChange={(e) => run(async () => { await put(`/api/admin/users/${u.id}`, { role: e.target.value }); users.reload(); }, "Role changed")}>
                <option value="admin">Administrator</option><option value="operator">Office operator</option><option value="viewer">Read-only viewer</option></select></td>
              <td>{u.group_scope.length ? u.group_scope.map(gname).join(", ") : <span className="dim">all groups</span>}</td>
              <td><input type="checkbox" checked={u.schoolwide_broadcast} aria-label="Schoolwide broadcast permission"
                onChange={(e) => run(async () => { await put(`/api/admin/users/${u.id}`, { schoolwide_broadcast: e.target.checked }); users.reload(); })} /></td>
              <td><button className="link" onClick={() => { const pw = prompt(`New password for ${u.username}`); if (pw) run(() => put(`/api/admin/users/${u.id}`, { password: pw }), "Password changed"); }}>Reset password</button>
                <button className="link" onClick={() => run(async () => { await del(`/api/admin/users/${u.id}`); users.reload(); }, "Deleted")}>Delete</button></td></tr>))}</tbody></table>
        <p className="dim small">Operators announce and edit schedules within their groups; “schoolwide” lets them address every endpoint. Viewers see status and history only. Rights are enforced by the server, not just this page.</p>
      </Card>
      <Card title="Add account">
        <Field label="Username"><input value={n.username} onChange={(e) => setN({ ...n, username: e.target.value })} /></Field>
        <Field label="Temporary password"><input type="password" value={n.password} onChange={(e) => setN({ ...n, password: e.target.value })} autoComplete="new-password" /></Field>
        <Field label="Role"><select value={n.role} onChange={(e) => setN({ ...n, role: e.target.value })}>
          <option value="operator">Office operator</option><option value="viewer">Read-only viewer</option><option value="admin">Administrator</option></select></Field>
        <label className="check"><input type="checkbox" checked={n.schoolwide_broadcast} onChange={(e) => setN({ ...n, schoolwide_broadcast: e.target.checked })} /> May address all endpoints</label>
        <fieldset><legend>Limit to groups (none = all)</legend>{(groups.data ?? []).map((g) => (
          <label key={g.id} className="check"><input type="checkbox" checked={n.group_scope.includes(g.id)}
            onChange={(e) => setN({ ...n, group_scope: e.target.checked ? [...n.group_scope, g.id] : n.group_scope.filter((x) => x !== g.id) })} /> {g.name}</label>))}</fieldset>
        <button className="primary" disabled={busy || !n.username || n.password.length < 1}
          onClick={() => run(async () => { await post("/api/admin/users", n); setN({ ...n, username: "", password: "" }); users.reload(); }, "Account created")}>Create</button>
      </Card>
    </div>
  );
}

function Groups() {
  const groups = useData<any[]>("/api/admin/groups");
  const devices = useData<{ items: Device[] }>("/api/devices?page_size=200");
  const [sel, setSel] = useState<number | null>(null);
  const members = useData<any[]>(sel ? `/api/admin/groups/${sel}/members` : null);
  const [name, setName] = useState("");
  const { run } = useBusy();
  const memberIds = new Set((members.data ?? []).map((m: any) => m.device_id ?? m));
  return (
    <div className="grid">
      <Card title="Groups">
        <ul className="plain">{(groups.data ?? []).map((g) => (
          <li key={g.id}><button className={sel === g.id ? "on" : "link"} onClick={() => setSel(g.id)}>{g.name}</button>
            <button className="link" onClick={() => run(async () => { await del(`/api/admin/groups/${g.id}`); if (sel === g.id) setSel(null); groups.reload(); }, "Group deleted")}>delete</button></li>))}</ul>
        <div className="row"><input placeholder="New group (e.g. Building A, Grade 3)" value={name} onChange={(e) => setName(e.target.value)} aria-label="New group name" />
          <button disabled={!name} onClick={() => run(async () => { await post("/api/admin/groups", { name }); setName(""); groups.reload(); }, "Group created")}>Add</button></div>
      </Card>
      <Card title={sel ? "Members" : "Select a group"}>
        {sel && (devices.data?.items ?? []).map((d) => (
          <label key={d.device_id} className="check"><input type="checkbox" checked={memberIds.has(d.device_id)} onChange={(e) => run(async () => {
            if (e.target.checked) await post(`/api/admin/groups/${sel}/members`, { device_id: d.device_id });
            else await del(`/api/admin/groups/${sel}/members/${d.device_id}`);
            members.reload();
          })} /> {d.label}</label>))}
        {sel && <p className="dim small">Membership is pushed to each endpoint so group bells ring from its offline cache.</p>}
      </Card>
    </div>
  );
}

function Enrollment() {
  const tokens = useData<any[]>("/api/admin/enrollment-tokens");
  const installer = useData<{ available: boolean; version?: string; detail?: string }>("/api/admin/enrollment-installer");
  const devices = useData<{ items: Device[] }>("/api/devices?page_size=200");
  const [f, setF] = useState({ label: "Deployment", max_uses: 50, expires_hours: 24 });
  const { busy, run } = useBusy();
  const download = (exe: boolean) => run(async () => {
    const csrf = document.cookie.match(/(?:^|;\s*)sb_csrf=([^;]+)/)?.[1] ?? "";
    const res = await fetch(exe ? "/api/admin/enrollment-installer" : "/api/admin/enrollment-file", { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF": decodeURIComponent(csrf) }, body: JSON.stringify(f), credentials: "same-origin" });
    if (!res.ok) throw new Error((await res.json()).detail);
    const blob = await res.blob();
    const a = document.createElement("a"); const url = URL.createObjectURL(blob); a.href = url; a.download = exe ? "SchoolBellSetup.exe" : "schoolbell-enroll.json"; a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
    tokens.reload();
  }, exe ? "Windows installer downloaded — keep it private" : "Enrollment file downloaded — keep it private");
  return (
    <div className="grid">
      <Card title="Windows deployment">
        <p className="dim small">Download one EXE with enrollment and the school certificate included. Each computer receives its own credential. The expiration and computer limit below apply to new installations; enrolled computers keep working.</p>
        <Field label="Label"><input value={f.label} onChange={(e) => setF({ ...f, label: e.target.value })} /></Field>
        <Field label="Computers it may enroll"><input type="number" min={1} value={f.max_uses} onChange={(e) => setF({ ...f, max_uses: Number(e.target.value) })} /></Field>
        <Field label="Valid for (hours)"><input type="number" min={1} max={336} value={f.expires_hours} onChange={(e) => setF({ ...f, expires_hours: Number(e.target.value) })} /></Field>
        <button className="primary" disabled={busy || !installer.data?.available} onClick={() => download(true)}>Download Windows Installer</button>
        {installer.data?.available && <p className="dim small">Windows client {installer.data.version}. Deploy only SchoolBellSetup.exe and run as an administrator.</p>}
        {installer.data && !installer.data.available && <p className="dim small">{installer.data.detail}</p>}
        <Field label="PDQ parameters"><pre className="code">/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /SERVER={location.host} /LOG="C:\Windows\Temp\SchoolBell-setup.log"</pre></Field>
        <p className="dim small">The server address is a parameter; it is not included in the EXE. Keep the installer private and download a fresh copy when enrollment expires or its computer limit is reached.</p>
        <details><summary>Separate enrollment file (advanced)</summary>
          <button disabled={busy} onClick={() => download(false)}>Download schoolbell-enroll.json</button>
          <p className="dim small">For the generic installer, supply this file with --enrollment-file and your CA certificate with --ca-file if required.</p>
        </details>
      </Card>
      <Card title="Enrollment tokens" className="span2">
        <p className="dim small">If computers are refused because an enrollment ran out or expired (Admin → Log says which), allow more
          computers or extend it: installers already deployed with it then work again. Revoked enrollments stay closed.</p>
        <table><thead><tr><th>Label</th><th>Used</th><th>Expires</th><th /></tr></thead>
          <tbody>{(tokens.data ?? []).map((t) => {
            const expired = !t.revoked && t.expires_at <= Date.now();
            const full = !t.revoked && t.used >= t.max_uses;
            const extend = (add_uses: number, extend_hours: number, msg: string) =>
              run(async () => { await post(`/api/admin/enrollment-tokens/${t.id}/extend`, { add_uses, extend_hours }); tokens.reload(); }, msg);
            return <tr key={t.id}><td>{t.label}</td>
              <td>{t.used} / {t.max_uses} {full && <Badge state="all_used" title="Every computer this enrollment allows has enrolled" />}</td>
              <td>{t.revoked ? <Badge state="revoked" /> : <>{fmtTime(t.expires_at)} {expired && <Badge state="expired" />}</>}</td>
              <td className="nowrap">{!t.revoked && <>
                <button className="link" disabled={busy} onClick={() => extend(5, 0, "5 more computers allowed")}>+5 computers</button>{" "}
                <button className="link" disabled={busy} onClick={() => extend(0, 24, "Extended 24 hours")}>+24 h</button>{" "}
                <button className="link" onClick={() => run(async () => { await del(`/api/admin/enrollment-tokens/${t.id}`); tokens.reload(); }, "Revoked")}>Revoke</button></>}</td></tr>;
          })}</tbody></table>
      </Card>
      <Card title="Revoke a computer" className="span2">
        <p className="dim small">A revoked computer can no longer connect or receive anything; re-enrolling needs a new enrollment file.</p>
        <table><tbody>{(devices.data?.items ?? []).filter((d) => !d.revoked).map((d) => <tr key={d.device_id}><td>{d.label}</td><td><Badge state={d.state} /></td>
          <td><button className="link" onClick={() => { if (confirm(`Revoke ${d.label}?`)) run(async () => { await post("/api/admin/devices/revoke", { device_id: d.device_id }); devices.reload(); }, "Revoked"); }}>Revoke</button></td></tr>)}</tbody></table>
      </Card>
    </div>
  );
}

function KeepAwake() {
  const policies = useData<any[]>("/api/admin/keepawake", 10000);
  const [aud, setAud] = useState<Audience>({ mode: "all", groups: [], devices: [] });
  const [minutes, setMinutes] = useState(60);
  const [until, setUntil] = useState("");
  const { busy, run } = useBusy();
  const create = () => run(async () => {
    const t = audienceTarget(aud);
    const scope = t.all ? "all" : t.groups ? "group" : "device";
    const ids = t.all ? [undefined] : (t.groups ?? t.devices ?? []).map(String);
    for (const id of ids)
      await post("/api/admin/keepawake", { scope, id, duration_minutes: until ? undefined : minutes, until_utc: until ? new Date(until).getTime() : undefined });
    policies.reload();
  }, "Keep-awake sent");
  return (
    <div className="grid">
      <Card title="Keep computers awake" className="span2">
        <p className="dim small">Stops automatic idle sleep until the time below, even if a computer goes offline. It does not stop someone choosing Sleep, closing a lid or shutting down, and it never unlocks a screen.</p>
        <AudienceSelector value={aud} onChange={setAud} allowAll />
        <div className="row">
          {[60, 120, 240].map((m) => <button key={m} className={minutes === m && !until ? "on" : ""} onClick={() => { setMinutes(m); setUntil(""); }}>{m / 60} hour{m > 60 ? "s" : ""}</button>)}
          <label className="field"><span>…or until</span><input type="datetime-local" value={until} onChange={(e) => setUntil(e.target.value)} /></label>
        </div>
        <button className="primary" disabled={busy || !audienceValid(aud)} onClick={create}>Keep awake</button>
      </Card>
      <Card title="Active policies" className="span2">
        {(policies.data ?? []).length === 0 ? <Empty>None.</Empty> : (
          <table><thead><tr><th>Scope</th><th>Until</th><th>Acknowledged</th><th /></tr></thead>
            <tbody>{policies.data!.map((p: any) => <tr key={p.policy_id}><td>{p.scope}{p.target_id ? ` ${p.target_id}` : ""}</td><td>{fmtTime(p.expires_at)}</td>
              <td>{p.acks ? `${p.acks.applied ?? 0} applied, ${p.acks.failed ?? 0} failed, ${p.acks.pending ?? 0} pending` : JSON.stringify(p.ack_counts ?? {})}</td>
              <td><button onClick={() => run(async () => { await del(`/api/admin/keepawake/${p.policy_id}`); policies.reload(); }, "Released")}>Release now</button></td></tr>)}</tbody></table>)}
      </Card>
    </div>
  );
}

function Settings() {
  const s = useData<any>("/api/admin/settings");
  const defaults = useData<any>("/api/admin/device-defaults");
  const { busy, run } = useBusy();
  const [tz, setTz] = useState<string>();
  const [name, setName] = useState<string>();
  const [vol, setVol] = useState<number>();
  const [cap, setCap] = useState<number>();
  const zones = (Intl as any).supportedValuesOf ? (Intl as any).supportedValuesOf("timeZone") as string[] : [];
  return (
    <div className="grid">
      <Card title="School">
        <Field label="School timezone" hint="Bells are edited in this local time and published as absolute times. Never guessed from this browser.">
          <select value={tz ?? s.data?.school_tz ?? ""} onChange={(e) => setTz(e.target.value)}>
            <option value="">— choose —</option>{zones.map((z) => <option key={z} value={z}>{z}</option>)}</select></Field>
        <Field label="Server name"><input value={name ?? s.data?.server_name ?? ""} onChange={(e) => setName(e.target.value)} /></Field>
        <button className="primary" disabled={busy} onClick={() => run(async () => { await put("/api/admin/settings", { school_tz: tz ?? s.data?.school_tz, server_name: name ?? s.data?.server_name }); s.reload(); }, "Saved")}>Save</button>
      </Card>
      <Card title="Default announcement volume (all endpoints)">
        <Field label={`Announcement level: ${vol ?? defaults.data?.announcement_volume ?? 80}%`}><input type="range" min={10} max={100} value={vol ?? defaults.data?.announcement_volume ?? 80} onChange={(e) => setVol(Number(e.target.value))} /></Field>
        <Field label={`Cap: ${cap ?? defaults.data?.volume_cap ?? 100}%`}><input type="range" min={10} max={100} value={cap ?? defaults.data?.volume_cap ?? 100} onChange={(e) => setCap(Number(e.target.value))} /></Field>
        <p className="dim small">During a bell or announcement the speaker and SchoolBell's own volume are unmuted and set to this level, then restored to exactly what the user had. Per-endpoint overrides are on the Endpoints page.</p>
        <button className="primary" disabled={busy} onClick={() => run(async () => { await put("/api/admin/device-defaults", { announcement_volume: vol, volume_cap: cap }); defaults.reload(); }, "Sent to all endpoints")}>Save</button>
      </Card>
    </div>
  );
}

function Updates() {
  const server = useData<any>("/api/admin/updates/server", 15000);
  const client = useData<any>("/api/admin/updates/client", 10000);
  const { busy, run } = useBusy();
  const [aud, setAud] = useState<Audience>({ mode: "all", groups: [], devices: [] });
  const [batch, setBatch] = useState(10);
  const [interval, setIntervalS] = useState(300);
  const [repo, setRepo] = useState<string>();
  const sv = server.data;
  const latestClient = client.data?.releases?.[0]?.version;
  const [ver, setVer] = useState<string>();
  return (
    <div className="grid">
      <Card title="Server software" className="span2" actions={<button disabled={busy} onClick={() => run(async () => { await post("/api/admin/updates/server/check"); server.reload(); })}>Check GitHub now</button>}>
        <p>Running <b>{sv?.current_version}</b> · source <code>{sv?.repo}</code> · last check {fmtTime(sv?.last_check_at)}</p>
        {!sv?.publisher_key && <p className="error">No publisher key is installed, so no update can be verified or applied.</p>}
        {sv?.last_error && <p className="warn-text">{sv.last_error}</p>}
        {sv?.latest && <p>Latest signed release: <b>{sv.latest.version}</b> ({fmtTime(sv.latest.created_at)}) — {sv.latest.newer ? <b>newer, available</b> : "you are up to date"}</p>}
        {sv?.latest?.newer && <button className="primary" disabled={busy || sv.request_pending} onClick={() => { if (confirm(`Install ${sv.latest.version}? SchoolBell restarts (about a minute); a backup is taken first and it rolls back automatically if the new version fails its health check.`)) run(async () => { await post("/api/admin/updates/server/apply", { version: sv.latest.version }); server.reload(); }, "Update requested"); }}>Install {sv.latest.version}</button>}
        {sv?.updater && <p className="dim">Updater: <Badge state={sv.updater.state ?? "unknown"} /> {sv.updater.message} {sv.updater.at ? fmtTime(sv.updater.at * (sv.updater.at < 1e12 ? 1000 : 1)) : ""}</p>}
        <details><summary>Automatic updates</summary>
          <label className="check"><input type="checkbox" checked={!!sv?.auto_update} onChange={(e) => run(async () => { await put("/api/admin/updates/server/config", { auto_update: e.target.checked }); server.reload(); })} /> Install verified updates automatically between {sv?.window} school time (never while a live announcement is running)</label>
          <div className="row"><input value={repo ?? sv?.repo ?? ""} onChange={(e) => setRepo(e.target.value)} aria-label="GitHub repository" />
            <button onClick={() => run(async () => { await put("/api/admin/updates/server/config", { repo }); server.reload(); }, "Saved")}>Set repository</button></div>
          <p className="dim small">Only releases signed with the SchoolBell publisher key are offered — a GitHub account alone cannot push code to the school.</p>
        </details>
      </Card>
      <Card title="Windows client updates" className="span2">
        <p>Computers by version: {(client.data?.device_versions ?? []).map((v: any) => <span key={v.version}> <b>{v.version}</b>: {v.devices}</span>)}</p>
        <p>Signed client releases on this server: {(client.data?.releases ?? []).map((r: any) => r.version).join(", ") || <span className="dim">none (they arrive with server updates, or upload one below)</span>}</p>
        {latestClient && <>
          <AudienceSelector value={aud} onChange={setAud} allowAll />
          <div className="row">
            <Field label="Version"><select value={ver ?? latestClient} onChange={(e) => setVer(e.target.value)}>{client.data.releases.map((r: any) => <option key={r.version}>{r.version}</option>)}</select></Field>
            <Field label="Computers per batch"><input type="number" min={1} value={batch} onChange={(e) => setBatch(Number(e.target.value))} /></Field>
            <Field label="Minutes between batches"><input type="number" min={0} value={interval / 60} onChange={(e) => setIntervalS(Number(e.target.value) * 60)} /></Field>
          </div>
          <button className="primary" disabled={busy || !audienceValid(aud)} onClick={() => run(async () => { await post("/api/admin/updates/client/rollout", { version: ver ?? latestClient, target: audienceTarget(aud), batch_size: batch, batch_interval_s: interval }); client.reload(); }, "Rollout started")}>Roll out</button>
          <p className="dim small">Each computer checks the publisher signature, waits until no bell or announcement is due, installs, and reinstalls its previous version automatically if the new one does not reconnect.</p></>}
        <h3>Rollouts</h3>
        {(client.data?.rollouts ?? []).length === 0 ? <Empty>None.</Empty> : <table><thead><tr><th>When</th><th>Version</th><th>Status</th><th>Installed</th><th>Failed</th><th /></tr></thead>
          <tbody>{client.data.rollouts.map((r: any) => <tr key={r.rollout_id}><td>{fmtTime(r.created_at)}</td><td>{r.version}</td><td>{r.status}</td><td>{r.installed}/{r.devices}</td><td>{r.failed}</td>
            <td>{r.status === "active" && <button className="link" onClick={() => run(async () => { await del(`/api/admin/updates/client/rollouts/${r.rollout_id}`); client.reload(); })}>Cancel</button>}</td></tr>)}</tbody></table>}
        <details><summary>Upload a signed client release</summary>
          <form onSubmit={(e) => { e.preventDefault(); const fd = new FormData(e.currentTarget); run(async () => { await upload("/api/admin/updates/client/upload", fd); client.reload(); }, "Release verified and stored"); }}>
            <div className="row"><label>Installer <input type="file" name="setup" accept=".exe" required /></label>
              <label>release-manifest.json <input type="file" name="manifest" required /></label>
              <label>release-manifest.json.sig <input type="file" name="signature" required /></label>
              <button className="primary">Verify and upload</button></div>
          </form></details>
      </Card>
    </div>
  );
}

function Backup() {
  const list = useData<any[]>("/api/admin/backups");
  const { busy, run } = useBusy();
  const [audio, setAudio] = useState(false);
  return (
    <div className="grid">
      <Card title="Backups" className="span2" actions={<><label className="check"><input type="checkbox" checked={audio} onChange={(e) => setAudio(e.target.checked)} /> include recordings</label>
        <button className="primary" disabled={busy} onClick={() => run(async () => { await api("POST", `/api/admin/backup?include_audio=${audio}`, {}); list.reload(); }, "Backup created")}>Back up now</button></>}>
        <p className="dim small">A consistent snapshot of the database, bell sounds and server keys. Copy backups off this server regularly.</p>
        <table><thead><tr><th>File</th><th>Size</th><th>Created</th><th /></tr></thead>
          <tbody>{(list.data ?? []).map((b) => <tr key={b.file}><td><a href={`/api/admin/backups/${b.file}`}>{b.file}</a></td><td>{(b.bytes / 1048576).toFixed(1)} MB</td><td>{fmtTime(b.created_at)}</td>
            <td><button className="link" onClick={() => { if (confirm(`Restore ${b.file}? SchoolBell restarts; the current database is kept as a pre-restore copy.`)) { const fd = new FormData(); fd.append("backup", b.file); run(() => upload("/api/admin/restore", fd), "Restoring — reload this page in a minute"); } }}>Restore</button></td></tr>)}</tbody></table>
      </Card>
      <Card title="Restore from a file">
        <form onSubmit={(e) => { e.preventDefault(); if (!confirm("Restore this backup? SchoolBell restarts.")) return; const fd = new FormData(e.currentTarget); run(() => upload("/api/admin/restore", fd), "Restoring — reload this page in a minute"); }}>
          <input type="file" name="file" accept=".tar.gz,.gz" required /> <button className="danger">Restore</button>
        </form>
      </Card>
    </div>
  );
}

function ServerLog() {
  const { busy, run } = useBusy();
  const [level, setLevel] = useState("");
  const [q, setQ] = useState("");
  const [days, setDays] = useState(1);
  const logs = useData<{ verbose: boolean; lines: string[] }>(
    `/api/admin/logs?lines=500${level ? `&level=${level}` : ""}${q ? `&q=${encodeURIComponent(q)}` : ""}`, 5000);
  const lines = logs.data?.lines ?? [];
  return (
    <div className="grid">
      <Card title="Server log" className="span2" actions={<>
        <label className="check"><input type="checkbox" checked={!!logs.data?.verbose} disabled={busy || !logs.data}
          onChange={(e) => { const on = e.target.checked; run(async () => { await post("/api/admin/logs/verbose", { on }); logs.reload(); }, on ? "Verbose logging on" : "Verbose logging off"); }} /> Verbose</label>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Days to download">
          <option value={1}>Today</option><option value={7}>Last 7 days</option><option value={14}>Last 14 days</option></select>
        <a className="file-btn" href={`/api/admin/logs.txt?days=${days}`} download>Download .txt</a></>}>
        <p className="dim small">Bells that a computer missed or could not play, computers connecting and disconnecting, publishing,
          updates and errors, newest first. Verbose adds every bell that played and other detail. Kept for 14 days.</p>
        <div className="row">
          <select value={level} onChange={(e) => setLevel(e.target.value)} aria-label="Level">
            <option value="">Everything</option><option value="warn">Warnings and errors</option></select>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter: computer, bell, user…" aria-label="Filter" />
        </div>
        {lines.length === 0 ? <Empty>No log lines{q || level ? " match" : " yet"}.</Empty>
          : <pre className="code log">{[...lines].reverse().join("\n")}</pre>}
      </Card>
    </div>
  );
}

function Diagnostics() {
  const d = useData<any>("/api/diagnostics", 5000);
  const t = useData<any>("/api/admin/transcription");
  const [raw, setRaw] = useState(false);
  return (
    <div className="grid">
      <Card title="Server diagnostics" className="span2" actions={<button onClick={() => setRaw(!raw)}>{raw ? "Summary" : "Raw"}</button>}>
        {raw ? <pre className="code">{JSON.stringify(d.data, null, 2)}</pre> : d.data && (
          <dl className="kv">{Object.entries(d.data).filter(([, v]) => typeof v !== "object").map(([k, v]) => <><dt key={k}>{k.replace(/_/g, " ")}</dt><dd>{String(v)}</dd></>)}</dl>)}
      </Card>
      <Card title="Transcription">
        <p>{t.data?.transcription_enabled ? "On" : "Off"} — {t.data?.note}</p>
        <p className="dim small">Queued jobs: {t.data?.jobs?.queued ?? 0}. Playback never waits for transcription.</p>
      </Card>
      <Card title="About"><p>Signed in to {location.host}. <a href="#" onClick={(e) => { e.preventDefault(); get("/api/health").then((h) => alert(`SchoolBell ${h.version}`)); }}>Version</a></p></Card>
    </div>
  );
}
