"""Private verifier fetch (contract 5.9): URL / zip / pack-dir / private-dir sources, sha256 pinning, temp loading.

The manifest's sha256 is the only trust root. fetch() keeps the verified bytes in memory; loaded() writes them to a
private directory under tempfile.gettempdir() (never the project or the state directory) only while the module is
imported and used, then deletes it in `finally`. cleanup_stale() removes what a killed runner left behind.
"""
import contextlib
import errno
import http.client
import json
import linecache
import os
import re
import secrets
import shutil
import socket
import ssl
import stat
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import zlib

from . import package_version, store
from .pack import LOOPBACK_HOSTS, MAX_FILE_BYTES, is_semver, load_source_module, read_file, template_error

TEMP_PREFIX = "usk-eval-"
MODULE_PREFIX = "usk_eval_private_"
OVERRIDE_ENV = "USK_EVAL_PRIVATE_URLS"
STALE_AFTER_S = 3600.0
TIMEOUT_S = 15.0
SLACK_BYTES = 1024
CHUNK_BYTES = 65536
MAX_REDIRECTS = 5
# Failures after which the rest of that host's URLs are not requested at all (and not recorded: an attempt is a request
# that was made). A reset or any other OSError may be transient: not here.
HOST_ERRORS = ("timeout", "dns_failed", "connection_refused", "tls_error")
TLS_HINT = ("TLS certificate verification failed. Install the root certificates for your Python (macOS python.org "
            "builds: run \"Install Certificates.command\" from the Python folder in Applications; behind a company "
            "proxy: set SSL_CERT_FILE to your CA bundle), or download the private pack zip from the release page and "
            "pass --private-zip <zip>.")

_TEMP_NAME = re.compile(r"^usk-eval-[a-z0-9_]{8}\Z")
_UNSAFE = re.compile(r"\W")


class PrivateUnavailable(Exception):
    """No source delivered a verifier matching the manifest; the attempt waits for verification (contract 2.7).

    `attempts` is [{via, error}] with short error codes (http 404, timeout, hash_mismatch, ...) and never content.
    """
    code = "VERIFICATION_PENDING"

    def __init__(self, attempts):
        self.attempts = [dict(item) for item in attempts]
        Exception.__init__(self, "no verifier source matched: %s" % (
            ", ".join(str(item.get("error")) for item in self.attempts) or "no source"))

    @property
    def hint(self):
        """Extra advice for the user when the failures point at something they can fix (see fix_hint), else None."""
        return fix_hint(self.attempts)


class PrivateLoadError(PrivateUnavailable):
    """loaded() could not prepare the verifier because the machine failed: no temp folder, disk full, not writable.

    Raised from loaded() before the body runs and before any verifier code is imported, so the attempt can wait and be
    retried like a failed download: same `code` and `attempts` ([{via: temp root, error: temp_dir_failed |
    temp_write_failed}]) as PrivateUnavailable. `error` is that short code; `cause` names the errno (ENOSPC, EACCES,
    ...) or exception type. Nothing derived from the verifier or from OS message text is ever stored.
    """

    def __init__(self, error, via=None, cause=None):
        PrivateUnavailable.__init__(self, [{"via": via, "error": error}])
        self.error = error
        self.cause = cause
        Exception.__init__(self, "the verifier could not be prepared: %s%s" % (error, " (%s)" % cause if cause else ""))


class VerifierImportError(AttributeError):
    """Raised by every attribute read on the stand-in for a verifier whose own code failed to import.

    That is a verifier problem (verifier_error, the attempt is scored invalid), not an environment problem.
    """


class _TooLarge(Exception):
    pass


class _RedirectBlocked(Exception):
    pass


class Fetched(object):
    """A verifier held in memory after the pin check.

    kind: url | zip | pack-dir | private-dir; sha256: digest of the bytes that run; via: URL or file it came from;
    official: False for pack-dir and for a private-dir file that differs from the manifest; attempts: [{via, error}]
    of failed tries (and a tolerated mismatch); deleted: True once loaded() removed every copy.
    """

    def __init__(self, kind, file, sha256, via, official, attempts, data):
        self.kind = kind
        self.file = file
        self.sha256 = sha256
        self.via = via
        self.official = official
        self.attempts = list(attempts)
        self.data = data
        self.deleted = False

    def discard(self):
        self.data = None
        self.deleted = True

    def __repr__(self):
        return "<Fetched %s %s %s via %s>" % (self.kind, self.file, (self.sha256 or "")[:12], self.via)


