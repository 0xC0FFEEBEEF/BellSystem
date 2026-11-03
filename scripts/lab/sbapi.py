#!/usr/bin/env python3
"""Tiny SchoolBell office-API client for lab/test automation (cookie session + CSRF header).

  sbapi.py --base https://192.168.122.1:8443 --ca lab-ca.crt --jar jar.txt METHOD PATH [JSON]
  sbapi.py ... upload PATH field=@file.wav field=value ...
Prints the JSON response. Exit 1 on HTTP >= 400. Lab tooling only.
"""
import argparse, http.cookiejar, json, mimetypes, os, ssl, sys, urllib.request, uuid

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", required=True)
    ap.add_argument("--ca")
    ap.add_argument("--jar", required=True)
    ap.add_argument("--out", help="write raw response body to this file")
    ap.add_argument("method")
    ap.add_argument("path")
    ap.add_argument("rest", nargs="*")
    a = ap.parse_args()
    jar = http.cookiejar.MozillaCookieJar(a.jar)
    if os.path.exists(a.jar):
        jar.load(ignore_discard=True, ignore_expires=True)
    ctx = ssl.create_default_context(cafile=a.ca) if a.ca else None
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar), urllib.request.HTTPSHandler(context=ctx))
    def csrf():
        for c in jar:
            if c.name == "sb_csrf": return c.value
        opener.open(a.base + "/api/auth/csrf").read()
        jar.save(ignore_discard=True, ignore_expires=True)
        for c in jar:
            if c.name == "sb_csrf": return c.value
        return ""
    method = a.method.upper()
    headers = {"Origin": a.base}
    data = None
    if method == "UPLOAD":
        method = "POST"
        if a.rest and a.rest[0] in ("PUT", "POST"):
            method = a.rest.pop(0)
        boundary = uuid.uuid4().hex
        parts = []
        for kv in a.rest:
            k, v = kv.split("=", 1)
            if v.startswith("@"):
                fn = v[1:]
                ct = mimetypes.guess_type(fn)[0] or "application/octet-stream"
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"; filename="{os.path.basename(fn)}"\r\nContent-Type: {ct}\r\n\r\n'.encode() + open(fn, "rb").read() + b"\r\n")
            else:
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode())
        data = b"".join(parts) + f"--{boundary}--\r\n".encode()
        headers["Content-Type"] = f"multipart/form-data; boundary={boundary}"
    elif a.rest:
        data = a.rest[0].encode()
        headers["Content-Type"] = "application/json"
    if method not in ("GET", "HEAD"):
        headers["X-CSRF"] = csrf()
    req = urllib.request.Request(a.base + a.path, data=data, method=method, headers=headers)
    try:
        resp = opener.open(req)
        body, status = resp.read(), resp.status
    except urllib.error.HTTPError as e:
        body, status = e.read(), e.code
    jar.save(ignore_discard=True, ignore_expires=True)
    if a.out:
        open(a.out, "wb").write(body)
        print(json.dumps({"status": status, "bytes": len(body)}))
    else:
        try: print(json.dumps(json.loads(body), indent=1))
        except Exception: print(body[:2000].decode("utf-8", "replace"))
    sys.exit(1 if status >= 400 else 0)

if __name__ == "__main__":
    main()
