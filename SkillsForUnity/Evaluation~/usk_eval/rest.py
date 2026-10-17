"""REST client for the runner, verifiers and solvers (ported from v1 eval/harness/rest.py).

Retry semantics follow what the server guarantees (SkillsHttpServer.Listener/Dispatch): 503/429 are answered
before execution and a refused connection never reached the editor, so both are retried for any request. 504
means the job is still queued and may run later, and a reset/timeout mid-response is ambiguous, as is an empty or
cut-short body (a listener shutting down for a domain reload can answer 200 with no body): reads retry, writes raise
WriteUncertain so the caller reads state back instead of replaying a non-idempotent change.
Every request carries X-Agent-Id, User-Agent and X-Expect-Instance (contract 7.3).
"""
import http.client
import json
import socket
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

RUNNER_AGENT = "UnitySkillsEval"
SOLVER_AGENT = "SelftestSolver"
MUTATING_FLAGS = ("mutatesScene", "mutatesAssets", "mayTriggerReload", "mayEnterPlayMode")
TERMINAL_JOB_STATES = ("completed", "failed", "cancelled")
IDLE_SETTLE_S = 2.0
TRANSIENT_WAIT_S = 2.0


def user_agent(runner_version, solver=False):
    return "%s/%s" % ("unity-skills-eval-selftest" if solver else "unity-skills-eval", runner_version)


class _Counter(object):
    """Requests this process sent that may have reached the server (refused connections excluded)."""

    def __init__(self):
        self._lock = threading.Lock()
        self.value = 0

    def bump(self):
        with self._lock:
            self.value += 1


REQUESTS = _Counter()


class RestError(RuntimeError):
    def __init__(self, message, status=None, payload=None):
        RuntimeError.__init__(self, message)
        self.status = status
        self.payload = payload


class Unreachable(RestError):
    """No answer within the retry window."""


class StillBusy(Unreachable):
    """wait_idle ran out of time while the editor kept compiling or importing; payload is its last /health."""


class WriteUncertain(RestError):
    """The write may or may not have executed; read state back before deciding."""


class SkillError(RestError):
    """The skill answered with a business error envelope (status error/partial)."""

    @property
    def code(self):
        return (self.payload or {}).get("errorCode")


def as_problems(exc, log_path):
    """REST_UNREACHABLE / INSTANCE_MISMATCH / REST_ERROR for a failed runner request (contract 3.1)."""
    from .problems import problem
    if isinstance(exc, Unreachable):
        return [problem("REST_UNREACHABLE", detail=str(exc)[:300])]
    if exc.status == 409:
        return [problem("INSTANCE_MISMATCH", detail=str(exc)[:300])]
    return [problem("REST_ERROR", [str(exc)[:200], log_path])]


def refused(exc):
    reason = getattr(exc, "reason", None)
    return isinstance(reason, ConnectionRefusedError) or "Connection refused" in str(reason) or \
        "actively refused" in str(reason)