# ---------------------------------------------------------------- source selection


def fix_hint(attempts):
    """Advice for the user when the failed attempts show a cause they can fix themselves (TLS_HINT), else None."""
    if any(item.get("error") == "tls_error" for item in attempts or []):
        return TLS_HINT
    return None


def _agent(user_agent):
    if user_agent:
        return user_agent
    try:
        return "unity-skills-eval/%s" % package_version()
    except Exception:
        return "unity-skills-eval"


def _source(pack, private_zip, private_dir):
    """(kind, path) of the verifier source: an explicit zip/dir wins over what the pack was opened with."""
    if private_zip:
        return "zip", os.path.abspath(private_zip)
    if private_dir:
        return "private-dir", os.path.abspath(private_dir)
    return pack.private_kind, pack.private_path


def _templates(pack, environ):
    """(templates, error, loopback_http): the manifest's templates, or the USK_EVAL_PRIVATE_URLS test override."""
    raw = (os.environ if environ is None else environ).get(OVERRIDE_ENV)
    if raw:
        try:
            templates = json.loads(raw)
        except ValueError:
            return [], "bad_url_override", True
        override = True
        if not isinstance(templates, list) or not templates:
            return [], "bad_url_override", True
    else:
        templates, override = list((pack.manifest.get("private") or {}).get("urlTemplates") or []), False
    if any(template_error(item, allow_loopback_http=override) for item in templates):
        return [], "bad_url_override" if override else "bad_url_template", override
    return templates, None, override


def _plan(pack, entry, plugin_version, environ):
    """([(ref, url)], error, loopback_http): every template of one ref before the next ref, duplicates dropped."""
    templates, error, loopback_http = _templates(pack, environ)
    if error:
        return [], error, loopback_http
    if not plugin_version:
        try:
            plugin_version = package_version()
        except Exception:
            plugin_version = None
    out, seen = [], set()
    for ref in (pack.manifest.get("private") or {}).get("refs") or []:
        if "{plugin}" in ref:
            if not is_semver(plugin_version):
                continue
            ref = ref.replace("{plugin}", plugin_version)
        for template in templates:
            url = template.replace("{ref}", urllib.parse.quote(ref, safe="/")).replace(
                "{file}", urllib.parse.quote(entry["file"]))
            if url not in seen:
                seen.add(url)
                out.append((ref, url))
    return out, None, loopback_http


def url_candidates(pack, task_id, plugin_version=None, environ=None):
    """[(ref, url)] in the order fetch() tries them (contract 5.9); empty when the templates are unusable."""
    return _plan(pack, pack.private_entry(task_id), plugin_version, environ)[0]


# ---------------------------------------------------------------- HTTP


class _Redirects(urllib.request.HTTPRedirectHandler):
    """Follows redirects only to https (or loopback http for the test hook) hosts that appear in the templates."""
    max_redirections = MAX_REDIRECTS

    def __init__(self, hosts, loopback_http):
        urllib.request.HTTPRedirectHandler.__init__(self)
        self.hosts = hosts
        self.loopback_http = loopback_http

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        parts = urllib.parse.urlsplit(newurl)
        host = (parts.hostname or "").lower()
        secure = parts.scheme == "https" or (parts.scheme == "http" and self.loopback_http and host in LOOPBACK_HOSTS)
        if not secure or host not in self.hosts:
            raise _RedirectBlocked()
        return urllib.request.HTTPRedirectHandler.redirect_request(self, req, fp, code, msg, headers, newurl)


def _opener(url, hosts, loopback_http):
    """Honors HTTPS_PROXY / NO_PROXY like urllib does by default; loopback hosts are never proxied."""
    host = (urllib.parse.urlsplit(url).hostname or "").lower()
    proxies = urllib.request.ProxyHandler({}) if host in LOOPBACK_HOSTS else urllib.request.ProxyHandler()
    return urllib.request.build_opener(proxies, _Redirects(hosts, loopback_http))


