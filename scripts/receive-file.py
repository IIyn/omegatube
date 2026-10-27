#!/usr/bin/env python3
"""Dev helper: receives one file pushed by the TV app's DevUpload() on TCP 5556."""
import socket
import sys

out = sys.argv[1] if len(sys.argv) > 1 else "upload.bin"
srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("0.0.0.0", 5556))
srv.listen(1)
conn, addr = srv.accept()
size = 0
with open(out, "wb") as f:
    while chunk := conn.recv(65536):
        f.write(chunk)
        size += len(chunk)
print(f"received {size} bytes from {addr[0]} -> {out}", flush=True)
