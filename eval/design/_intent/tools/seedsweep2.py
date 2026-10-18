#!/usr/bin/env python3
"""Seed sweep over a drafted task's own offline model (no Unity, no network).

usage: seedsweep2.py REPO_ROOT ID MODE SEED [SEED ...]
  MODE full  : reference, every negative, every alternative, untouched fixture, fingerprint repeat; legacy and 6000.4 ids
  MODE quick : reference + untouched fixture + fingerprint repeat; legacy ids only

The task's test module is found as runner.test_pack_<id> (or runner.test_pack_*  that mentions the id). Two styles are
supported:
  * a TestCase class with TASK_ID == ID and a run_for(seed=..., entity_mode=...) method (pack_offline.OpsTaskTests);
  * a module-level Offline class taking (test, seed, entity_mode) or (test, seed).
Exit 1 when anything deviates.
"""
import glob
import importlib
import inspect
import os
import sys
import time
import unittest

sys.dont_write_bytecode = True


def find_module(root, task_id):
    names = ["runner.test_pack_%s" % task_id.lower()]
    for path in sorted(glob.glob(os.path.join(root, "eval", "tests", "runner", "test_pack_*.py"))):
        name = "runner." + os.path.basename(path)[:-3]
        if name not in names:
            names.append(name)
    for name in names:
        try:
            module = importlib.import_module(name)
        except Exception:
            continue
        for _label, value in vars(module).items():
            if inspect.isclass(value) and issubclass(value, unittest.TestCase) and getattr(value, "TASK_ID", None) == task_id:
                return module, "mixin", value
        if name == names[0] and hasattr(module, "Offline"):
            return module, "offline", module.Offline
    raise SystemExit("no usable offline test style found for %s" % task_id)


def make_factory(style, cls, holder):
    if style == "mixin":
        instance = cls("test_one_seed_one_fingerprint")
        instance.__dict__["_sweep_holder"] = holder

        def fresh(seed, ids):
            return instance.run_for(seed=seed, entity_mode=ids)

        return fresh, instance
    params = list(inspect.signature(cls.__init__).parameters)[2:]

    def fresh(seed, ids):
        return cls(holder, seed, ids) if len(params) >= 2 else cls(holder, seed)

    return fresh, None


def main(argv):
    if len(argv) < 5:
        print(__doc__)
        return 2
    root, task_id, mode = os.path.realpath(argv[1]), argv[2], argv[3]
    time.sleep = lambda _seconds=0: None  # pacing sleeps of tasks (rate limits) mean nothing to an in-memory model
    seeds = [int(x) for x in argv[4:]]
    sys.path.insert(0, os.path.join(root, "eval", "tests"))
    sys.path.insert(0, os.path.join(root, "SkillsForUnity", "Evaluation~"))
    module, style, cls = find_module(root, task_id)
    holder = unittest.TestCase()
    fresh, instance = make_factory(style, cls, holder)
    modes = ("legacy", "6000.4") if mode == "full" else ("legacy",)
    problems, runs, started = [], 0, time.time()
    for seed in seeds:
        for ids in modes:
            try:
                first, second = fresh(seed, ids), fresh(seed, ids)
                if first.fingerprint != second.fingerprint:
                    problems.append("seed %s ids %s: fingerprint differs between two builds" % (seed, ids))
                card = first.verify(first.solve(first.solutions.reference))
                runs += 1
                if not card.strict:
                    problems.append("seed %s ids %s: reference not strict, failed %s" % (seed, ids, sorted(card.failed())))
                untouched = fresh(seed, ids).verify(None)
                runs += 1
                if not untouched.failed():
                    problems.append("seed %s ids %s: untouched fixture passes everything" % (seed, ids))
                if hasattr(second, "teardown"):
                    second.teardown()
                    second.teardown()
                if mode == "full":
                    for negative in first.solutions.NEGATIVES:
                        run = fresh(seed, ids)
                        got = set(run.verify(run.solve(negative.apply)).failed())
                        runs += 1
                        if got != negative.expect_failed:
                            problems.append("seed %s ids %s: negative %s failed %s, expected %s" % (
                                seed, ids, negative.name, sorted(got), sorted(negative.expect_failed)))
                    for name, fn in first.solutions.ALTERNATIVES:
                        run = fresh(seed, ids)
                        card = run.verify(run.solve(fn))
                        runs += 1
                        if not card.strict:
                            problems.append("seed %s ids %s: alternative %s not strict, failed %s" % (
                                seed, ids, name, sorted(card.failed())))
            except Exception as exc:  # a fixture or verifier crash is itself the finding
                problems.append("seed %s ids %s: %s: %s" % (seed, ids, type(exc).__name__, str(exc)[:300]))
    prints = {}
    for seed in seeds[:6]:
        try:
            prints[seed] = fresh(seed, "legacy").fingerprint
        except Exception:
            pass
    if len(seeds) > 1 and len(set(prints.values())) < len(prints):
        problems.append("different seeds produced identical fingerprints: %s" % sorted(prints))
    holder.doCleanups()
    if instance is not None:
        instance.doCleanups()
    print("%s %s (%s style): %d seed(s), %d scored run(s), %.1fs, %d problem(s)" % (
        task_id, mode, style, len(seeds), runs, time.time() - started, len(problems)))
    for line in problems:
        print("  PROBLEM " + line)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
