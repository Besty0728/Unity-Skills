import http.client
import os
import sys
import threading
import unittest
from unittest import mock
from urllib.request import urlopen
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from harness.codex_proxy import CodexProxy  # noqa: E402


class UpstreamHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", "0")))
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b"{}")


class ProxyTimeoutTests(unittest.TestCase):
    def test_timeout_is_passed_to_upstream_connection(self):
        upstream = HTTPServer(("127.0.0.1", 0), UpstreamHandler)
        thread = threading.Thread(target=upstream.serve_forever, daemon=True)
        thread.start()
        proxy = CodexProxy("http://127.0.0.1:%d/v1" % upstream.server_port, timeout_s=3.25)
        seen = []
        original_init = http.client.HTTPConnection.__init__
        def record_timeout(connection, *args, **kwargs):
            seen.append(kwargs["timeout"])
            original_init(connection, *args, **kwargs)
        try:
            with mock.patch.object(http.client.HTTPConnection, "__init__", record_timeout):
                with urlopen(proxy.base_url + "/v1/responses", data=b"{}", timeout=2) as response:
                    self.assertEqual(response.read(), b"{}")
            self.assertEqual(seen[-1], 3.25)
        finally:
            proxy.close()
            upstream.shutdown()
            upstream.server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
