import { api, get, post, type Target } from "../api";
import { encodeFrame } from "./sbmf";
import { MicCapture } from "./mic";

export type LivePhase = "idle" | "mic" | "leasing" | "preparing" | "ready" | "ending" | "ended" | "error";

export type LiveStatus = {
  phase: LivePhase;
  message: string;
  announcementId?: string;
  deviceCount?: number;
  counts?: Record<string, number>;
  level: number;            // 0..1 mic level
  framesSent: number;
  captureRate?: number;
  readyAt?: number;
  error?: string;
};

/**
 * One live push-to-talk session (PROTOCOL.md §7): talk lease -> announcement offer -> bounded
 * preparation window (devices prepare output + volume and report ready) -> "Ready — speak" ->
 * SBMF frames over /ws/mic -> explicit end. Speech is only sent once the window has closed, so
 * the recording matches what classrooms heard. The lease is renewed while talking; closing the
 * tab closes the socket, which ends the announcement on the server.
 */
export class LiveSession {
  status: LiveStatus = { phase: "idle", message: "", level: 0, framesSent: 0 };
  onChange: (s: LiveStatus) => void = () => {};
  private mic = new MicCapture();
  private ws?: WebSocket;
  private leaseId?: string;
  private seq = 0;
  private renewTimer?: number;
  private pollTimer?: number;
  private sending = false;
  private closing = false;

  private set(p: Partial<LiveStatus>) {
    this.status = { ...this.status, ...p };
    this.onChange(this.status);
  }

