"""Shared bootstrap for the pack and private tests (not a test module itself).

Importing it puts the packaged runner and the maintainer tools on sys.path and preloads the runner modules the loaders
need, so no test has to import them while bytecode writing is enabled.
"""
import http.server
import json
import os
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from pathlib import Path
from unittest import mock

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[3]
PACKAGE = REPO / "SkillsForUnity" / "Evaluation~"
TOOLS = REPO / "eval" / "tools"
GOOD = Path(__file__).resolve().parent / "fixtures" / "pack" / "good"
for entry in (PACKAGE, TOOLS):
    if str(entry) not in sys.path:
        sys.path.insert(0, str(entry))

import usk_eval.task_api                                    # noqa: E402,F401
from usk_eval import pack, private, problems, store         # noqa: E402,F401

BOM = b"\xef\xbb\xbf"
NO_PROXY_ENV = {"HTTP_PROXY": "", "http_proxy": "", "HTTPS_PROXY": "", "https_proxy": "", "ALL_PROXY": "",
                "all_proxy": "", "NO_PROXY": "127.0.0.1,localhost", "no_proxy": "127.0.0.1,localhost"}


def read_bytes(path):
    with open(str(path), "rb") as handle:
        return handle.read()


def write_bytes(path, data):
    os.makedirs(os.path.dirname(str(path)), exist_ok=True)
    with open(str(path), "wb") as handle:
        handle.write(data)


def read_text(path):
    return read_bytes(path).decode("utf-8-sig")


def write_text(path, text, bom=False):
    write_bytes(path, (BOM if bom else b"") + text.encode("utf-8"))


def load_json(path):
    return json.loads(read_text(path))


def save_json(path, obj):
    write_text(path, store.dumps(obj) + "\n")


def edit_json(path, edit):
    data = load_json(path)
    edit(data)
    save_json(path, data)
    return data


def free_port():
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    return port


def make_zip(path, entries, compression=zipfile.ZIP_STORED):
    os.makedirs(os.path.dirname(str(path)), exist_ok=True)
    with zipfile.ZipFile(str(path), "w", compression) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return str(path)


class TempCase(unittest.TestCase):
    """A scratch folder per test, removed afterwards."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="pack-test-")
        self.addCleanup(private.remove_tree, self.root)

    def path(self, *parts):
        return os.path.join(self.root, *parts)

    def copy_pack(self, name="pack"):
        """A writable copy of the good fixture pack (manifest.json, tasks/, private/)."""
        target = self.path(name)
        shutil.copytree(str(GOOD), target)
        return target

    def proxies_off(self):
        patcher = mock.patch.dict(os.environ, NO_PROXY_ENV)
        patcher.start()
        self.addCleanup(patcher.stop)


class PrivateCase(TempCase):
    """TempCase whose tempfile.gettempdir() is a folder of its own, so leftovers are visible and harmless."""

    def setUp(self):
        TempCase.setUp(self)
        self.temp_root = self.path("tmp")
        os.makedirs(self.temp_root)
        patcher = mock.patch.object(tempfile, "tempdir", self.temp_root)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.proxies_off()

    def leftovers(self):
        return sorted(os.listdir(self.temp_root))


class FakeServer(object):
    """HTTP server on 127.0.0.1:0. routes: path -> {status, body, headers, delay, length, trickle, drop}; unknown
    paths answer 404."""

    def __init__(self):
        self.routes = {}
        self.requests = []
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.0"

            def do_GET(self):
                outer.requests.append({"path": self.path, "headers": dict(self.headers.items())})
                route = outer.routes.get(self.path) or {"status": 404, "body": b"not found"}
                if route.get("delay"):
                    threading.Event().wait(route["delay"])
                if route.get("drop"):                       # hang up without a response
                    self.close_connection = True
                    return
                body = route.get("body", b"")
                try:
                    self.send_response(route.get("status", 200))
                    for key, value in (route.get("headers") or {}).items():
                        self.send_header(key, value)
                    if route.get("length", True):
                        self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    trickle = route.get("trickle")
                    if trickle:
                        for start in range(0, len(body), trickle[0]):
                            self.wfile.write(body[start:start + trickle[0]])
                            self.wfile.flush()
                            threading.Event().wait(trickle[1])
                    else:
                        self.wfile.write(body)
                except OSError:
                    pass

            def log_message(self, *args):
                pass

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.httpd.daemon_threads = True
        self.port = self.httpd.server_address[1]
        self.thread = threading.Thread(target=self.httpd.serve_forever, kwargs={"poll_interval": 0.02}, daemon=True)
        self.thread.start()

    def url(self, path=""):
        return "http://127.0.0.1:%d%s" % (self.port, path)

    def paths(self):
        return [item["path"] for item in self.requests]

    def wait_for(self, count, seconds=3.0):
        """Waits until `count` requests were recorded (a handler thread may lag behind a client that timed out)."""
        deadline = time.monotonic() + seconds
        while len(self.requests) < count and time.monotonic() < deadline:
            time.sleep(0.01)
        return len(self.requests) >= count

    def route(self, path, body=b"", status=200, **extra):
        self.routes[path] = dict(extra, body=body, status=status)

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()
        self.thread.join(5)


def openssl_available():
    return shutil.which("openssl") is not None


class SelfSignedServer(object):
    """HTTPS server on 127.0.0.1:0 with a throw-away self-signed certificate (made by the openssl command line tool,
    which tests must check with openssl_available()). Any client that verifies certificates has to refuse it, and
    answers "ok" to a client that does not."""

    def __init__(self, folder):
        cert, key = os.path.join(folder, "cert.pem"), os.path.join(folder, "key.pem")
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", key, "-out", cert,
                        "-days", "1", "-subj", "/CN=127.0.0.1"], check=True, stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE, timeout=120)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert, key)

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", "2")
                self.end_headers()
                self.wfile.write(b"ok")

            def log_message(self, *args):
                pass

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.httpd.daemon_threads = True
        self.httpd.socket = context.wrap_socket(self.httpd.socket, server_side=True)   # failed handshakes are ignored
        self.port = self.httpd.server_address[1]
        self.thread = threading.Thread(target=self.httpd.serve_forever, kwargs={"poll_interval": 0.02}, daemon=True)
        self.thread.start()

    def template(self):
        """A USK_EVAL_PRIVATE_URLS entry that points at this server."""
        return "https://127.0.0.1:%d/{ref}/{file}" % self.port

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()
        self.thread.join(5)

# Producer:Betsy
