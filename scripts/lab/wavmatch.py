#!/usr/bin/env python3
"""Does the VM's captured audio contain the source clip? Normalized cross-correlation.

Usage: wavmatch.py captured.wav source.wav [--from-byte N] [--seconds S]
Resamples both to 16 kHz mono, searches the best alignment, prints the peak normalized
correlation (1.0 = identical waveform) and the offset of the match in the capture.
"""
import argparse, struct, sys
import numpy as np
from scipy.signal import resample_poly, correlate

def read(path, from_byte=0, seconds=None):
    data = open(path, "rb").read()
    pos, fmt, off = 12, None, None
    while pos + 8 <= len(data):
        cid, size = data[pos:pos+4], struct.unpack("<I", data[pos+4:pos+8])[0]
        if cid == b"fmt ": fmt = struct.unpack("<HHIIHH", data[pos+8:pos+24])
        if cid == b"data": off = pos + 8; break
        pos += 8 + size
    _, ch, rate, _, align, bits = fmt
    start = max(off, from_byte); start -= (start - off) % align
    raw = data[start:]
    if seconds: raw = raw[: int(seconds * rate) * align]
    x = np.frombuffer(raw[: len(raw) // align * align], dtype="<i2").astype(np.float64).reshape(-1, ch).mean(axis=1)
    if rate != 16000:
        g = np.gcd(rate, 16000); x = resample_poly(x, 16000 // g, rate // g)
    return x

ap = argparse.ArgumentParser()
ap.add_argument("captured"); ap.add_argument("source")
ap.add_argument("--from-byte", type=int, default=0); ap.add_argument("--seconds", type=float)
a = ap.parse_args()
cap = read(a.captured, a.from_byte, a.seconds); src = read(a.source)
if len(cap) < len(src):
    print(f"capture ({len(cap)/16000:.2f}s) is shorter than the source ({len(src)/16000:.2f}s)"); sys.exit(1)
c = correlate(cap, src, mode="valid", method="fft")
# normalize by the energy of each aligned window
win = np.convolve(cap ** 2, np.ones(len(src)), mode="valid")
norm = c / (np.sqrt(win * np.sum(src ** 2)) + 1e-9)
i = int(np.argmax(np.abs(norm)))
print(f"best match at {i/16000:.3f}s into the capture, normalized correlation {abs(norm[i]):.3f} "
      f"(source {len(src)/16000:.2f}s, capture {len(cap)/16000:.2f}s)")
sys.exit(0 if abs(norm[i]) > 0.6 else 2)