def _classify(exc):
    """Short error code of a failed request.

    tls_error is certificate verification only (ssl.SSLCertVerificationError, also what a hostname mismatch raises),
    because that is the failure the "install root certificates" advice fixes. Any other ssl.SSLError (an EOF in the
    middle of the handshake, a protocol or cipher mismatch) is a network_error: it must neither kill the host's other
    URLs for good nor send the user off to install certificates.
    """
    reason = exc.reason if isinstance(exc, urllib.error.URLError) else exc
    if isinstance(reason, (socket.timeout, TimeoutError)):
        return "timeout"
    if isinstance(reason, ssl.SSLCertVerificationError):
        return "tls_error"
    if isinstance(reason, socket.gaierror):
        return "dns_failed"
    if isinstance(reason, ConnectionRefusedError):
        return "connection_refused"
    if isinstance(reason, (str, ValueError, UnicodeError, http.client.InvalidURL)):
        return "bad_url"
    return "network_error"


def _download(url, limit, user_agent, timeout, hosts, loopback_http):
    """(bytes, None) or (None, error code); never keeps or reports response content beyond the bytes returned."""
    deadline = time.monotonic() + timeout * 4
    try:
        request = urllib.request.Request(url, headers={"User-Agent": user_agent, "Accept": "*/*",
                                                       "Accept-Encoding": "identity"})
        with _opener(url, hosts, loopback_http).open(request, timeout=timeout) as response:
            length = (response.headers.get("Content-Length") or "").strip()
            if length.isdigit() and int(length) > limit:
                raise _TooLarge()
            chunks, total = [], 0
            while True:
                chunk = response.read(CHUNK_BYTES)
                if not chunk:
                    return b"".join(chunks), None
                total += len(chunk)
                if total > limit:
                    raise _TooLarge()
                chunks.append(chunk)
                if time.monotonic() > deadline:
                    raise socket.timeout("download deadline")
    except urllib.error.HTTPError as exc:
        exc.close()
        return None, "http %d" % exc.code
    except _TooLarge:
        return None, "too_large"
    except _RedirectBlocked:
        return None, "redirect_blocked"
    except Exception as exc:
        return None, _classify(exc)


def _from_urls(pack, entry, plugin_version, user_agent, environ, timeout):
    candidates, error, loopback_http = _plan(pack, entry, plugin_version, environ)
    if error or not candidates:
        raise PrivateUnavailable([{"via": None, "error": error or "no_url_candidates"}])
    hosts = set((urllib.parse.urlsplit(url).hostname or "").lower() for _ref, url in candidates)
    limit = entry["bytes"] + SLACK_BYTES
    attempts, dead = [], set()
    for _ref, url in candidates:
        host = (urllib.parse.urlsplit(url).hostname or "").lower()
        if host in dead:
            continue                      # not requested, so not an attempt; the host's own failure is recorded above
        data, failure = _download(url, limit, user_agent, timeout, hosts, loopback_http)
        if failure:
            attempts.append({"via": url, "error": failure})
            if failure in HOST_ERRORS:
                dead.add(host)
        elif store.sha256_bytes(data) != entry["sha256"]:
            attempts.append({"via": url, "error": "hash_mismatch"})
        else:
            return Fetched("url", entry["file"], entry["sha256"], url, True, attempts, data)
    raise PrivateUnavailable(attempts)


# ---------------------------------------------------------------- zip, folders


def _read_zip(path, entry):
    """(bytes, None) or (None, error): the entry named like the manifest file at the zip root."""
    limit = entry["bytes"] + SLACK_BYTES
    try:
        archive = zipfile.ZipFile(path)
    except zipfile.BadZipFile:
        return None, "bad_zip"
    except OSError:
        return None, "zip_not_found"
    with archive:
        try:
            info = archive.getinfo(entry["file"])
        except KeyError:
            return None, "entry_missing"
        if info.is_dir():
            return None, "entry_missing"
        if info.file_size > limit:
            return None, "too_large"
        try:
            with archive.open(info) as handle:
                data = handle.read(limit + 1)
        except (RuntimeError, NotImplementedError, EOFError, OSError, zlib.error, zipfile.BadZipFile):
            return None, "bad_zip"
    return (None, "too_large") if len(data) > limit else (data, None)


