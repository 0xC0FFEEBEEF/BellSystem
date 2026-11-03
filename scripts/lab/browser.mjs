// Headless Chrome driver for SchoolBell UI tests (raw Chrome DevTools Protocol, no npm deps).
//
//   node browser.mjs --base https://192.168.122.1:8443 --ca lab-ca.crt --user labadmin --pass-file pw.txt \
//        --scenario scenario.mjs [--mic speech.wav] [--width 1440] [--height 1000]
//
// TLS is validated normally: Chrome runs with an isolated HOME whose NSS database trusts only the
// given CA (the operator's own trust store is never touched; no certificate errors are ignored).
// --mic feeds a WAV file through Chrome's fake capture device, i.e. the real getUserMedia ->
// AudioWorklet -> WebSocket path runs with known synthetic speech as the "microphone".
// The scenario module exports `default async (page) => {}`; page has goto/eval/click/waitFor/shot.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith("--") ? [...a, [v.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? "1" : all[i + 1]]] : a), []));
const base = args.base, width = +(args.width ?? 1440), height = +(args.height ?? 1000);
const home = fs.mkdtempSync(path.join(process.env.TMPDIR ?? os.tmpdir(), "sb-chrome-"));
fs.mkdirSync(path.join(home, ".pki", "nssdb"), { recursive: true });
execFileSync("certutil", ["-d", `sql:${home}/.pki/nssdb`, "-N", "--empty-password"]);
if (args.ca) execFileSync("certutil", ["-d", `sql:${home}/.pki/nssdb`, "-A", "-t", "C,,", "-n", "schoolbell-lab-ca", "-i", args.ca]);

const port = 9600 + Math.floor(Math.random() * 300);
const flags = ["--headless=new", `--remote-debugging-port=${port}`, "--no-first-run", "--no-default-browser-check",
  `--user-data-dir=${home}/profile`, `--window-size=${width},${height}`, "--autoplay-policy=no-user-gesture-required",
  "--use-fake-ui-for-media-stream"];
if (args.mic) flags.push("--use-fake-device-for-media-stream", `--use-file-for-fake-audio-capture=${path.resolve(args.mic)}%noloop`);
const chrome = spawn("google-chrome-stable", [...flags, "about:blank"], { stdio: "ignore", env: { ...process.env, HOME: home } });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let wsUrl;
for (let i = 0; i < 60 && !wsUrl; i++) {
  await sleep(250);
  try { wsUrl = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((x) => x.type === "page").webSocketDebuggerUrl; } catch { }
}
const sock = new WebSocket(wsUrl);
await new Promise((r) => (sock.onopen = r));
let id = 0; const pending = {}; const consoleLines = [];
const send = (method, params = {}) => new Promise((r) => { const i = ++id; pending[i] = r; sock.send(JSON.stringify({ id: i, method, params })); });
sock.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending[m.id]) { pending[m.id](m.result ?? m); delete pending[m.id]; }
  if (m.method === "Runtime.consoleAPICalled") consoleLines.push(m.params.args.map((a) => a.value ?? a.description).join(" "));
  if (m.method === "Runtime.exceptionThrown") consoleLines.push("EXCEPTION " + JSON.stringify(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text));
};
await send("Page.enable"); await send("Runtime.enable");
await send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: width < 600 });

const page = {
  base, sleep, console: consoleLines,
  async eval(expr) {
    const r = await send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true, userGesture: true });
    if (r.exceptionDetails) throw new Error("eval failed: " + (r.exceptionDetails.exception?.description ?? r.exceptionDetails.text));
    return r.result?.value;
  },
  async goto(p) { await send("Page.navigate", { url: base + p }); await sleep(1500); },
  async waitFor(expr, ms = 15000) {
    const end = Date.now() + ms;
    while (Date.now() < end) { try { if (await page.eval(expr)) return true; } catch { } await sleep(200); }
    throw new Error("timed out waiting for: " + expr);
  },
  async click(selectorOrText) {
    const ok = await page.eval(`(() => { const s = ${JSON.stringify(selectorOrText)};
      let el = null; try { el = document.querySelector(s); } catch {}
      if (!el) el = [...document.querySelectorAll("button, a, label, summary")].find((e) => e.textContent.trim().startsWith(s));
      if (!el) return false; el.scrollIntoView({ block: "center" }); el.click(); return true; })()`);
    if (!ok) throw new Error("not found: " + selectorOrText);
    await sleep(300);
  },
  async shot(file) {
    const r = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true });
    fs.writeFileSync(file, Buffer.from(r.data, "base64"));
  },
  async login(user, pass) {
    await page.goto("/");
    const res = await page.eval(`(async () => {
      await fetch("/api/auth/csrf");
      const c = decodeURIComponent((document.cookie.match(/sb_csrf=([^;]+)/) || [])[1] || "");
      const r = await fetch("/api/auth/login", { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF": c }, body: JSON.stringify({ username: ${JSON.stringify(user)}, password: ${JSON.stringify(pass)} }) });
      return r.status; })()`);
    if (res !== 200) throw new Error("login failed: HTTP " + res);
    await page.goto("/");
  },
};

let code = 0;
try {
  if (args.user) await page.login(args.user, fs.readFileSync(args["pass-file"], "utf8").trim());
  const scenario = (await import(path.resolve(args.scenario))).default;
  await scenario(page);
} catch (e) {
  console.error("SCENARIO FAILED:", e.message);
  code = 1;
} finally {
  if (consoleLines.length) console.log("--- browser console ---\n" + consoleLines.slice(-30).join("\n"));
  sock.close(); chrome.kill();
  await sleep(300);
  fs.rmSync(home, { recursive: true, force: true });
  process.exit(code);
}