  async start(target: Target, chime: boolean, micId?: string, maxDurationS = 300) {
    if (this.status.phase !== "idle" && this.status.phase !== "ended" && this.status.phase !== "error") return;
    this.seq = 0; this.sending = false; this.closing = false;
    this.set({ phase: "mic", message: "Opening the microphone…", framesSent: 0, error: undefined, counts: undefined, announcementId: undefined });
    try {
      this.mic.onFrame = (f) => this.onMicFrame(f.pcm, f.rms, f.at);
      await this.mic.start(micId);
      this.set({ captureRate: this.mic.captureRate });

      this.set({ phase: "leasing", message: "Requesting the talk lease…" });
      const lease = await post("/api/announcements/lease", { target, max_duration_s: maxDurationS });
      this.leaseId = lease.lease_id;
      const renewS = Math.max(5, Math.min(60, (lease.renew_interval_s ?? 60) / 2));
      this.renewTimer = window.setInterval(() => {
        if (this.leaseId) post(`/api/announcements/lease/${this.leaseId}/renew`).catch(() => {});
      }, renewS * 1000);

      const live = await post("/api/announcements/live", { target, chime, lease_id: this.leaseId });
      this.set({
        phase: "preparing", announcementId: live.announcement_id, deviceCount: live.device_count,
        readyAt: live.window_closes_at ?? Date.now() + 5000,
        message: `Preparing ${live.device_count} endpoint(s): selecting speakers, raising volume${chime ? ", chime" : ""}…`,
      });

      const proto = location.protocol === "https:" ? "wss" : "ws";
      this.ws = new WebSocket(`${proto}://${location.host}/ws/mic?lease_id=${encodeURIComponent(this.leaseId!)}`);
      this.ws.binaryType = "arraybuffer";
      this.ws.onclose = () => { if (!this.closing) this.fail("The connection to the server was lost — the announcement has ended and classroom volume is being restored."); };
      this.ws.onerror = () => {};
      await new Promise<void>((resolve, reject) => {
        this.ws!.onopen = () => resolve();
        setTimeout(() => reject(new Error("microphone channel did not open")), 8000);
      });

      this.pollTimer = window.setInterval(() => this.poll(), 700);
      // The server starts the stream as soon as every connected computer is ready (usually well
      // under a second); the preparation window is only the longest wait. Watch for that start.
      const id = live.announcement_id as string;
      const deadline = (live.window_closes_at ?? Date.now() + 5000) + 1500;
      let started = !!live.started_at;
      const phase = (): LivePhase => this.status.phase;   // this.set() changes it; TS cannot see that
      while (!started && phase() === "preparing" && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 150));
        try {
          const a = await get(`/api/announcements/${encodeURIComponent(id)}`);
          if (a.status !== "active") return;   // ended server-side: poll() reports it
          started = !!a.started_at;
        } catch { /* transient: keep waiting until the deadline */ }
      }
      if (phase() !== "preparing") return;
      this.sending = true;
      this.set({ phase: "ready", message: "Ready — speak now. Release (or press Stop) to finish." });
    } catch (e: any) {
      this.fail(e?.message ?? String(e));
    }
  }

  private onMicFrame(pcm: Int16Array, rms: number, at: number) {
    // test instrumentation (read by scripts/lab/gate-b.mjs): wall time of the first captured frame
    const w = window as any;
    if (!w.__sbFirstMicFrameAt) w.__sbFirstMicFrameAt = at;
    if (this.sending && !w.__sbFirstSentAt) { w.__sbFirstSentAt = at; w.__sbFirstSentSeq = this.seq + 1; }
    this.set({ level: Math.min(1, rms * 4) });
    if (!this.sending || !this.ws || this.ws.readyState !== WebSocket.OPEN) return;
    // backpressure: if the socket buffer grows (> ~1 s of audio), drop rather than queue a delayed backlog
    if (this.ws.bufferedAmount > 50 * 660) return;
    this.ws.send(encodeFrame(++this.seq, at, pcm));
    this.status.framesSent = this.seq;
  }

  private async poll() {
    try {
      const a = await get("/api/announcements/active");
      const mine = a.announcements?.find((x: any) => x.announcement_id === this.status.announcementId);
      if (mine) this.set({ counts: mine.counts });
      else if (this.status.phase === "ready" || this.status.phase === "preparing") {
        // ended server-side (lease expiry, max duration, administrator)
        this.cleanup();
        this.set({ phase: "ended", message: "The announcement was ended by the server (time limit or lease)." });
      }
    } catch { /* transient */ }
  }

  async stop() {
    if (!["preparing", "ready", "leasing", "mic"].includes(this.status.phase)) return;
    this.closing = true;
    this.set({ phase: "ending", message: "Finishing…" });
    try {
      if (this.ws?.readyState === WebSocket.OPEN && this.sending)
        this.ws.send(encodeFrame(++this.seq, Date.now(), new Int16Array(320), true));
      if (this.status.announcementId)
        await api("POST", `/api/announcements/live/end?announcement_id=${encodeURIComponent(this.status.announcementId)}`, {});
    } catch { /* the socket close still ends it */ }
    const id = this.status.announcementId;
    this.cleanup();
    let counts = this.status.counts;
    try {
      const h = await get(`/api/announcements/${id}`);
      counts = h.final_counts ?? h.counts ?? counts;
    } catch { /* keep last */ }
    this.set({ phase: "ended", message: "Announcement ended.", counts });
  }

  private fail(msg: string) {
    this.cleanup();
    this.set({ phase: "error", message: msg, error: msg });
  }

  private cleanup() {
    this.sending = false;
    this.closing = true;
    window.clearInterval(this.renewTimer);
    window.clearInterval(this.pollTimer);
    this.mic.stop();
    try { this.ws?.close(); } catch { /* ignore */ }
    this.ws = undefined;
    this.leaseId = undefined;
    this.set({ level: 0 });
  }
}

/** Record-then-send: capture to 16 kHz mono in the browser for preview before sending. */
export class Recorder {
  private mic = new MicCapture();
  private chunks: Int16Array[] = [];
  onLevel: (l: number) => void = () => {};
  startedAt = 0;

  async start(micId?: string) {
    this.chunks = [];
    this.mic.onFrame = (f) => { this.chunks.push(f.pcm); this.onLevel(Math.min(1, f.rms * 4)); };
    await this.mic.start(micId);
    this.startedAt = Date.now();
  }

  stop(): Int16Array {
    this.mic.stop();
    const total = this.chunks.reduce((n, c) => n + c.length, 0);
    const out = new Int16Array(total);
    let o = 0;
    for (const c of this.chunks) { out.set(c, o); o += c.length; }
    this.chunks = [];
    return out;
  }
}
