"""Attempt-scoped Responses forwarding with metadata-only model identity evidence.

Bodies are forwarded unchanged. Request headers, credentials, prompts, and generated content
are never recorded. The only listener is an ephemeral IPv4 loopback port.
"""
import http.client
import http.server
import json
import threading
import urllib.parse

HOP_HEADERS = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
               "te", "trailer", "transfer-encoding", "upgrade"}
MAX_METADATA_EVENT = 2 * 1024 * 1024


def _usage(value):
    if not isinstance(value, dict):
        return None
    allowed = {"input_tokens", "output_tokens", "total_tokens", "input_tokens_details",
               "output_tokens_details", "cached_tokens", "cache_write_tokens", "reasoning_tokens"}
    return {key: (_usage(item) if isinstance(item, dict) else item)
            for key, item in value.items()
            if key in allowed and isinstance(item, (int, float, dict)) and not isinstance(item, bool)}


class ResponseMetadata:
    def __init__(self):
        self._lock = threading.Lock()
        self._observations = []
        self._request_ids = set()
        self._errors = []

    def observe(self, payload):
        if not isinstance(payload, dict):
            return
        kind = payload.get("type")
        if kind in ("response.created", "response.completed", "response.failed", "response.incomplete"):
            response = payload.get("response")
        elif payload.get("object") == "response":
            kind, response = "response.json", payload
        else:
            return
        if not isinstance(response, dict):
            return
        model = response.get("model")
        record = {"event": kind, "model": model if isinstance(model, str) else None,
                  "responseId": response.get("id"), "usage": _usage(response.get("usage"))}
        with self._lock:
            self._observations.append(record)

    def request_id(self, value):
        if value:
            with self._lock:
                self._request_ids.add(value)

    def error(self, value):
        with self._lock:
            self._errors.append(value)

    def snapshot(self):
        with self._lock:
            models = sorted({r["model"] for r in self._observations if r["model"]})
            return {"source": "response.model" if models else None, "models": models,
                    "observations": list(self._observations), "requestIds": sorted(self._request_ids),
                    "errors": list(self._errors)}


class _BodyMetadata:
    def __init__(self, content_type, metadata):
        self.sse = "text/event-stream" in content_type.lower()
        self.metadata = metadata
        self.buffer = bytearray()
        self.data = []
        self.oversized = False

    def feed(self, chunk):
        if self.oversized:
            return
        self.buffer.extend(chunk)
        if self.sse:
            while b"\n" in self.buffer:
                line, _, rest = self.buffer.partition(b"\n")
                self.buffer = bytearray(rest)
                line = line.rstrip(b"\r")
                if not line:
                    self._event()
                elif line.startswith(b"data:"):
                    self.data.append(line[5:].lstrip(b" "))
                if sum(map(len, self.data)) > MAX_METADATA_EVENT:
                    self.data = []
                    self.metadata.error("metadata event exceeds limit")
        if len(self.buffer) > MAX_METADATA_EVENT:
            self.buffer.clear()
            self.oversized = True
            self.metadata.error("metadata body exceeds limit")

    def _event(self):
        raw = b"\n".join(self.data)
        self.data = []
        if raw and raw != b"[DONE]":
            self._decode(raw)

    def _decode(self, raw):
        try:
            self.metadata.observe(json.loads(raw))
        except (ValueError, UnicodeError):
            pass

    def finish(self):
        if self.oversized:
            return
        if self.sse:
            self.feed(b"\n\n")
        else:
            self._decode(self.buffer)


