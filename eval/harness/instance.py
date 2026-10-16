"""Locate the live UnitySkills editor for a project and prove it is the right one.

Ports are not stable (8090-8100 are handed out in start order), so the registry is looked up by
project path and the answering editor must report the instanceId that RegistryService derives
from productName + project path (RegistryService.cs: InstanceId / ComputeStableHash).
"""
import hashlib
import json
import os
import re
import time
import urllib.error
import urllib.request

REGISTRY_PATH = os.path.expanduser("~/.unity_skills/registry.json")


class InstanceError(RuntimeError):
    pass


def expected_instance_id(product_name, project_path):
    clean = re.sub(r"[^A-Za-z0-9]", "", product_name or "")
    digest = hashlib.sha256(project_path.encode("utf-8")).digest()[:4]
    return "%s_%s" % (clean, digest.hex().upper())


def product_name_from_disk(project_path):
    settings = os.path.join(project_path, "ProjectSettings", "ProjectSettings.asset")
    with open(settings, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = re.match(r"\s*productName:\s*(.*)$", line)
            if match:
                return match.group(1).strip()
    raise InstanceError("productName not found in %s" % settings)


def read_registry(path=REGISTRY_PATH):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def registry_entry(project_path, registry=None):
    registry = read_registry() if registry is None else registry
    wanted = os.path.realpath(project_path)
    for key, entry in registry.items():
        if not isinstance(entry, dict):
            continue
        candidate = entry.get("path") or key
        if os.path.realpath(candidate) == wanted:
            return entry
    return None


class Instance(object):
    def __init__(self, project_path, instance_id, port, pid, product_name):
        self.project_path = project_path
        self.instance_id = instance_id
        self.port = port
        self.pid = pid
        self.product_name = product_name

    @property
    def base_url(self):
        return "http://127.0.0.1:%d" % self.port

    def as_dict(self):
        return {"projectPath": self.project_path, "instanceId": self.instance_id, "port": self.port,
                "pid": self.pid, "productName": self.product_name}


def _get_health(port, timeout=10):
    with urllib.request.urlopen("http://127.0.0.1:%d/health" % port, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def locate(project_path, wait_s=0.0, poll_s=5.0):
    """Return the verified Instance serving project_path, waiting up to wait_s for it to appear."""
    project_path = os.path.realpath(project_path)
    product = product_name_from_disk(project_path)
    expected = expected_instance_id(product, project_path)
    deadline = time.time() + max(wait_s, 30.0)   # always ride out a short 429/503 burst
    last = "no registry entry"
    while True:
        entry = registry_entry(project_path)
        if entry and entry.get("port"):
            if entry.get("id") and entry["id"] != expected:
                raise InstanceError("registry id %s != expected %s" % (entry["id"], expected))
            try:
                health = _get_health(int(entry["port"]))
                if health.get("instanceId") == expected and health.get("status") == "ok":
                    return Instance(project_path, expected, int(entry["port"]), entry.get("pid"), product)
                last = "port %s answers instanceId=%s status=%s" % (
                    entry["port"], health.get("instanceId"), health.get("status"))
            except (urllib.error.URLError, OSError, ValueError) as exc:
                last = "port %s: %r" % (entry.get("port"), exc)
        if time.time() >= deadline or (last == "no registry entry" and wait_s == 0):
            raise InstanceError("instance %s for %s not ready: %s" % (expected, project_path, last))
        time.sleep(poll_s if wait_s else 1.0)

# Producer:Betsy
