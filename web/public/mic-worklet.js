// SchoolBell microphone capture (AudioWorklet). Runs at the browser's ACTUAL capture rate
// (`sampleRate`, usually 44100 or 48000 — never assumed to be 16 kHz), low-pass filters at
// ~7 kHz, resamples to 16 kHz mono and posts 20 ms frames (320 Int16 samples) plus an RMS level.
class SchoolBellMic extends AudioWorkletProcessor {
  constructor() {
    super();
    this.outRate = 16000;
    this.ratio = sampleRate / this.outRate;           // input samples per output sample
    // windowed-sinc low-pass FIR, cutoff 7 kHz (below the 8 kHz Nyquist of the output)
    const taps = 63, fc = 7000 / sampleRate, h = new Float32Array(taps);
    let sum = 0;
    for (let i = 0; i < taps; i++) {
      const m = i - (taps - 1) / 2;
      const sinc = m === 0 ? 2 * fc : Math.sin(2 * Math.PI * fc * m) / (Math.PI * m);
      const w = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / (taps - 1));
      h[i] = sinc * w; sum += h[i];
    }
    for (let i = 0; i < taps; i++) h[i] /= sum;
    this.h = h;
    this.hist = new Float32Array(taps);                // FIR history (input samples)
    this.histPos = 0;
    this.prev = 0;                                     // last filtered sample (for interpolation)
    this.pos = 0;                                      // fractional read position in input samples
    this.frame = new Int16Array(320);
    this.fill = 0;
    this.sumSq = 0;
    this.active = true;
    this.port.onmessage = (e) => { if (e.data === "stop") this.active = false; };
  }

  filter(x) {
    const h = this.h, n = h.length;
    this.hist[this.histPos] = x;
    let acc = 0, j = this.histPos;
    for (let i = 0; i < n; i++) {
      acc += h[i] * this.hist[j];
      j = j === 0 ? n - 1 : j - 1;
    }
    this.histPos = (this.histPos + 1) % n;
    return acc;
  }

  process(inputs) {
    if (!this.active) return false;
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const ch0 = input[0], ch1 = input.length > 1 ? input[1] : null;
    for (let i = 0; i < ch0.length; i++) {
      const x = ch1 ? (ch0[i] + ch1[i]) * 0.5 : ch0[i];
      const y = this.filter(x);
      // emit every output sample whose position falls between prev (pos-1) and this sample
      while (this.pos <= 1) {
        const v = this.prev + (y - this.prev) * this.pos;
        const s = Math.max(-1, Math.min(1, v));
        this.frame[this.fill++] = s < 0 ? s * 0x8000 : s * 0x7fff;
        this.sumSq += s * s;
        if (this.fill === 320) {
          const rms = Math.sqrt(this.sumSq / 320);
          const copy = this.frame.slice(0);
          this.port.postMessage({ pcm: copy.buffer, rms }, [copy.buffer]);
          this.fill = 0; this.sumSq = 0;
        }
        this.pos += this.ratio;
      }
      this.pos -= 1;
      this.prev = y;
    }
    return true;
  }
}
registerProcessor("schoolbell-mic", SchoolBellMic);
