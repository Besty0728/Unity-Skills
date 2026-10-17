"""Locate the live UnitySkills editor of a project and prove its identity (contract 2.4 check, 11 #17).

Ports are handed out in start order, so the registry is looked up by project path, and the answering editor must
report the instanceId RegistryService derives from productName + the project path Unity sees
(RegistryService.cs: InstanceId / ComputeStableHash). /health runs on the main thread, so a timeout means busy.
"""
import hashlib
import http.client
import json
import os
import re
import socket
import time
import urllib.error
import urllib.request

from . import paths as P
from . import rest
from .problems import ProblemError, problem

HEALTH_TIMEOUT_S = 10.0


class Instance(object):
    def __init__(self, project_path, unity_path, instance_id, port, pid, product_name, health):
        self.project_path = project_path
        self.unity_path = unity_path
        self.instance_id = instance_id
        self.port = port
        self.pid = pid
        self.product_name = product_name
        self.health = health

    @property
    def base_url(self):
        return "http://127.0.0.1:%d" % self.port

    def as_dict(self):
        return {"id": self.instance_id, "port": self.port, "pid": self.pid}


def expected_instance_id(product_name, unity_path):
    clean = re.sub(r"[^A-Za-z0-9]", "", product_name or "")
    digest = hashlib.sha256(unity_path.encode("utf-8")).digest()[:4]
    return "%s_%s" % (clean, digest.hex().upper())


def product_name_from_disk(project_path):
    settings = os.path.join(project_path, "ProjectSettings", "ProjectSettings.asset")
    try:
        with open(settings, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                match = re.match(r"\s*productName:\s*(.*)$", line)
                if match:
                    value = match.group(1).strip()
                    if len(value) >= 2 and value[0] == value[-1] == '"':
                        try:
                            value = json.loads(value)
                        except ValueError:
                            value = value[1:-1]
                    elif len(value) >= 2 and value[0] == value[-1] == "'":
                        value = value[1:-1].replace("''", "'")
                    return value
    except OSError:
        return None
    return None


def _health(port, headers, timeout):
    request = urllib.request.Request("http://127.0.0.1:%d/health" % port, headers=headers, method="GET")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def locate(project_path, environ, user_agent, wait_s=120.0, poll_s=1.0, clock=time.time, sleep=time.sleep):
    """The verified Instance for project_path; INSTANCE_NOT_RUNNING / INSTANCE_MISMATCH / REST_UNREACHABLE."""
    deadline = clock() + wait_s
    product = product_name_from_disk(project_path)
    last, busy = "no answer", False
    while True:
        entry = P.registry_entry(P.read_registry(P.registry_path(environ)), project_path)
        if entry is None:
            raise ProblemError(problem("INSTANCE_NOT_RUNNING", detail="no registry entry for %s" % project_path))
        unity_path = entry["path"]
        expected = expected_instance_id(product, unity_path) if product is not None else entry.get("id")
        if entry.get("id") and expected and entry["id"] != expected:
            raise ProblemError(problem("INSTANCE_MISMATCH", detail="registry id %s, expected %s" % (
                entry["id"], expected)))
        status = P.entry_status(entry)
        if status == "running" and not P.heartbeat_fresh(entry, time.time()):
            raise ProblemError(problem("INSTANCE_NOT_RUNNING", detail="stale registry heartbeat"))
        if status == "running":
            headers = {"X-Agent-Id": rest.RUNNER_AGENT, "User-Agent": user_agent}
            if expected:
                headers["X-Expect-Instance"] = expected
            try:
                health = _health(int(entry["port"]), headers, HEALTH_TIMEOUT_S)
                rest.REQUESTS.bump()
                answered = health.get("instanceId")
                if expected and answered != expected:
                    raise ProblemError(problem("INSTANCE_MISMATCH", detail="port %s answers %s" % (
                        entry["port"], answered)))
                return Instance(project_path, unity_path, answered or expected, int(entry["port"]),
                                entry.get("pid"), product, health)
            except urllib.error.HTTPError as exc:
                rest.REQUESTS.bump()
                exc.close()
                if exc.code == 409:
                    raise ProblemError(problem("INSTANCE_MISMATCH", detail="HTTP 409 from port %s" % entry["port"]))
                last, busy = "HTTP %d" % exc.code, True
            except urllib.error.URLError as exc:
                if rest.refused(exc):
                    last, busy = "connection refused", False
                else:
                    rest.REQUESTS.bump()
                    last, busy = "transport error %r" % getattr(exc, "reason", exc), True
            except (socket.timeout, TimeoutError, ConnectionResetError, ValueError, http.client.HTTPException) as exc:
                rest.REQUESTS.bump()
                last, busy = "no answer (%s)" % type(exc).__name__, True
        else:
            last, busy = "editor is %s" % status, False
        if clock() >= deadline:
            code = "REST_UNREACHABLE" if busy else "INSTANCE_NOT_RUNNING"
            raise ProblemError(problem(code, detail="%s within %.0fs" % (last, wait_s)))
        sleep(poll_s)

# Producer:Betsy