class CodexProxy:
    def __init__(self, upstream_url, timeout_s=60):
        self.timeout_s = max(0.1, float(timeout_s))
        self.upstream = urllib.parse.urlsplit(upstream_url)
        if self.upstream.scheme not in ("http", "https") or not self.upstream.hostname:
            raise ValueError("Codex provider requires an HTTP(S) base_url")
        if self.upstream.username or self.upstream.password or self.upstream.query or self.upstream.fragment:
            raise ValueError("Codex provider base_url must not contain credentials, query, or fragment")
        self.records = ResponseMetadata()
        self._active_lock = threading.Lock()
        self._idle = threading.Condition(self._active_lock)
        self._connections = set()
        proxy = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):
                pass

            def do_POST(self):
                self._forward()

            def do_GET(self):
                self._forward()

            def _forward(self):
                prefix = proxy.upstream.path.rstrip("/")
                path = urllib.parse.urlsplit(self.path)
                if path.scheme or path.netloc or (prefix and not path.path.startswith(prefix + "/")):
                    self.send_error(400)
                    return
                connection = None
                try:
                    body = self._body()
                    connection_class = http.client.HTTPSConnection if proxy.upstream.scheme == "https" else http.client.HTTPConnection
                    connection = connection_class(proxy.upstream.hostname, proxy.upstream.port, timeout=proxy.timeout_s)
                    with proxy._active_lock:
                        proxy._connections.add(connection)
                    excluded = HOP_HEADERS | {"host", "content-length"}
                    headers = {key: value for key, value in self.headers.items() if key.lower() not in excluded}
                    headers["Host"] = proxy.upstream.netloc
                    if body:
                        headers["Content-Length"] = str(len(body))
                    connection.request(self.command, self.path, body=body, headers=headers)
                    response = connection.getresponse()
                    proxy.records.request_id(response.getheader("x-request-id") or response.getheader("request-id"))
                    if response.status >= 400:
                        proxy.records.error("upstream HTTP %d" % response.status)
                    self.send_response_only(response.status, response.reason)
                    for key, value in response.getheaders():
                        if key.lower() not in HOP_HEADERS:
                            self.send_header(key, value)
                    self.send_header("Connection", "close")
                    self.end_headers()
                    self.close_connection = True
                    collector = _BodyMetadata(response.getheader("Content-Type") or "", proxy.records)
                    if response.getheader("Content-Encoding") not in (None, "identity"):
                        # Preserve compressed bytes; lack of parsed evidence remains explicit.
                        collector.oversized = True
                        proxy.records.error("compressed response metadata unavailable")
                    while True:
                        chunk = response.read1(8192)
                        if not chunk:
                            break
                        collector.feed(chunk)
                        self.wfile.write(chunk)
                        self.wfile.flush()
                    collector.finish()
                except (OSError, ValueError, http.client.HTTPException) as exc:
                    proxy.records.error(type(exc).__name__)
                    self.close_connection = True
                    if connection is None:
                        self.send_error(502)
                finally:
                    if connection:
                        with proxy._active_lock:
                            proxy._connections.discard(connection)
                            proxy._idle.notify_all()
                        connection.close()

            def _body(self):
                if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                    chunks = []
                    while True:
                        size = int(self.rfile.readline().split(b";", 1)[0], 16)
                        if not size:
                            while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                                pass
                            return b"".join(chunks)
                        chunks.append(self.rfile.read(size))
                        if self.rfile.read(2) != b"\r\n":
                            raise ValueError("invalid chunk framing")
                return self.rfile.read(int(self.headers.get("Content-Length", 0)))

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
        self.server.block_on_close = False
        self.base_url = "http://127.0.0.1:%d%s" % (self.server.server_port, self.upstream.path.rstrip("/"))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        with self._active_lock:
            for connection in self._connections:
                connection.close()
        self.server.server_close()
        self.thread.join(timeout=2)

    def metadata(self):
        # A client may finish reading Content-Length bytes before the handler finalizes JSON metadata.
        with self._idle:
            finished = self._idle.wait_for(lambda: not self._connections, timeout=2.0)
        snapshot = self.records.snapshot()
        if not finished:
            snapshot["errors"].append("metadata collection still active")
        return snapshot

# Producer:Betsy
