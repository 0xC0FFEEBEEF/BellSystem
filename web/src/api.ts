// Thin client for the SchoolBell office API (cookie session + double-submit CSRF header).
export type Target = { all?: boolean; groups?: number[]; devices?: string[] };

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

function csrfCookie(): string {
  const m = document.cookie.match(/(?:^|;\s*)sb_csrf=([^;]+)/);
  return m ? decodeURIComponent(m[1]) : "";
}

async function ensureCsrf(): Promise<string> {
  let c = csrfCookie();
  if (!c) { await fetch("/api/auth/csrf", { credentials: "same-origin" }); c = csrfCookie(); }
  return c;
}

export async function api<T = any>(method: string, path: string, body?: unknown, form?: FormData): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== "GET") headers["X-CSRF"] = await ensureCsrf();
  let payload: BodyInit | undefined;
  if (form) payload = form;
  else if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const res = await fetch(path, { method, headers, body: payload, credentials: "same-origin" });
  const text = await res.text();
  let data: any = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { detail: text }; }
  if (!res.ok) {
    if (res.status === 401 && path !== "/api/auth/me" && !path.startsWith("/api/auth/")) window.dispatchEvent(new Event("sb:unauthorized"));
    throw new ApiError(res.status, data?.detail ?? `HTTP ${res.status}`);
  }
  return data as T;
}

export const get = <T = any>(p: string) => api<T>("GET", p);
export const post = <T = any>(p: string, b?: unknown) => api<T>("POST", p, b ?? {});
export const put = <T = any>(p: string, b?: unknown) => api<T>("PUT", p, b ?? {});
export const del = <T = any>(p: string) => api<T>("DELETE", p);
export const upload = <T = any>(p: string, form: FormData, method = "POST") => api<T>(method, p, undefined, form);

export type Me = { username: string; role: "admin" | "operator" | "viewer"; group_scope: number[]; schoolwide_broadcast: boolean };

export type Device = {
  device_id: string; name: string; label: string; status: string; revoked: boolean; first_seen: number;
  last_seen: number | null; accepted_revision: number; cache_revision: number; cache_expires_at: number | null;
  groups: number[]; connected: boolean; state: string; keep_awake: any; computer_name: string | null; active_user: string | null;
};

export const fmtTime = (ms?: number | null) =>
  ms ? new Date(ms).toLocaleString([], { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }) : "—";
export const fmtAgo = (ms?: number | null) => {
  if (!ms) return "never";
  const s = Math.round((Date.now() - ms) / 1000);
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
};
