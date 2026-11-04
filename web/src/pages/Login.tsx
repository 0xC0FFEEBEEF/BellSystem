import { useState } from "react";
import { post } from "../api";
import { Field } from "../ui";

export function Login({ onDone }: { onDone: () => void }) {
  const [mode, setMode] = useState<"login" | "setup">("login");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true); setError("");
    try {
      if (mode === "setup") await post("/api/auth/setup", { code: code.trim(), username, password });
      await post("/api/auth/login", { username, password });
      onDone();
    } catch (err: any) { setError(err.message); }
    finally { setBusy(false); }
  }

  return (
    <main className="login">
      <form onSubmit={submit} className="card">
        <h1><img src="/favicon.svg" alt="" width={32} height={32} /> SchoolBell</h1>
        <p className="dim">{mode === "login" ? "Office sign-in" : "First-time setup: create the administrator account"}</p>
        {mode === "setup" && <Field label="Setup code" hint="Printed once in the server log at first start (journalctl -u schoolbell | grep 'SETUP CODE').">
          <input value={code} onChange={(e) => setCode(e.target.value)} autoComplete="one-time-code" required /></Field>}
        <Field label="Username"><input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" required autoFocus /></Field>
        <Field label="Password" hint={undefined}>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete={mode === "setup" ? "new-password" : "current-password"} required /></Field>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="primary" disabled={busy}>{mode === "login" ? "Sign in" : "Create administrator and sign in"}</button>
        <button type="button" className="link" onClick={() => setMode(mode === "login" ? "setup" : "login")}>
          {mode === "login" ? "First-time setup" : "Back to sign-in"}</button>
      </form>
    </main>
  );
}
