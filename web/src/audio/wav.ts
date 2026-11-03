// 16 kHz mono 16-bit WAV encoding + browser-side decode/resample of any uploaded audio file.
export function encodeWav(samples: Int16Array, rate = 16000): Blob {
  const buf = new ArrayBuffer(44 + samples.byteLength);
  const v = new DataView(buf);
  const w = (o: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
  w(0, "RIFF"); v.setUint32(4, 36 + samples.byteLength, true); w(8, "WAVE");
  w(12, "fmt "); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  w(36, "data"); v.setUint32(40, samples.byteLength, true);
  new Int16Array(buf, 44).set(samples);
  return new Blob([buf], { type: "audio/wav" });
}

/** Decode any browser-playable audio file and convert it to 16 kHz mono PCM16. */
export async function fileTo16kMono(file: Blob): Promise<Int16Array> {
  const bytes = await file.arrayBuffer();
  const ctx = new AudioContext();
  const decoded = await ctx.decodeAudioData(bytes);
  await ctx.close();
  const frames = Math.ceil(decoded.duration * 16000);
  const off = new OfflineAudioContext(1, Math.max(1, frames), 16000);
  const src = off.createBufferSource();
  src.buffer = decoded;
  src.connect(off.destination);
  src.start();
  const rendered = await off.startRendering();
  const f = rendered.getChannelData(0);
  const out = new Int16Array(f.length);
  for (let i = 0; i < f.length; i++) { const s = Math.max(-1, Math.min(1, f[i])); out[i] = s < 0 ? s * 0x8000 : s * 0x7fff; }
  return out;
}
