import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { get, type Device, type Me, type Target } from "./api";

// ------------------------------------------------------------------ session + toasts

export const MeContext = createContext<Me | null>(null);
export const useMe = () => useContext(MeContext)!;

type Toast = { id: number; text: string; kind: "ok" | "err" };
const ToastCtx = createContext<(text: string, kind?: "ok" | "err") => void>(() => {});
export const useToast = () => useContext(ToastCtx);

export function ToastHost({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const push = useCallback((text: string, kind: "ok" | "err" = "ok") => {
    const id = Date.now() + Math.random();
    setToasts((t) => [...t, { id, text, kind }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), kind === "err" ? 8000 : 4000);
  }, []);
  return (
    <ToastCtx.Provider value={push}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {toasts.map((t) => <div key={t.id} className={`toast ${t.kind}`}>{t.text}</div>)}
      </div>
    </ToastCtx.Provider>
  );
}

// ------------------------------------------------------------------ data hooks

export function useData<T>(path: string | null, intervalMs = 0): { data: T | undefined; error?: string; reload: () => void } {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (!path) return;
    let alive = true;
    const load = () => get<T>(path).then((d) => { if (alive) { setData(d); setError(undefined); } })
      .catch((e) => { if (alive) setError(e.message); });
    load();
    const t = intervalMs ? window.setInterval(load, intervalMs) : 0;
    return () => { alive = false; if (t) clearInterval(t); };
  }, [path, intervalMs, tick]);
  return { data, error, reload: () => setTick((x) => x + 1) };
}

export function useBusy() {
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const run = useCallback(async (fn: () => Promise<unknown>, ok?: string) => {
    setBusy(true);
    try { await fn(); if (ok) toast(ok); }
    catch (e: any) { toast(e?.message ?? String(e), "err"); }
    finally { setBusy(false); }
  }, [toast]);
  return { busy, run };
}

// ------------------------------------------------------------------ small components

const STATE_TEXT: Record<string, [string, string]> = {
  ready_to_play: ["Ready", "ok"], playing: ["Playing", "ok"], connected: ["Connected", "info"],
  muted: ["Muted (unmutes for announcements)", "warn"], missing_speaker: ["Missing speaker", "bad"],
  no_renderer: ["No renderer", "bad"], stale: ["Stale", "warn"], offline: ["Offline", "muted"],
  playback_failed: ["Playback failed", "bad"], revoked: ["Revoked", "muted"],
  completed: ["Completed", "ok"], started: ["Started", "info"], ready: ["Ready", "info"], offered: ["Offered", "muted"],
  failed: ["Failed", "bad"], expired: ["Expired", "warn"], suppressed: ["Suppressed", "warn"], unknown: ["Unknown", "warn"],
  no_response: ["No response", "bad"], installed: ["Installed", "ok"], pending: ["Pending", "muted"],
  rolled_back: ["Rolled back", "bad"], needs_client_update: ["Needs client update", "bad"], downloading: ["Downloading", "info"], installing: ["Installing", "info"],
};

export function Badge({ state, title }: { state: string; title?: string }) {
  const [text, kind] = STATE_TEXT[state] ?? [state.replace(/_/g, " "), "muted"];
  return <span className={`badge ${kind}`} title={title ?? state}>{text}</span>;
}

export function Counts({ counts }: { counts?: Record<string, number> }) {
  if (!counts || Object.keys(counts).length === 0) return <span className="dim">—</span>;
  return <span className="counts">{Object.entries(counts).map(([k, v]) => <span key={k}><Badge state={k} /> {v}</span>)}</span>;
}

export function Card({ title, actions, children, className }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`card ${className ?? ""}`}>
      {(title || actions) && <header className="card-head"><h2>{title}</h2><div className="actions">{actions}</div></header>}
      {children}
    </section>
  );
}

export function Meter({ level }: { level: number }) {
  return <div className="meter" role="meter" aria-label="Microphone level" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(level * 100)}>
    <div style={{ width: `${Math.round(level * 100)}%` }} />
  </div>;
}

export function Empty({ children }: { children: ReactNode }) { return <p className="empty">{children}</p>; }

export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <label className="field"><span>{label}</span>{children}{hint && <small>{hint}</small>}</label>;
}

