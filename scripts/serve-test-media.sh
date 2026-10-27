#!/usr/bin/env bash
# Serves the PoC test streams to the TV and prints the TV's UDP log lines.
# Usage: scripts/serve-test-media.sh [media dir]
set -euo pipefail
DIR="${1:-$HOME/Videos/omegatube-test}"
python3 -m http.server 8000 --bind 0.0.0.0 --directory "$DIR" &
HTTP_PID=$!
trap 'kill $HTTP_PID' EXIT
echo "serving $DIR on :8000, TV logs on udp :5555"
python3 -u -c '
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("0.0.0.0", 5555))
while True:
    data, addr = s.recvfrom(2048)
    print(addr[0], data.decode(errors="replace"), flush=True)
'
