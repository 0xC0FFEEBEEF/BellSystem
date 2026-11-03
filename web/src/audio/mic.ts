// Office microphone capture: getUserMedia (secure context only) -> AudioWorklet resampler ->
// 16 kHz 20 ms frames. The microphone is opened only while the operator is talking or recording.
export type MicFrame = { pcm: Int16Array; rms: number; at: number };

export async function listMics(): Promise<MediaDeviceInfo[]> {
  try { return (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === "audioinput"); }
  catch { return []; }
}

export class MicCapture {
  private ctx?: AudioContext;
  private stream?: MediaStream;
  private node?: AudioWorkletNode;
  onFrame: (f: MicFrame) => void = () => {};
  captureRate = 0;

  async start(deviceId?: string) {
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia)
      throw new Error("The microphone needs the trusted HTTPS address of this server (browsers block microphones on plain http).");
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        deviceId: deviceId ? { exact: deviceId } : undefined,
        echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1,
      },
    });
    this.ctx = new AudioContext();               // runs at the device's real rate; we resample ourselves
    this.captureRate = this.ctx.sampleRate;
    await this.ctx.audioWorklet.addModule("/mic-worklet.js");
    const src = this.ctx.createMediaStreamSource(this.stream);
    this.node = new AudioWorkletNode(this.ctx, "schoolbell-mic", { numberOfInputs: 1, numberOfOutputs: 0 });
    this.node.port.onmessage = (e) => this.onFrame({ pcm: new Int16Array(e.data.pcm), rms: e.data.rms, at: Date.now() });
    src.connect(this.node);
    if (this.ctx.state === "suspended") await this.ctx.resume();
  }

  get trackLabel() { return this.stream?.getAudioTracks()[0]?.label ?? ""; }

  stop() {
    try { this.node?.port.postMessage("stop"); } catch { /* closed */ }
    this.node?.disconnect();
    this.stream?.getTracks().forEach((t) => t.stop());
    this.ctx?.close().catch(() => {});
    this.node = undefined; this.stream = undefined; this.ctx = undefined;
  }
}
