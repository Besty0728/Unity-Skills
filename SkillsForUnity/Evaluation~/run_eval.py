"""UnitySkills evaluation runner entry point (contract 1.2, 2.3): python -B run_eval.py <command> [args]."""
import sys

sys.dont_write_bytecode = True

import json  # noqa: E402
import os  # noqa: E402
import time  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))


def _flag(argv, name):
    for index, token in enumerate(argv):
        if token == name and index + 1 < len(argv):
            return argv[index + 1]
        if token.startswith(name + "="):
            return token.split("=", 1)[1]
    return None


def _redirect_panel_streams(argv):
    """The GUI parent's stdio handles may be invalid, so panel runs send stdout/stderr to the log file."""
    target = _flag(argv, "--log-file")
    if not target and _flag(argv, "--project"):
        target = os.path.join(_flag(argv, "--project"), "Library", "UnitySkills", "eval", "runner.log")
    stream = None
    if target:
        try:
            os.makedirs(os.path.dirname(os.path.abspath(target)), exist_ok=True)
            stream = open(target, "a", encoding="utf-8", errors="replace", buffering=1)
        except OSError:
            stream = None
    if stream is None:
        stream = open(os.devnull, "w", encoding="utf-8")
    sys.stdout = sys.stderr = stream


def _envelope(argv, code, message, fix, exit_code):
    envelope = {"ok": False, "command": argv[0] if argv else None, "data": None,
                "problems": [{"code": code, "message": message, "fix": fix}], "next": None}
    result_file = _flag(argv, "--result-file")
    if _flag(argv, "--launcher") == "panel" and result_file:
        result = {"schema": 1}
        result.update(envelope)
        result.update({"exitCode": exit_code, "finishedAt": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime()),
                       "cmdId": _flag(argv, "--cmd-id")})
        try:
            with open(result_file, "w", encoding="ascii") as handle:
                handle.write(json.dumps(result, ensure_ascii=True, separators=(",", ":")))
        except OSError:
            pass
    else:
        sys.stdout.write(json.dumps(envelope, ensure_ascii=True, separators=(",", ":")) + "\n")
    return exit_code


def main():
    argv = sys.argv[1:]
    if _flag(argv, "--launcher") == "panel":
        _redirect_panel_streams(argv)
    if sys.version_info < (3, 9):
        return _envelope(argv, "PYTHON_TOO_OLD", "Python 3.9 or newer is required",
                         "Install Python 3.9 or newer and run the command with it.", 2)
    if HERE not in sys.path:
        sys.path.insert(0, HERE)
    try:
        from usk_eval import cli
    except Exception as exc:          # a damaged package must still answer with an envelope
        return _envelope(argv, "INTERNAL_ERROR", "Unexpected runner error: %s" % type(exc).__name__,
                         "Unexpected runner error; reinstall the UnitySkills package and report it.", 4)
    return cli.main(argv)


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
