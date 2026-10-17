"""State storage: atomic writes, schema-checked reads, JSONL append, UTC timestamps (contract 0, 4.2)."""
import datetime
import hashlib
import json
import os
import re
import secrets
import time

SCHEMA = 1
UTC = datetime.timezone.utc

_TS_RE = re.compile(r"^(\d{4})-(\d{2})-(\d{2})[Tt ](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?([Zz]|[+-]\d{2}:?\d{2})$")
_REPLACE_RETRIES = 5
_REPLACE_DELAY_S = 0.1


class StoreError(Exception):
    """code is a problem code: STATE_CORRUPT, SCHEMA_UNSUPPORTED or IO_ERROR."""

    def __init__(self, code, path, detail=""):
        Exception.__init__(self, "%s %s %s" % (code, path, detail))
        self.code = code
        self.path = path
        self.detail = detail


# ---------------------------------------------------------------- time


def now():
    return datetime.datetime.now(UTC)


def iso(moment):
    moment = moment.astimezone(UTC)
    return "%s.%03dZ" % (moment.strftime("%Y-%m-%dT%H:%M:%S"), moment.microsecond // 1000)


def now_iso():
    return iso(now())


def parse_utc(text):
    """Aware UTC datetime from `Z` or `+HH:MM` timestamps with 0-9 fraction digits (truncated to 6)."""
    match = _TS_RE.match(text.strip()) if isinstance(text, str) else None
    if not match:
        raise ValueError("not a UTC timestamp: %r" % (text,))
    year, month, day, hour, minute, second, fraction, zone = match.groups()
    micro = int((fraction or "0")[:6].ljust(6, "0"))
    if zone in ("Z", "z"):
        offset = datetime.timedelta(0)
    else:
        digits = zone[1:].replace(":", "")
        offset = datetime.timedelta(hours=int(digits[:2]), minutes=int(digits[2:]))
        if zone[0] == "-":
            offset = -offset
    moment = datetime.datetime(int(year), int(month), int(day), int(hour), int(minute), int(second), micro,
                               tzinfo=datetime.timezone(offset))
    return moment.astimezone(UTC)


def epoch(text):
    return parse_utc(text).timestamp()


def seconds_between(start_iso, end_iso):
    if not start_iso or not end_iso:
        return None
    return round(epoch(end_iso) - epoch(start_iso), 1)


def stamp_id(prefix):
    """<prefix>YYYYMMDDTHHMMSS<6hex> in UTC (sessionId, runId)."""
    return "%s%s%s" % (prefix, now().strftime("%Y%m%dT%H%M%S"), secrets.token_hex(3))


def new_attempt_id():
    return secrets.token_hex(6)


# ---------------------------------------------------------------- hashing


def dumps(obj):
    return json.dumps(obj, ensure_ascii=False, allow_nan=False, indent=1)


def canonical_digest(obj):
    data = json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(data.encode("utf-8")).hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_text(text):
    return sha256_bytes(text.encode("utf-8"))


# ---------------------------------------------------------------- writes


def _replace(src, dst):
    for attempt in range(_REPLACE_RETRIES + 1):
        try:
            os.replace(src, dst)
            return
        except PermissionError:   # Windows: antivirus or a reader holding the target
            if attempt == _REPLACE_RETRIES:
                raise
            time.sleep(_REPLACE_DELAY_S)


def atomic_write_bytes(path, data):
    directory = os.path.dirname(path)
    tmp = os.path.join(directory, "%s.tmp-%d-%s" % (os.path.basename(path), os.getpid(), secrets.token_hex(3)))
    try:
        os.makedirs(directory, exist_ok=True)
        with open(tmp, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        _replace(tmp, path)
    except OSError as exc:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise StoreError("IO_ERROR", path, exc.strerror or str(exc))


def atomic_write_json(path, obj):
    atomic_write_bytes(path, (dumps(obj) + "\n").encode("utf-8"))


def atomic_write_text(path, text, bom=False):
    atomic_write_bytes(path, (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))


def append_jsonl(path, obj):
    """One write per complete line, so concurrent readers only ever see whole lines or a torn tail."""
    line = json.dumps(obj, ensure_ascii=False, allow_nan=False, separators=(",", ":")) + "\n"
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "a", encoding="utf-8", newline="\n") as handle:
            handle.write(line)
    except OSError as exc:
        raise StoreError("IO_ERROR", path, exc.strerror or str(exc))


def append_text(path, text):
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "a", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
    except OSError as exc:
        raise StoreError("IO_ERROR", path, exc.strerror or str(exc))


# ---------------------------------------------------------------- reads


def check_schema(obj, path):
    schema = obj.get("schema") if isinstance(obj, dict) else None
    if type(schema) is not int:
        raise StoreError("STATE_CORRUPT", path, "missing or non-integer schema")
    if schema > SCHEMA:
        raise StoreError("SCHEMA_UNSUPPORTED", path, "schema %d" % schema)
    return obj


def read_json(path, missing_ok=False):
    """Parsed state file with a validated schema; None when missing and missing_ok."""
    try:
        with open(path, "rb") as handle:
            raw = handle.read()
    except FileNotFoundError:
        if missing_ok:
            return None
        raise StoreError("IO_ERROR", path, "file not found")
    except OSError as exc:
        raise StoreError("IO_ERROR", path, exc.strerror or str(exc))
    try:
        obj = json.loads(raw.decode("utf-8-sig"))
    except ValueError:
        raise StoreError("STATE_CORRUPT", path, "not valid JSON")
    return check_schema(obj, path)


def read_jsonl(path):
    """Complete, parseable lines of a JSONL file; a torn trailing line is ignored."""
    try:
        with open(path, "rb") as handle:
            raw = handle.read()
    except FileNotFoundError:
        return []
    except OSError as exc:
        raise StoreError("IO_ERROR", path, exc.strerror or str(exc))
    out = []
    lines = raw.split(b"\n")
    for line in lines[:-1]:          # the last element has no newline: torn or empty
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line.decode("utf-8-sig"))
        except ValueError:
            continue
        if isinstance(obj, dict):
            out.append(obj)
    return out


class RunLog(object):
    """runner.log lines `<iso> pid=<pid> cmd=<command> <LEVEL> <message>`; continuation lines indented 4 spaces.

    Never pass answers or host-log content here (contract 4.1).
    """

    def __init__(self, command, primary=None, extra=None):
        self.command = command or "-"
        self.primary = primary
        self.extra = extra

    def _write(self, level, message):
        lines = str(message).replace("\r\n", "\n").rstrip("\n").split("\n")
        text = "%s pid=%d cmd=%s %s %s\n" % (now_iso(), os.getpid(), self.command, level, lines[0])
        text += "".join("    %s\n" % line for line in lines[1:])
        for path in (self.primary, self.extra):
            if not path:
                continue
            try:
                append_text(path, text)
            except StoreError:
                pass

    def info(self, message):
        self._write("INFO", message)

    def warn(self, message):
        self._write("WARN", message)

    def error(self, message):
        self._write("ERROR", message)

    @property
    def path(self):
        return self.primary or self.extra or ""

# Producer:Betsy
