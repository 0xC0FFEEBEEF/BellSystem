#!/usr/bin/env python3
"""Run a PowerShell script inside a libvirt Windows VM through qemu-guest-agent.

Usage: vmexec.py [--domain NAME] [--timeout S] (-c 'powershell code' | -f script.ps1)
Runs as LocalSystem (the guest agent's account), session 0. Prints stdout/stderr and
exits with the guest exit code. Lab tooling only - never used on school machines.
"""
import argparse, base64, json, subprocess, sys, time

def agent(domain, cmd, timeout=60):
    out = subprocess.run(["virsh", "-c", "qemu:///system", "qemu-agent-command", domain,
                          json.dumps(cmd), "--timeout", str(timeout)],
                         capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit(f"agent error: {out.stderr.strip()}")
    return json.loads(out.stdout)["return"]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--domain", default="schoolbell-lab")
    ap.add_argument("--timeout", type=int, default=300)
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("-c", dest="code")
    g.add_argument("-f", dest="file")
    a = ap.parse_args()
    code = a.code if a.code is not None else open(a.file, encoding="utf-8").read()
    # -EncodedCommand takes UTF-16LE base64; avoids every quoting problem.
    # wrap in a script block so a leading param() block stays valid
    wrapped = "$ProgressPreference='SilentlyContinue'; & {\n" + code + "\n}"
    enc = base64.b64encode(wrapped.encode("utf-16-le")).decode()
    pid = agent(a.domain, {"execute": "guest-exec", "arguments": {
        "path": "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
        "arg": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", enc],
        "capture-output": True}})["pid"]
    deadline = time.time() + a.timeout
    while True:
        st = agent(a.domain, {"execute": "guest-exec-status", "arguments": {"pid": pid}})
        if st.get("exited"):
            break
        if time.time() > deadline:
            print(f"timeout after {a.timeout}s (pid {pid} still running)", file=sys.stderr)
            sys.exit(124)
        time.sleep(0.5)
    for key, stream in (("out-data", sys.stdout), ("err-data", sys.stderr)):
        if key in st:
            stream.write(base64.b64decode(st[key]).decode("utf-8", "replace"))
    sys.exit(st.get("exitcode", 0))

if __name__ == "__main__":
    main()
