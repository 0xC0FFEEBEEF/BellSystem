"""Generate SchoolBell's built-in sound assets (stdlib only, 16-bit PCM mono).

Run once: python3 gen_sounds.py   -> sounds/{bell,chime,buzzer}.wav
"""
import math
import struct
import wave
from pathlib import Path

RATE = 22050
OUT = Path(__file__).resolve().parent / "sounds"


def write_wav(path: Path, samples):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(s)) for s in samples))


def bell(freq=830.0, dur=2.6):
    """Struck-bell tone: fundamental + inharmonic partials, exponential decay."""
    n = int(RATE * dur)
    out = []
    for i in range(n):
        t = i / RATE
        env = math.exp(-2.4 * t)
        v = (math.sin(2 * math.pi * freq * t) * 0.55
             + math.sin(2 * math.pi * freq * 1.27 * t) * 0.30
             + math.sin(2 * math.pi * freq * 2.13 * t) * 0.15)
        # short attack to avoid a click
        atk = min(1.0, t / 0.008)
        out.append(0.72 * env * atk * v * 32767)
    return out


def chime():
    """Three-note door chime: rising major arpeggio, overlapping decays."""
    notes = [659.25, 830.61, 987.77]  # E5, G#5, B5
    total = 3.2
    n = int(RATE * total)
    out = [0.0] * n
    for k, f in enumerate(notes):
        start = int(RATE * 0.45 * k)
        for i in range(start, n):
            t = (i - start) / RATE
            env = math.exp(-1.7 * t)
            atk = min(1.0, t / 0.01)
            out[i] += 0.30 * env * atk * (math.sin(2 * math.pi * f * t)
                                          + 0.25 * math.sin(2 * math.pi * f * 2 * t))
    return [min(0.8, max(-0.8, v)) * 32767 for v in out]


def buzzer(freq=460.0, dur=0.9):
    """Buzzer: square wave with a fast attack/decay so it doesn't click."""
    n = int(RATE * dur)
    out = []
    for i in range(n):
        t = i / RATE
        env = min(1.0, t / 0.01) * math.exp(-1.6 * max(0.0, t - 0.35))
        out.append(0.55 * env * (1.0 if math.sin(2 * math.pi * freq * t) >= 0 else -1.0) * 32767)
    return out


def main():
    OUT.mkdir(exist_ok=True)
    write_wav(OUT / "bell.wav", bell())
    write_wav(OUT / "chime.wav", chime())
    write_wav(OUT / "buzzer.wav", buzzer())
    for f in sorted(OUT.glob("*.wav")):
        print(f"{f.name}: {f.stat().st_size} bytes")


if __name__ == "__main__":
    main()