def _read_local(path):
    if not os.path.isfile(path):
        return None, "not_found"
    try:
        return read_file(path, MAX_FILE_BYTES), None
    except ValueError:
        return None, "too_large"
    except OSError:
        return None, "read_error"


def _from_zip(path, entry):
    if not path:
        raise PrivateUnavailable([{"via": None, "error": "no_source_path"}])
    data, error = _read_zip(path, entry)
    if error:
        raise PrivateUnavailable([{"via": path, "error": error}])
    if store.sha256_bytes(data) != entry["sha256"]:
        raise PrivateUnavailable([{"via": path, "error": "hash_mismatch"}])
    return Fetched("zip", entry["file"], entry["sha256"], path, True, [], data)


def _from_folder(kind, path, entry):
    """private-dir: DIR/<file>; pack-dir: DIR/private/<file>. A hash mismatch is tolerated but never official."""
    if not path:
        raise PrivateUnavailable([{"via": None, "error": "no_source_path"}])
    target = os.path.join(path, "private", entry["file"]) if kind == "pack-dir" else os.path.join(path, entry["file"])
    data, error = _read_local(target)
    if error:
        raise PrivateUnavailable([{"via": target, "error": error}])
    digest = store.sha256_bytes(data)
    match = digest == entry["sha256"]
    return Fetched(kind, entry["file"], digest, target, match and kind == "private-dir",
                   [] if match else [{"via": target, "error": "hash_mismatch_allowed"}], data)


# ---------------------------------------------------------------- fetch, probe


def fetch(pack, task_id, plugin_version=None, user_agent=None, private_zip=None, private_dir=None, environ=None,
          timeout=None):
    """The task's verifier as a Fetched (contract 5.9) or PrivateUnavailable (code VERIFICATION_PENDING).

    private_zip / private_dir override the source the pack was opened with (submit --retry-verify). Nothing is
    written to disk. environ (default os.environ) is only read for the USK_EVAL_PRIVATE_URLS test hook.
    """
    entry = pack.private_entry(task_id)
    kind, path = _source(pack, private_zip, private_dir)
    if kind == "url":
        return _from_urls(pack, entry, plugin_version, _agent(user_agent), environ, TIMEOUT_S if timeout is None
                          else timeout)
    if kind == "zip":
        return _from_zip(path, entry)
    if kind in ("pack-dir", "private-dir"):
        return _from_folder(kind, path, entry)
    raise ValueError("unknown verifier source kind: %r" % (kind,))


def probe(pack, task_id, plugin_version=None, user_agent=None, private_zip=None, private_dir=None, environ=None,
          timeout=None):
    """check/start reachability probe: a full fetch and hash check of one task's verifier, then dropped.

    {kind, probedTask, reachable, via, attempts}; attempts (an additive field) lists the failed tries, and an unreachable
    result carries `hint` (also additive, only when there is one) with advice such as installing certificates.
    """
    kind = _source(pack, private_zip, private_dir)[0]
    try:
        fetched = fetch(pack, task_id, plugin_version, user_agent, private_zip, private_dir, environ, timeout)
    except PrivateUnavailable as exc:
        out = {"kind": kind, "probedTask": task_id, "reachable": False, "via": None, "attempts": exc.attempts}
        if exc.hint:
            out["hint"] = exc.hint
        return out
    out = {"kind": fetched.kind, "probedTask": task_id, "reachable": True, "via": fetched.via,
           "attempts": fetched.attempts}
    fetched.discard()
    return out


# ---------------------------------------------------------------- temp files and loading


def _writable(func, path, _error):
    """rmtree hook: clear read-only bits (Windows read-only files, POSIX read-only folders) and retry once."""
    for target in (path, os.path.dirname(path)):
        try:
            os.chmod(target, stat.S_IRWXU)
        except OSError:
            pass
    func(path)


def _rmtree(path):
    if sys.version_info >= (3, 12):
        shutil.rmtree(path, onexc=_writable)
    else:
        shutil.rmtree(path, onerror=_writable)


def remove_tree(path, attempts=5, delay=0.1):
    """Deletes a directory tree, retrying while Windows antivirus or a reader still holds it; True when it is gone."""
    for _ in range(attempts):
        if not os.path.lexists(path):
            return True
        try:
            _rmtree(path)
        except OSError:
            pass
        if not os.path.lexists(path):
            return True
        time.sleep(delay)
    return not os.path.lexists(path)