// ------------------------------------------------------------------ audience selector

export type Audience = { mode: "all" | "groups" | "devices"; groups: number[]; devices: string[] };

export const audienceTarget = (a: Audience): Target =>
  a.mode === "all" ? { all: true } : a.mode === "groups" ? { groups: a.groups } : { devices: a.devices };

export const audienceValid = (a: Audience) =>
  a.mode === "all" || (a.mode === "groups" && a.groups.length > 0) || (a.mode === "devices" && a.devices.length > 0);

/** Audience is chosen explicitly before the microphone opens; schoolwide looks different. */
export function AudienceSelector({ value, onChange, allowAll }: { value: Audience; onChange: (a: Audience) => void; allowAll: boolean }) {
  const groups = useData<{ id: number; name: string; member_count?: number }[]>("/api/groups-list");
  const [q, setQ] = useState("");
  const devices = useData<{ total: number; items: Device[] }>(`/api/devices?page_size=200&q=${encodeURIComponent(q)}`, 15000);
  const all = useData<{ total: number; items: Device[] }>("/api/devices?page_size=200", 15000);
  const items = all.data?.items ?? [];
  const reachable = (ds: Device[]) => ds.filter((d) => d.connected).length;

  let selected: Device[] = [];
  if (value.mode === "all") selected = items;
  else if (value.mode === "groups") selected = items.filter((d) => d.groups.some((g) => value.groups.includes(g)));
  else selected = items.filter((d) => value.devices.includes(d.device_id));

  return (
    <div className="audience">
      <div className="seg" role="radiogroup" aria-label="Audience">
        {allowAll && <button type="button" role="radio" aria-checked={value.mode === "all"} className={value.mode === "all" ? "on" : ""} onClick={() => onChange({ ...value, mode: "all" })}>Everyone</button>}
        <button type="button" role="radio" aria-checked={value.mode === "groups"} className={value.mode === "groups" ? "on" : ""} onClick={() => onChange({ ...value, mode: "groups" })}>Groups</button>
        <button type="button" role="radio" aria-checked={value.mode === "devices"} className={value.mode === "devices" ? "on" : ""} onClick={() => onChange({ ...value, mode: "devices" })}>Selected endpoints</button>
      </div>
      {value.mode === "groups" && (
        <div className="checklist">
          {(groups.data ?? []).length === 0 && <span className="dim">No groups yet (create them in Admin → Groups).</span>}
          {(groups.data ?? []).map((g) => (
            <label key={g.id}><input type="checkbox" checked={value.groups.includes(g.id)}
              onChange={(e) => onChange({ ...value, groups: e.target.checked ? [...value.groups, g.id] : value.groups.filter((x) => x !== g.id) })} /> {g.name}</label>
          ))}
        </div>
      )}
      {value.mode === "devices" && (
        <div>
          <input type="search" placeholder="Search user, computer, room, group…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search endpoints" />
          <div className="checklist scroll">
            {(devices.data?.items ?? []).map((d) => (
              <label key={d.device_id}><input type="checkbox" checked={value.devices.includes(d.device_id)}
                onChange={(e) => onChange({ ...value, devices: e.target.checked ? [...value.devices, d.device_id] : value.devices.filter((x) => x !== d.device_id) })} />
                {" "}{d.label} <Badge state={d.state} /></label>
            ))}
          </div>
        </div>
      )}
      <p className="audience-count"><strong>{selected.length}</strong> endpoint(s) selected · <strong>{reachable(selected)}</strong> connected now
        {selected.length - reachable(selected) > 0 && <> · {selected.length - reachable(selected)} not connected</>}</p>
    </div>
  );
}

// ------------------------------------------------------------------ tiny router

export function usePath(): [string, (p: string) => void] {
  const [path, setPath] = useState(location.pathname);
  useEffect(() => {
    const on = () => setPath(location.pathname);
    window.addEventListener("popstate", on);
    return () => window.removeEventListener("popstate", on);
  }, []);
  const nav = useCallback((p: string) => { history.pushState(null, "", p); setPath(p); window.scrollTo(0, 0); }, []);
  return [path, nav];
}

export function useInterval(fn: () => void, ms: number) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => { const t = setInterval(() => ref.current(), ms); return () => clearInterval(t); }, [ms]);
}
