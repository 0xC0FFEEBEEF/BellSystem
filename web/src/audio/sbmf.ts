// SBMF frame encoder (PROTOCOL.md §7): "SBMF" | ver u8=1 | flags u8 | seq u32 | capture_ts u64 |
// len u16 | PCM s16le mono 16 kHz (320 samples / 20 ms). Little-endian throughout.
export function encodeFrame(seq: number, captureTsMs: number, pcm: Int16Array, last = false): ArrayBuffer {
  const buf = new ArrayBuffer(20 + pcm.byteLength);
  const v = new DataView(buf);
  v.setUint8(0, 0x53); v.setUint8(1, 0x42); v.setUint8(2, 0x4d); v.setUint8(3, 0x46);
  v.setUint8(4, 1);
  v.setUint8(5, last ? 1 : 0);
  v.setUint32(6, seq >>> 0, true);
  v.setBigUint64(10, BigInt(Math.floor(captureTsMs)), true);
  v.setUint16(18, pcm.byteLength, true);
  new Int16Array(buf, 20).set(pcm);
  return buf;
}