def _temp_root():
    try:
        return tempfile.gettempdir()
    except Exception:
        return None


def _cause(exc):
    """errno name (ENOSPC, EACCES, ...) or the exception type; never a message that could carry file names."""
    return errno.errorcode.get(getattr(exc, "errno", None)) or type(exc).__name__


def _load_reason(exc):
    line = getattr(exc, "lineno", None)
    return "%s (line %s)" % (type(exc).__name__, line) if isinstance(exc, SyntaxError) and line else type(exc).__name__


class _BrokenVerifier(object):
    """Stands in for a verifier that failed to import: every attribute read raises VerifierImportError.

    VerifierImportError is an AttributeError, so getattr(module, name, default) still falls back to the default while
    module.ASSERTIONS / module.verify raise inside the caller's try block (verifier_error). The message carries the
    exception type only, never text from the private source.
    """

    def __init__(self, file, reason):
        self.__dict__["_message"] = "verifier %s failed to load: %s" % (file, reason)

    def __getattr__(self, name):
        raise VerifierImportError(self.__dict__["_message"])


@contextlib.contextmanager
def loaded(fetched, attempt_id):
    """Context manager yielding the verifier module (contract 5.9).

    The verified bytes go to <tempdir>/usk-eval-XXXXXXXX/<12hex>.py and run as usk_eval_private_<attempt>_<6hex> via
    importlib.util.spec_from_file_location. On exit - normal, on a verifier exception or on a failed import - the module
    leaves sys.modules, the directory is removed and fetched.deleted tells whether that worked.

    Two different failures, kept apart on purpose:
    - the machine cannot hold the file (no temp folder, disk full): PrivateLoadError is raised from loaded() itself,
      before the body, and the attempt can wait and retry like a failed download;
    - the verifier's own code fails to import: the body gets a stand-in whose attributes raise VerifierImportError, so
      the caller's verify try-block records verifier_error (invalid).
    """
    if fetched.data is None:
        raise RuntimeError("the verifier %s was already deleted" % fetched.file)
    name = "%s%s_%s" % (MODULE_PREFIX, _UNSAFE.sub("_", str(attempt_id)), secrets.token_hex(3))
    directory = path = None
    try:
        try:
            directory = tempfile.mkdtemp(prefix=TEMP_PREFIX)
        except OSError as exc:
            raise PrivateLoadError("temp_dir_failed", _temp_root(), _cause(exc))
        path = os.path.join(directory, secrets.token_hex(6) + ".py")
        try:
            with open(path, "xb") as handle:
                handle.write(fetched.data)
        except OSError as exc:
            raise PrivateLoadError("temp_write_failed", _temp_root(), _cause(exc))
        try:
            module = load_source_module(name, path, fetched.data)
        except (Exception, SystemExit) as exc:
            module = _BrokenVerifier(fetched.file, _load_reason(exc))
        yield module
    finally:
        sys.modules.pop(name, None)
        if path:
            linecache.cache.pop(path, None)
        fetched.data = None
        fetched.deleted = remove_tree(directory) if directory else True


def cleanup_stale(max_age_s=STALE_AFTER_S, temp_root=None):
    """Removes usk-eval-* verifier folders older than max_age_s (default 1 h) from the temp root; returns the paths.

    Only folders named like tempfile.mkdtemp made them, owned by this user, holding nothing but .py files and
    __pycache__ are touched.
    """
    root = temp_root or tempfile.gettempdir()
    removed = []
    try:
        names = sorted(os.listdir(root))
    except OSError:
        return removed
    cutoff = time.time() - max_age_s
    for name in names:
        path = os.path.join(root, name)
        if not _TEMP_NAME.match(name):
            continue
        try:
            info = os.lstat(path)
            content = os.listdir(path) if stat.S_ISDIR(info.st_mode) else None
        except OSError:
            continue
        if content is None or info.st_mtime > cutoff:
            continue
        if hasattr(os, "getuid") and info.st_uid != os.getuid():
            continue
        if all(item.endswith(".py") or item == "__pycache__" for item in content) and remove_tree(path):
            removed.append(path)
    return removed

# Producer:Betsy
