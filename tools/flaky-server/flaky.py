#!/usr/bin/env python3
"""Dev test server: serves one file with HTTP Range support, but misbehaves on purpose to exercise the
core's recovery: every Nth request is cut mid-body, and the first request on the "/expired" path gets 403.

Usage: python3 -I tools/flaky-server/flaky.py <file> [port]
"""
import http.server
import os
import re
import sys

PATH = sys.argv[1]
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8001
SIZE = os.path.getsize(PATH)
state = {"requests": 0, "expired_served": False}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        state["requests"] += 1
        n = state["requests"]
        if self.path.startswith("/expired") and not state["expired_served"]:
            state["expired_served"] = True
            self.send_response(403)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        m = re.match(r"bytes=(\d+)-(\d*)", self.headers.get("Range", ""))
        start = int(m.group(1)) if m else 0
        end = int(m.group(2)) if m and m.group(2) else SIZE - 1
        end = min(end, SIZE - 1)
        self.send_response(206)
        self.send_header("Content-Range", f"bytes {start}-{end}/{SIZE}")
        self.send_header("Content-Length", str(end - start + 1))
        self.end_headers()
        with open(PATH, "rb") as f:
            f.seek(start)
            data = f.read(end - start + 1)
        if n % 4 == 0:  # cut every 4th response halfway through
            self.wfile.write(data[: len(data) // 2])
            self.wfile.flush()
            self.close_connection = True
            self.connection.shutdown(2)
            return
        self.wfile.write(data)

    def log_message(self, fmt, *args):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
