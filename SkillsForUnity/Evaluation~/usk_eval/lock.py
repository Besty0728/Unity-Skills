"""Cross-platform command lock (contract 2.6): O_CREAT|O_EXCL file, stale takeover by rename."""
import glob
import json
import os
import secrets
import socket
import sys
import time

from . import store
from .problems import ProblemError, problem

ACQUIRE_TIMEOUT_S = 120.0
EMPTY_STALE_S = 10.0
MAX_AGE_S = 3600.0
STALE_FILE_TTL_S = 24 * 3600.0
POLL_S = 0.5


def pid_alive(pid):
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
        return False
    if sys.platform == "win32":
        return _win_pid_alive(pid)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _win_pid_alive(pid):
    # os.kill on Windows terminates the target for any signal but CTRL events, so ask the kernel instead.
    import ctypes
    from ctypes import wintypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
    kernel32.GetExitCodeProcess.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
    handle = kernel32.OpenProcess(0x1000, False, pid)        # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ctypes.get_last_error() == 5                  # ERROR_ACCESS_DENIED: exists, not ours
    try:
        code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return True
        return code.value == 259                             # STILL_ACTIVE
    finally:
        kernel32.CloseHandle(handle)


def _read(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            info = json.load(handle)
    except (OSError, ValueError):
        return None
    return info if isinstance(info, dict) else None


def _started(info):
    try:
        return store.epoch(info.get("startedAt"))
    except (ValueError, TypeError):
        return None


class Lock(object):
    def __init__(self, path, cmd, launcher, log=None, timeout_s=ACQUIRE_TIMEOUT_S, clock=time.time, sleep=time.sleep):
        self.path = path
        self.cmd = cmd
        self.launcher = launcher
        self.log = log
        self.timeout_s = timeout_s
        self.clock = clock
        self.sleep = sleep
        self.held = False
        self._content = None

    def _stale(self, info):
        if info is None:
            try:
                return self.clock() - os.path.getmtime(self.path) > EMPTY_STALE_S
            except OSError:
                return False
        if not pid_alive(info.get("pid")):
            return True
        started = _started(info)
        return started is not None and self.clock() - started > MAX_AGE_S

    def _take_over(self, info):
        """Move the stale lock aside; restore it if it changed after we judged it (someone took it first)."""
        aside = "%s.stale-%d-%s" % (self.path, os.getpid(), secrets.token_hex(3))
        try:
            os.rename(self.path, aside)
        except OSError:
            return
        moved = _read(aside)
        if info is not None and moved != info:
            self._restore(aside)
            return
        if self.log:
            self.log.warn("took over stale lock %s" % json.dumps(info, ensure_ascii=True))

    def _restore(self, aside):
        try:
            if sys.platform == "win32":
                os.rename(aside, self.path)                  # fails if a new lock exists: no clobbering
            else:
                os.link(aside, self.path)
                os.remove(aside)
        except OSError:
            pass

    def _sweep(self):
        for stale in glob.glob(glob.escape(self.path) + ".stale-*"):
            try:
                if self.clock() - os.path.getmtime(stale) > STALE_FILE_TTL_S:
                    os.remove(stale)
            except OSError:
                pass

    def acquire(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        self._sweep()
        deadline = self.clock() + self.timeout_s
        content = {"schema": 1, "pid": os.getpid(), "cmd": self.cmd, "launcher": self.launcher,
                   "startedAt": store.now_iso(), "host": socket.gethostname()}
        while True:
            try:
                fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
            except FileExistsError:
                info = _read(self.path)
                if self._stale(info):
                    self._take_over(info)
                    continue
                if self.clock() > deadline:
                    info = info or {}
                    raise ProblemError(problem("LOCK_TIMEOUT", [info.get("pid", "?"), info.get("cmd", "?")]))
                self.sleep(POLL_S)
                continue
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(content, handle, ensure_ascii=True)
                handle.flush()
                os.fsync(handle.fileno())
            self.held = True
            self._content = content
            return self

    def release(self):
        if not self.held:
            return
        self.held = False
        info = _read(self.path)
        if info and info.get("pid") == os.getpid() and info.get("startedAt") == self._content.get("startedAt"):
            try:
                os.remove(self.path)
            except OSError:
                pass

    def __enter__(self):
        return self.acquire()

    def __exit__(self, *exc):
        self.release()
        return False


def holder(path):
    """The lock content when the lock exists and its pid is alive, else None (for status displays)."""
    info = _read(path)
    return info if info and pid_alive(info.get("pid")) else None

# Producer:Betsy
