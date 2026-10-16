"""REST client for the evaluation controller and verifier.

Retry semantics follow what the server actually guarantees (SkillsHttpServer.Listener/Dispatch):
503 is always answered before execution (queue full, server stopped, compiling/reloading) and
a refused connection never reached the editor, so both are safe to retry for any request.
504 means the job is still queued and may execute later, and a reset/timeout mid-response is
ambiguous: reads retry, writes raise WriteUncertain so the caller reads state back instead of
replaying a non-idempotent change.
"""
import json
import socket
import time
import urllib.error
import urllib.parse
import urllib.request

MUTATING_FLAGS = ("mutatesScene", "mutatesAssets", "mayTriggerReload", "mayEnterPlayMode")


class RestError(RuntimeError):
    def __init__(self, message, status=None, payload=None):
        RuntimeError.__init__(self, message)
        self.status = status
        self.payload = payload


class WriteUncertain(RestError):
    """The write may or may not have executed; read state back before deciding."""


class SkillError(RestError):
    """The skill answered with a business error envelope (status error/partial)."""

    @property
    def code(self):
        return (self.payload or {}).get("errorCode")


class Client(object):
    def __init__(self, instance, agent_id="evaluator", retry_window_s=600.0, timeout_s=120.0):
        self.instance = instance
        self.agent_id = agent_id
        self.retry_window_s = retry_window_s
        self.timeout_s = timeout_s
        self._schema = {}
        self.calls = 0

    # ------------------------------------------------------------------ transport

    def _headers(self):
        return {"Content-Type": "application/json", "X-Agent-Id": self.agent_id,
                "X-Expect-Instance": self.instance.instance_id}

    def _once(self, method, path, body=None, timeout=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.instance.base_url + path, data=data, method=method,
                                         headers=self._headers())
        self.calls += 1
        with urllib.request.urlopen(request, timeout=timeout or self.timeout_s) as response:
            raw = response.read().decode("utf-8")
            return response.status, json.loads(raw) if raw else {}

    def request(self, method, path, body=None, write=False, timeout=None):
        deadline = time.time() + self.retry_window_s
        last = None
        while True:
            try:
                return self._once(method, path, body, timeout)[1]
            except urllib.error.HTTPError as exc:
                payload = _json_or_none(exc)
                if exc.code in (503, 429):  # both answered before execution (Queue/Listener admission)
                    last = exc
                    wait = _retry_after(exc, payload)
                elif exc.code == 504:
                    if write:
                        raise WriteUncertain("504 for write %s %s" % (method, path), 504, payload)
                    last, wait = exc, 3.0
                elif exc.code == 409:
                    raise RestError("instance mismatch (409) for %s" % path, 409, payload)
                else:
                    raise RestError("HTTP %d for %s %s" % (exc.code, method, path), exc.code, payload)
            except urllib.error.URLError as exc:
                if not _refused(exc) and write:
                    raise WriteUncertain("transport error for write %s %s: %r" % (method, path, exc))
                last, wait = exc, 2.0
            except (socket.timeout, TimeoutError, ConnectionResetError) as exc:
                if write:
                    raise WriteUncertain("timeout/reset for write %s %s: %r" % (method, path, exc))
                last, wait = exc, 2.0
            if time.time() + wait > deadline:
                raise RestError("%s %s unreachable within %.0fs: %r" % (method, path, self.retry_window_s, last))
            time.sleep(wait)

    # ------------------------------------------------------------------ endpoints

    def health(self):
        return self.request("GET", "/health")

    def get(self, path, params=None):
        query = ("?" + urllib.parse.urlencode(params)) if params else ""
        return self.request("GET", path + query)

    def schema(self, names):
        missing = [n for n in names if n not in self._schema]
        if missing:
            payload = self.get("/skills/schema", {"names": ",".join(missing), "wire": "v2"})
            result = payload.get("result", payload)
            for skill in result.get("skills") or []:
                self._schema[skill["name"]] = skill
            for name in missing:
                self._schema.setdefault(name, None)
        return {n: self._schema.get(n) for n in names}

    def is_read_only(self, skill):
        meta = self.schema([skill])[skill]
        if not meta:
            return False
        flags = meta.get("flags") or []
        return "readOnly" in flags and not any(f in flags for f in MUTATING_FLAGS)

    def read(self, skill, **args):
        """Call a read-only skill; refuses anything the schema does not mark readOnly."""
        if not self.is_read_only(skill):
            raise RestError("verifier refused non-read-only skill %s" % skill)
        payload = self.request("POST", "/skill/%s" % skill, args, write=False)
        return _unwrap(skill, payload)

    def read_raw(self, skill, **args):
        """Like read(), but returns the envelope without raising on business errors."""
        if not self.is_read_only(skill):
            raise RestError("verifier refused non-read-only skill %s" % skill)
        return self.request("POST", "/skill/%s" % skill, args, write=False)

    def write(self, skill, **args):
        """Controller-side write; business errors raise SkillError, ambiguity raises WriteUncertain."""
        payload = self.request("POST", "/skill/%s" % skill, args, write=True)
        return _unwrap(skill, payload)

    def batch(self, steps, mode=None, continue_on_error=False):
        path = "/skills/batch" + ("?mode=%s" % mode if mode else "")
        body = {"steps": steps}
        if continue_on_error:
            body["continueOnError"] = True
        payload = self.request("POST", path, body, write=True)
        if payload.get("status") not in ("completed", "success"):
            raise SkillError("batch failed: %s" % json.dumps(payload)[:600], payload=payload)
        return payload

    def wait_job(self, job_id, timeout_s=600.0):
        """Long-poll /jobs/{id} until a terminal state; 503 during a reload is retried."""
        deadline = time.time() + timeout_s
        while True:
            payload = self.request("GET", "/jobs/%s?wait=30" % job_id, timeout=60)
            job = payload.get("result", payload)
            if job.get("status") in ("completed", "failed", "cancelled"):
                return job
            if time.time() > deadline:
                raise RestError("job %s not terminal after %.0fs: %s" % (job_id, timeout_s, job.get("status")))

    def wait_idle(self, settle_s=2.0, timeout_s=600.0):
        """Wait until the editor reports no compile/import twice in a row."""
        deadline = time.time() + timeout_s
        quiet = 0
        while time.time() < deadline:
            health = self.health()
            comp = health.get("compilation") or {}
            busy = comp.get("isCompiling") or comp.get("isUpdating") or health.get("domainReloadPending")
            quiet = 0 if busy else quiet + 1
            if quiet >= 2:
                return health
            time.sleep(settle_s)
        raise RestError("editor still busy after %.0fs" % timeout_s)


def _unwrap(skill, payload):
    status = payload.get("status")
    if status in ("error", "partial") or (status is None and payload.get("errorCode")):
        raise SkillError("%s failed: %s %s" % (skill, payload.get("errorCode"), payload.get("error")),
                         payload=payload)
    result = payload.get("result", payload)
    if isinstance(result, dict) and result.get("success") is False:
        raise SkillError("%s returned success=false: %s" % (skill, json.dumps(result)[:400]), payload=payload)
    return result


def _json_or_none(exc):
    try:
        return json.loads(exc.read().decode("utf-8"))
    except Exception:
        return None


def _retry_after(exc, payload):
    try:
        return max(1.0, float(exc.headers.get("Retry-After")))
    except (TypeError, ValueError):
        pass
    if isinstance(payload, dict) and payload.get("retryAfterSeconds"):
        return float(payload["retryAfterSeconds"])
    return 3.0


def _refused(exc):
    reason = getattr(exc, "reason", None)
    return isinstance(reason, ConnectionRefusedError) or "Connection refused" in str(reason)

# Producer:Betsy
