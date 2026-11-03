#!/usr/bin/env python3
"""Record (host unix_ms, size) of the VM's captured audio file every 10 ms until killed or --seconds."""
import os, sys, time
path, out = sys.argv[1], sys.argv[2]
seconds = float(sys.argv[3]) if len(sys.argv) > 3 else 120
end = time.time() + seconds
last = -1
with open(out, "w") as f:
    while time.time() < end:
        s = os.path.getsize(path)
        if s != last:
            f.write(f"{int(time.time()*1000)} {s}\n"); f.flush(); last = s
        time.sleep(0.01)
