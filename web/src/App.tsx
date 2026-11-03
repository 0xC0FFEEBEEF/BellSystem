import { useCallback, useEffect, useState } from "react";
import { get, post, type Me } from "./api";
import { MeContext, ToastHost, useData, usePath } from "./ui";
import { Login } from "./pages/Login";
import { Dashboard } from "./pages/Dashboard";
import { Announce } from "./pages/Announce";
import { Bells } from "./pages/Bells";
import { Endpoints } from "./pages/Endpoints";
import { History } from "./pages/History";
import { Admin } from "./pages/Admin";

const NAV: [string, string, (m: Me) => boolean][] = [
  ["/", "Dashboard", () => true],
  ["/announce", "Announce", (m) => m.role !== "viewer"],
  ["/bells", "Bells", () => true],
  ["/endpoints", "Endpoints", () => true],
  ["/history", "History", () => true],
  ["/admin", "Admin", (m) => m.role === "admin"],
];

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined);
  const [path, nav] = usePath();
  const load = useCallback(() => get<Me>("/api/auth/me").then(setMe).catch(() => setMe(null)), []);
  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    const on = () => setMe(null);
    window.addEventListener("sb:unauthorized", on);
    return () => window.removeEventListener("sb:unauthorized", on);
  }, []);

  if (me === undefined) return <p className="loading">Loading…</p>;
  if (me === null) return <ToastHost><Login onDone={load} /></ToastHost>;

  const page = path === "/" ? <Dashboard nav={nav} />
    : path.startsWith("/announce") ? <Announce />
    : path.startsWith("/bells") ? <Bells />
    : path.startsWith("/endpoints") ? <Endpoints />
    : path.startsWith("/history") ? <History />
    : path.startsWith("/admin") ? <Admin />
    : <p>Not found. <a href="/" onClick={(e) => { e.preventDefault(); nav("/"); }}>Dashboard</a></p>;

  return (
    <MeContext.Provider value={me}>
      <ToastHost>
        <header className="top">
          <a className="brand" href="/" onClick={(e) => { e.preventDefault(); nav("/"); }}><img src="/favicon.svg" alt="" width={24} height={24} /> SchoolBell</a>
          <nav aria-label="Main">
            {NAV.filter(([, , show]) => show(me)).map(([p, label]) => (
              <a key={p} href={p} className={(p === "/" ? path === "/" : path.startsWith(p)) ? "on" : ""}
                onClick={(e) => { e.preventDefault(); nav(p); }}>{label}</a>))}
          </nav>
          <div className="who"><ServerTime /><span>{me.username} · {me.role}</span>
            <button className="link" onClick={() => post("/api/auth/logout").finally(() => setMe(null))}>Sign out</button></div>
        </header>
        <main className="page">{page}</main>
      </ToastHost>
    </MeContext.Provider>
  );
}

function ServerTime() {
  const s = useData<any>("/api/admin/settings");
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 15000); return () => clearInterval(t); }, []);
  const tz = s.data?.school_tz;
  if (!tz) return null;
  return <span className="dim" title={`School time (${tz})`}>{new Date(now).toLocaleTimeString([], { timeZone: tz, hour: "numeric", minute: "2-digit" })}</span>;
}
