"""Tiny mock of the Clash /connections endpoint for local UI testing."""
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

START = time.time()


def payload():
    t = time.time() - START
    def conn(cid, process, path, chain, up, down):
        return {
            "id": cid,
            "upload": int(t * up),
            "download": int(t * down),
            "start": "2026-10-18T04:00:00Z",
            "chains": chain,
            "rule": "Match",
            "rulePayload": "",
            "metadata": {
                "network": "tcp",
                "type": "HTTP",
                "host": "example.com",
                "destinationIP": "1.2.3.4",
                "destinationPort": "443",
                "process": process,
                "processPath": path,
            },
        }

    connections = [
        conn("c1", "chrome.exe", r"C:\Program Files\Google\Chrome\chrome.exe",
             ["\ud83c\udded\ud83c\uddf0 香港01", "自动选择", "GLOBAL"], 90_000, 950_000),
        conn("c2", "Code.exe", r"C:\Program Files\Microsoft VS Code\Code.exe",
             ["\ud83c\uddef\ud83c\uddf5 东京02", "自动选择", "GLOBAL"], 20_000, 120_000),
        conn("c3", "Spotify.exe", r"C:\Users\Public\Spotify.exe",
             ["DIRECT"], 3_000, 8_000),
    ]
    return {"downloadTotal": 1, "uploadTotal": 1, "connections": connections}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        body = json.dumps(payload()).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 19090
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
