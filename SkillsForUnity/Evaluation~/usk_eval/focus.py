"""Bring the evaluation editor to the front only when compilation stalls (macOS; ported from v1 focus.py).

A Unity editor that is not the active macOS app has been seen to stall in isCompiling, so focus is taken only when
compile/import stays busy for stall_s. Only the pid-targeted System Events form is used: `tell application "Unity"`
cannot tell two open editors apart. USK_EVAL_MANUAL_FOCUS=1 turns it off.
"""
import os
import subprocess
import sys
import threading
import time


def enabled(environ=None):
    environ = os.environ if environ is None else environ
    return sys.platform == "darwin" and environ.get("USK_EVAL_MANUAL_FOCUS") != "1"


def activate(pid):
    if not pid:
        return False
    script = ('tell application "System Events" to set frontmost of '
              '(first process whose unix id is %d) to true' % int(pid))
    try:
        out = subprocess.run(["osascript", "-e", script], capture_output=True, text=True, timeout=15)
        return out.returncode == 0
    except Exception:
        return False


def _busy(client):
    health = client.probe_health(timeout=5.0)
    if health is None:
        return None          # reloading: unknown, not evidence of a stall by itself
    comp = health.get("compilation") or {}
    return bool(comp.get("isCompiling") or comp.get("isUpdating") or health.get("domainReloadPending"))


class FocusKeeper(object):
    """Activates the editor when it has been busy for stall_s; records each activation."""

    def __init__(self, client, pid, stall_s=20.0, poll_s=4.0, environ=None):
        self.client = client
        self.pid = pid
        self.stall_s = stall_s
        self.poll_s = poll_s
        self.active = enabled(environ) and bool(pid)
        self.activations = []
        self._stop = threading.Event()
        self._thread = None

    def _loop(self):
        busy_since = None
        while not self._stop.is_set():
            busy = _busy(self.client)
            now = time.time()
            if busy:
                busy_since = busy_since or now
                if now - busy_since >= self.stall_s:
                    self.activations.append({"t": round(now, 1), "ok": activate(self.pid)})
                    busy_since = now
            elif busy is False:
                busy_since = None
            self._stop.wait(self.poll_s)

    def __enter__(self):
        if self.active:
            self._thread = threading.Thread(target=self._loop, name="focus-keeper", daemon=True)
            self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=30)
        return False

# Producer:Betsy
