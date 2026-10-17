"""Weighted assertions of one attempt (contract 4.6, 5.5, 5.8; ported from v1 eval/harness/verify.py).

A spec row is (id, weight, critical, [reqIds], desc). Weights sum to 10; every assertion names the prompt requirements
it checks (R<n> task requirements, G<n> general rules), so a failed assertion reads back as failed requirements.
"""
import math
import re

REQ_RE = re.compile(r"^[RG][1-9][0-9]*$")
_MAX_DEPTH = 12


def _plain(value, depth=0):
    """JSON-safe copy of whatever a verifier recorded: tuples and sets become lists, non-finite floats and unknown
    objects become text, so one odd value cannot make the result file unwritable."""
    if value is None or isinstance(value, (bool, int, str)):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else repr(value)
    if depth >= _MAX_DEPTH:
        return str(value)
    if isinstance(value, dict):
        return {str(key): _plain(item, depth + 1) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_plain(item, depth + 1) for item in value]
    if isinstance(value, (set, frozenset)):
        return sorted((_plain(item, depth + 1) for item in value), key=repr)
    return str(value)


def _clamp(value):
    """0..1; NaN is a failure, never a pass."""
    value = float(value)
    if value != value:
        return 0.0
    return max(0.0, min(1.0, value))


def _spec_row(row):
    if isinstance(row, (str, bytes)) or not isinstance(row, (list, tuple)) or len(row) != 5:
        raise ValueError("assertion spec must be (id, weight, critical, [reqIds], desc), got %r" % (row,))
    aid, weight, critical, reqs, desc = row
    if not isinstance(aid, str) or not aid:
        raise ValueError("assertion id must be a non-empty string: %r" % (aid,))
    if isinstance(weight, bool) or not isinstance(weight, (int, float)) or not math.isfinite(weight) or weight < 0:
        raise ValueError("assertion %s: weight must be a finite number >= 0, got %r" % (aid, weight))
    if isinstance(reqs, (str, bytes)) or not isinstance(reqs, (list, tuple)) or not reqs:
        raise ValueError("assertion %s: reqIds must be a non-empty list" % aid)
    bad = [r for r in reqs if not isinstance(r, str) or not REQ_RE.match(r)]
    if bad:
        raise ValueError("assertion %s: reqIds must look like R1 or G2, got %r" % (aid, bad))
    return aid, {"weight": float(weight), "critical": bool(critical), "req": list(dict.fromkeys(reqs)),
                 "desc": str(desc)}


class Scorecard(object):
    def __init__(self, spec):
        self.spec = {}
        self.order = []
        for row in spec:
            aid, meta = _spec_row(row)
            if aid in self.spec:
                raise ValueError("duplicate assertion %s" % aid)
            self.spec[aid] = meta
            self.order.append(aid)
        total = sum(v["weight"] for v in self.spec.values())
        if not abs(total - 10.0) <= 1e-6:
            raise ValueError("assertion weights sum to %s, expected 10" % total)
        self.results = {}

    def check(self, aid, passed, expected=None, actual=None, note=None, **evidence):
        return self.fraction(aid, 1.0 if passed else 0.0, expected, actual, note, **evidence)

    def fraction(self, aid, value, expected=None, actual=None, note=None, **evidence):
        """Partial credit: weight * value; only value == 1 counts as passed for strict."""
        if aid not in self.spec:
            raise KeyError("unknown assertion %s" % aid)
        value = _clamp(value)
        shown = round(value, 4)
        if value < 1.0 <= shown:
            shown = 0.9999                  # keeps `pass` and `fraction == 1` in step after rounding
        self.results[aid] = {"pass": value >= 1.0, "fraction": shown, "expected": _plain(expected),
                             "actual": _plain(actual), "note": _plain(note), "evidence": _plain(evidence) or None}
        return value >= 1.0

    def unobservable(self, aid, reason):
        return self.check(aid, False, note="unobservable: %s" % reason, unobservable=True)

    def finish(self):
        for aid in self.order:
            if aid not in self.results:
                self.unobservable(aid, "verifier did not evaluate this assertion")
        return self

    @property
    def score(self):
        return round(sum(self.spec[a]["weight"] * r["fraction"] for a, r in self.results.items()), 4)

    @property
    def strict(self):
        return len(self.results) == len(self.spec) and all(r["pass"] for r in self.results.values())

    @property
    def critical_failed(self):
        return [a for a in self.failed() if self.spec[a]["critical"]]

    def failed(self):
        """Assertions that did not pass, in spec order; one not evaluated yet counts as failed."""
        return [a for a in self.order if not self.results.get(a, {}).get("pass")]

    def requirements_failed(self):
        """Sorted union of the requirements named by failed assertions."""
        return sorted({req for aid in self.failed() for req in self.spec[aid]["req"]})

    def as_dict(self):
        """result.json `score` (contract 4.6); `hint` is filled in by the runner from the private module."""
        rows = []
        for aid in self.order:
            meta = self.spec[aid]
            res = self.results.get(aid) or {"pass": False, "fraction": 0.0, "expected": None, "actual": None,
                                            "note": None, "evidence": None}
            rows.append({"id": aid, "req": list(meta["req"]), "weight": meta["weight"], "critical": meta["critical"],
                         "desc": meta["desc"], "pass": res["pass"], "fraction": res["fraction"],
                         "expected": res["expected"], "actual": res["actual"], "note": res["note"],
                         "evidence": res["evidence"], "hint": None})
        return {"score": self.score, "completion": round(self.score * 10, 2), "strict": self.strict,
                "criticalFailed": self.critical_failed, "assertions": rows}

# Producer:Betsy
