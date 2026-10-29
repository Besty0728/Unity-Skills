"""Domain reload cost of a long-running editor, read from that editor's own log (maintainer tool, not a gate input).

Unity 2022.3's domain reload gets slower with every reload one editor process has done, plugin or not (reload1027:
0.8 s when fresh, 7 s after 250 pure reloads; a 14-hour gate reached 26 s and timed Play Mode cases out); only a
restart resets it. Gate drivers open each editor with its own -logFile and run this between tasks:

    python3 -B eval/tools/reload_guard.py <editor log> [--threshold-ms 3000] [--recent 3]

Prints one JSON line: reloads in the log, the most recent totals and their median, in ms. Exit 0 below the threshold,
3 when the median reaches it (restart the editor before the next task), 1 when the log is missing or has no reload yet.
"""
import argparse
import collections
import json
import re
import statistics
import sys

PROFILE = re.compile(r"^Domain Reload Profiling: (\d+)ms")
DEFAULT_THRESHOLD_MS = 3000
DEFAULT_RECENT = 3


def scan(path, recent=DEFAULT_RECENT):
    """The number of reloads in the log and the totals of the last `recent` of them."""
    count, last = 0, collections.deque(maxlen=recent)
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = PROFILE.match(line)
            if m:
                count += 1
                last.append(int(m.group(1)))
    return count, list(last)


def verdict(path, threshold_ms=DEFAULT_THRESHOLD_MS, recent=DEFAULT_RECENT):
    """(report, exit code)."""
    try:
        count, last = scan(path, recent)
    except OSError as exc:
        return {"log": path, "error": str(exc)}, 1
    if not last:
        return {"log": path, "reloads": 0}, 1
    median = statistics.median(last)
    restart = median >= threshold_ms
    return {"log": path, "reloads": count, "recentMs": last, "medianMs": median, "thresholdMs": threshold_ms,
            "restart": restart}, 3 if restart else 0


def main(argv=None):
    p = argparse.ArgumentParser(description="Tells a gate driver when an editor's domain reloads have become too slow.")
    p.add_argument("log", help="the editor's own log file (its -logFile)")
    p.add_argument("--threshold-ms", type=int, default=DEFAULT_THRESHOLD_MS)
    p.add_argument("--recent", type=int, default=DEFAULT_RECENT, help="how many of the last reloads the median covers")
    args = p.parse_args(argv)
    if args.recent < 1:
        p.error("--recent must be at least 1")
    report, code = verdict(args.log, args.threshold_ms, args.recent)
    print(json.dumps(report))
    return code


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
