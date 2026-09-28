#!/usr/bin/env python3
"""Stdlib mock of Cloudflare Tunnel REST endpoints for DinD publish tests."""

from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import sys


def ok(handler, result):
    body = json.dumps({"success": True, "result": result, "errors": []}).encode()
    handler.send_response(200)
    handler.send_header("Content-Type", "application/json")
    handler.send_header("Content-Length", str(len(body)))
    handler.end_headers()
    handler.wfile.write(body)


class H(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _read(self):
        n = int(self.headers.get("Content-Length", 0))
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path.startswith("/client/v4/zones"):
            ok(self, [{"id": "zone-test", "name": "example.com", "account": {"id": "acct-test"}}])
        elif "/cfd_tunnel/" in path and path.endswith("/token"):
            ok(self, "connector-token-from-get")
        elif path.startswith("/client/v4/accounts/") and "cfd_tunnel" in path:
            ok(self, [])
        elif "/dns_records" in path:
            ok(self, [])
        else:
            self.send_error(404)

    def do_POST(self):
        body = self._read()
        path = self.path.split("?", 1)[0]
        if path.endswith("/cfd_tunnel"):
            open("/tmp/cf-tunnel-create.json", "wb").write(body)
            ok(self, {"id": "tun-test", "name": "interfold-test", "token": "connector-token-jwt"})
        elif path.endswith("/dns_records"):
            open("/tmp/cf-dns-post.json", "wb").write(body)
            ok(self, {"id": "dns-1"})
        else:
            self.send_error(404)

    def do_PUT(self):
        body = self._read()
        open("/tmp/cf-ingress-put.json", "wb").write(body)
        ok(self, {})


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