class Client(object):
    def __init__(self, instance, agent_id=RUNNER_AGENT, agent_ua="unity-skills-eval", retry_window_s=600.0,
                 timeout_s=120.0):
        self.instance = instance
        self.agent_id = agent_id
        self.agent_ua = agent_ua
        self.retry_window_s = retry_window_s
        self.timeout_s = timeout_s
        self._schema = {}

    # ------------------------------------------------------------------ transport

    def _headers(self):
        return {"Content-Type": "application/json", "X-Agent-Id": self.agent_id, "User-Agent": self.agent_ua,
                "X-Expect-Instance": self.instance.instance_id}

    def _once(self, method, path, body=None, timeout=None):
        """(status, payload) of one exchange; an empty or cut-short body raises ValueError (unreadable)."""
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.instance.base_url + path, data=data, method=method,
                                         headers=self._headers())
        try:
            response = urllib.request.urlopen(request, timeout=timeout or self.timeout_s)
        except urllib.error.URLError as exc:
            if isinstance(exc, urllib.error.HTTPError) or not refused(exc):
                REQUESTS.bump()
            raise
        except (socket.timeout, TimeoutError, ConnectionResetError):
            REQUESTS.bump()
            raise
        except http.client.HTTPException as exc:      # a garbled status line or headers
            REQUESTS.bump()
            raise ValueError("garbled answer to %s %s: %s" % (method, path, type(exc).__name__)) from None
        REQUESTS.bump()
        with response:
            try:
                raw = response.read().decode("utf-8")
            except http.client.HTTPException:        # IncompleteRead
                raise ValueError("cut-short answer to %s %s" % (method, path)) from None
        if not raw.strip():
            raise ValueError("empty answer to %s %s" % (method, path))
        return response.status, json.loads(raw)

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
                if not refused(exc) and write:
                    raise WriteUncertain("transport error for write %s %s: %r" % (method, path, exc))
                last, wait = exc, TRANSIENT_WAIT_S
            except (socket.timeout, TimeoutError, ConnectionResetError) as exc:
                if write:
                    raise WriteUncertain("timeout/reset for write %s %s: %r" % (method, path, exc))
                last, wait = exc, TRANSIENT_WAIT_S
            except ValueError as exc:     # an empty, cut-short or garbled body during a reload
                if write:
                    raise WriteUncertain("unreadable answer for write %s %s: %s" % (method, path, exc))
                last, wait = exc, TRANSIENT_WAIT_S
            if time.time() + wait > deadline:
                raise Unreachable("%s %s unreachable within %.0fs: %r" % (method, path, self.retry_window_s, last))
            time.sleep(wait)

    # ------------------------------------------------------------------ endpoints

    def health(self):
        return self.request("GET", "/health")

    def probe_health(self, timeout=5.0):
        """One /health attempt without retries; None when the editor does not answer."""
        try:
            return self._once("GET", "/health", timeout=timeout)[1]
        except Exception:
            return None

    def get(self, path, params=None, timeout=None):
        query = ("?" + urllib.parse.urlencode(params)) if params else ""
        return self.request("GET", path + query, timeout=timeout)

    def schema(self, names):
        """Schema entry per name. A reloading editor can answer without some names, so a missing one is asked for
        once more and then reported; it is never cached as unknown."""
        missing = self._fetch_schema(names)
        if missing:
            time.sleep(TRANSIENT_WAIT_S)
            missing = self._fetch_schema(missing)
        if missing:
            raise RestError("/skills/schema has no entry for %s (asked twice)" % ", ".join(missing))
        return {n: self._schema[n] for n in names}

    def _fetch_schema(self, names):
        """Fetches the names not cached yet; returns those the answer left out."""
        missing = [n for n in names if n not in self._schema]
        if missing:
            payload = self.get("/skills/schema", {"names": ",".join(missing), "wire": "v2"})
            result = payload.get("result", payload)
            for skill in result.get("skills") or []:
                self._schema[skill["name"]] = skill
        return [n for n in missing if n not in self._schema]

    def is_read_only(self, skill):
        flags = self.schema([skill])[skill].get("flags") or []
        return "readOnly" in flags and not any(f in flags for f in MUTATING_FLAGS)

    def read(self, skill, **args):
        """Call a read-only skill; refuses anything the schema does not mark readOnly."""
        if not self.is_read_only(skill):
            raise RestError("refused non-read-only skill %s" % skill)
        payload = self.request("POST", "/skill/%s" % skill, args, write=False)
        return _unwrap(skill, payload)

    def read_raw(self, skill, **args):
        """Like read(), but returns the envelope without raising on business errors."""
        if not self.is_read_only(skill):
            raise RestError("refused non-read-only skill %s" % skill)
        return self.request("POST", "/skill/%s" % skill, args, write=False)

    def write(self, skill, **args):
        """Runner-side write; business errors raise SkillError, ambiguity raises WriteUncertain."""
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

    def job(self, job_id, wait=30, timeout=60):
        payload = self.request("GET", "/jobs/%s?wait=%d" % (urllib.parse.quote(str(job_id)), wait), timeout=timeout)
        return payload.get("result", payload)

    def wait_job(self, job_id, timeout_s=600.0):
        """Long-poll /jobs/{id} until a terminal state; 503 during a reload is retried."""
        deadline = time.time() + timeout_s
        while True:
            job = self.job(job_id)
            if job.get("status") in TERMINAL_JOB_STATES:
                return job
            if time.time() > deadline:
                raise RestError("job %s not terminal after %.0fs: %s" % (job_id, timeout_s, job.get("status")))

    def wait_idle(self, settle_s=None, timeout_s=600.0):
        """Wait until the editor reports no compile/import twice in a row."""
        deadline = time.time() + timeout_s
        quiet = 0
        health = None
        while time.time() < deadline:
            health = self.health()
            comp = health.get("compilation") or {}
            busy = comp.get("isCompiling") or comp.get("isUpdating") or health.get("domainReloadPending")
            quiet = 0 if busy else quiet + 1
            if quiet >= 2:
                return health
            time.sleep(IDLE_SETTLE_S if settle_s is None else settle_s)
        raise StillBusy("editor still busy after %.0fs" % timeout_s, payload=health)


class ReadOnlyView(object):
    """What verifiers get: reads only, so `verify` can never write to the editor (contract 5.5)."""

    def __init__(self, client):
        self._client = client

    def read(self, skill, **args):
        return self._client.read(skill, **args)

    def read_raw(self, skill, **args):
        return self._client.read_raw(skill, **args)

    def is_read_only(self, skill):
        return self._client.is_read_only(skill)

    def schema(self, names):
        return self._client.schema(names)

    def health(self):
        return self._client.health()


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
    finally:
        exc.close()


def _retry_after(exc, payload):
    try:
        return max(1.0, float(exc.headers.get("Retry-After")))
    except (TypeError, ValueError, AttributeError):
        pass
    if isinstance(payload, dict) and payload.get("retryAfterSeconds"):
        try:
            return float(payload["retryAfterSeconds"])
        except (TypeError, ValueError):
            pass
    return 3.0

# Producer:Betsy
