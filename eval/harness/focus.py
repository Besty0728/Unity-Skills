"""Bring the evaluation editor to the front only when compilation stalls.

A Unity editor that is not the active macOS app has been seen to stall in isCompiling (memory
note 8090-recompile-needs-real-os-focus), but the S1 probe compiled a script in 7 s with Finder
frontmost, so focus is taken only when compile/import stays busy for stall_s. Only the
pid-targeted System Events form is used: `tell application "Unity"` cannot tell two open
editors apart and may raise the user's own.
"""
import json
import os
import subprocess
import threading
import time
import urllib.request


def activate(pid):
    if not pid or os.environ.get("UNITYSKILLS_EVAL_MANUAL_FOCUS") == "1":
        return False
    script = ('tell application "System Events" to set frontmost of '
              '(first process whose unix id is %d) to true' % int(pid))
    try:
        out = subprocess.run(["osascript", "-e", script], capture_output=True, text=True, timeout=15)
        return out.returncode == 0
    except Exception:
        return False


def _busy(base_url):
    try:
        with urllib.request.urlopen(base_url + "/health", timeout=5) as response:
            health = json.loads(response.read().decode("utf-8"))
    except Exception:
        return None          # reloading: unknown, not evidence of a stall by itself
    comp = health.get("compilation") or {}
    return bool(comp.get("isCompiling") or comp.get("isUpdating") or health.get("domainReloadPending"))


class FocusKeeper(object):
    """Activates the editor when it has been busy for stall_s; records each activation."""

    def __init__(self, instance, stall_s=20.0, poll_s=4.0):
        self.instance = instance
        self.stall_s = stall_s
        self.poll_s = poll_s
        self.activations = []
        self._stop = threading.Event()
        self._thread = None

    def _loop(self):
        busy_since = None
        while not self._stop.is_set():
            busy = _busy(self.instance.base_url)
            now = time.time()
            if busy:
                busy_since = busy_since or now
                if now - busy_since >= self.stall_s:
                    self.activations.append({"t": round(now, 1), "ok": activate(self.instance.pid)})
                    busy_since = now
            elif busy is False:
                busy_since = None
            self._stop.wait(self.poll_s)

    def __enter__(self):
        self._thread = threading.Thread(target=self._loop, name="focus-keeper", daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=30)
        return False

# Producer:Betsy
