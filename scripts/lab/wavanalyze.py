#!/usr/bin/env python3
"""Find audible segments in the VM's captured audio (QEMU 'file' audio backend).

Usage: wavanalyze.py out.wav [--from-byte N] [--threshold-db -45] [--sizelog sizes.log]
QEMU writes the header sizes only when it closes the file, so the data length is taken
from the file size. With --sizelog (lines "unix_ms size" from wavwatch.py) each segment
also gets the host wall-clock time at which those bytes were written.
"""
import argparse, bisect, math, struct, sys

def header(f):
    data = f.read(4096)
    assert data[:4] == b"RIFF" and data[8:12] == b"WAVE", "not a WAV file"
    pos = 12
    fmt = None
    while pos + 8 <= len(data):
        cid, size = data[pos:pos+4], struct.unpack("<I", data[pos+4:pos+8])[0]
        if cid == b"fmt ":
            fmt = struct.unpack("<HHIIHH", data[pos+8:pos+24])
        if cid == b"data":
            return fmt, pos + 8
        pos += 8 + size
    raise SystemExit("no data chunk")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("wav")
    ap.add_argument("--from-byte", type=int, default=0)
    ap.add_argument("--threshold-db", type=float, default=-45.0)
    ap.add_argument("--window-ms", type=int, default=20)
    ap.add_argument("--sizelog")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    with open(a.wav, "rb") as f:
        (tag, ch, rate, brate, align, bits), data_off = header(f)
        assert bits == 16, f"expected s16, got {bits} bits"
        start = max(data_off, a.from_byte)
        start -= (start - data_off) % align
        f.seek(start)
        raw = f.read()
    win = max(1, rate * a.window_ms // 1000) * align
    sizes = []
    if a.sizelog:
        for line in open(a.sizelog):
            t, s = line.split()
            sizes.append((int(s), int(t)))
        sizes.sort()
    keys = [s for s, _ in sizes]
    def wall(offset):
        if not sizes: return None
        i = bisect.bisect_left(keys, offset)
        if i == 0: return sizes[0][1]
        if i >= len(sizes): return sizes[-1][1]
        (s0, t0), (s1, t1) = sizes[i-1], sizes[i]
        return t0 + (t1 - t0) * (offset - s0) / max(1, s1 - s0)
    segs, cur = [], None
    peak_all = -120.0
    for i in range(0, len(raw) - win + 1, win):
        chunk = raw[i:i+win]
        n = len(chunk) // 2
        vals = struct.unpack("<%dh" % n, chunk[:n*2])
        rms = math.sqrt(sum(v*v for v in vals) / n) / 32768.0
        db = 20 * math.log10(rms) if rms > 0 else -120
        peak_all = max(peak_all, db)
        off = start + i
        if db >= a.threshold_db:
            if cur is None: cur = {"start_byte": off, "peak_db": db}
            cur["end_byte"] = off + win
            cur["peak_db"] = max(cur["peak_db"], db)
        elif cur is not None and off - cur["end_byte"] > rate * align * 0.3:
            segs.append(cur); cur = None
    if cur: segs.append(cur)
    bps = rate * align
    for s in segs:
        s["start_s"] = round((s["start_byte"] - data_off) / bps, 3)
        s["dur_s"] = round((s["end_byte"] - s["start_byte"]) / bps, 3)
        s["peak_db"] = round(s["peak_db"], 1)
        w = wall(s["start_byte"])
        if w is not None: s["wall_ms"] = int(w)
    if a.json:
        import json; print(json.dumps({"rate": rate, "channels": ch, "segments": segs}))
    else:
        print(f"format: {rate} Hz, {ch} ch, {bits}-bit; analysed {len(raw)/bps:.2f}s from byte {start}; loudest window {peak_all:.1f} dBFS")
        for s in segs:
            print(f"  audible at {s['start_s']:>9.3f}s for {s['dur_s']:.3f}s, peak {s['peak_db']} dBFS" +
                  (f", host wall {s['wall_ms']}" if 'wall_ms' in s else ""))
        if not segs: print("  no audible segments")

if __name__ == "__main__":
    main()
