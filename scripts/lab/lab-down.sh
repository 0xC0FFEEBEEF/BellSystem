#!/usr/bin/env bash
LAB=${1:?lab dir}
for p in caddy server; do [ -f "$LAB/$p.pid" ] && kill "$(cat "$LAB/$p.pid")" 2>/dev/null; rm -f "$LAB/$p.pid"; done

