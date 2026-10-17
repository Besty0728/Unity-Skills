"""v3 selftest and gate (maintainer tool): proves each task's private verify with its reference, negatives and alternatives.

Every case runs through the real runner CLI exactly as an agent drives it: start (one task), next, the solution does
its work through REST as SelftestSolver, the answer file is written, submit, then result.json is read. One session per
case, so every case starts from a fresh build of the same seed.

Gate criteria (contract 5.6, 9), per task:
  reference x2 (same seed)   status pass, and both builds have the same fingerprint
  reference at other seeds   every other manifest seed and --extra-seed: status pass
  each negative              status partial or fail, failed assertions == the declared expect_failed
  each alternative           status pass
Any case that ends invalid or contaminated, or whose inputs change while it runs, makes the task red. A case that
fails is never run again: a gate passes in one go (the one repeat is for a case that could not start because the
editor was compiling or importing).

The gate record (default eval/runs/selftest/gate.json) is keyed by canonical_digest of
{taskId, caseVersion, unityMajor, pluginVersion, pipeline, gateInputsHash}. `pipeline` is the render pipeline the runs
used (the runner's own snapshot at issue, state.env.atIssue.pipelineType): tasks that accept several pipelines are gated
once per pipeline and the entries never overwrite each other. GATE_INPUTS (eval/cli.py) names the maintainer files that
go into gateInputsHash next to the runner and the task's public/private/solutions files.

  python3 -B eval/cli.py --project <P> selftest3 [--task ID ...] [--repeat 2] [--private-dir D | --pack-dir D]
  python3 -B eval/cli.py --project <P> gate3 [--task ID ...] [--unity-major 2022|6000] [--pipeline BuiltIn|URP|...]

Exit codes: 0 green, 1 a case failed, 2 the environment (editor, pack, solutions) is not usable.
"""
import contextlib
import datetime
import glob
import json
import os
import sys
import time
import traceback

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from harness import solver_driver as D  # noqa: E402
from usk_eval import environment as runner_environment  # noqa: E402
from usk_eval import journal as runner_journal  # noqa: E402
from usk_eval import scope as runner_scope  # noqa: E402
from usk_eval import package_version, store  # noqa: E402
from usk_eval.lock import pid_alive  # noqa: E402
from usk_eval.problems import ProblemError  # noqa: E402
from usk_eval.rest import RestError  # noqa: E402

REPO = D.REPO
GATE_SCHEMA = 1
DEFAULT_PRIVATE_DIR = os.path.join(REPO, "eval", "pack", "private")
DEFAULT_SOLUTIONS_DIR = os.path.join(REPO, "eval", "pack", "solutions")
DEFAULT_RUNS_DIR = os.path.join(REPO, "eval", "runs", "selftest")
DEFAULT_GATE_FILE = os.path.join(DEFAULT_RUNS_DIR, "gate.json")
MIN_REPEAT = 2
MIN_NEGATIVES = 5
MIN_ALTERNATIVES = 1
ENVIRONMENT_EXIT = 2


class SelftestError(Exception):
    """The run cannot continue (exit code 2): wrong environment, unusable pack or solutions."""


# ---------------------------------------------------------------- gate record


def echo(message):
    print(message, flush=True)


def new_gate():
    return {"schema": GATE_SCHEMA, "gates": {}}


def load_gate(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            gate = json.load(handle)
    except FileNotFoundError:
        return new_gate()
    except (OSError, ValueError) as exc:
        raise SelftestError("gate file %s is unreadable: %s" % (path, type(exc).__name__))
    if not isinstance(gate, dict) or gate.get("schema") != GATE_SCHEMA or not isinstance(gate.get("gates"), dict):
        raise SelftestError("gate file %s is not a schema %d gate record" % (path, GATE_SCHEMA))
    return gate


def save_gate(path, gate):
    store.atomic_write_bytes(path, (store.dumps(gate) + "\n").encode("utf-8"))


@contextlib.contextmanager
def gate_lock(path, timeout_s=60.0):
    """Exclusive access to the gate file for a read-modify-write: selftests of different editors share one record."""
    lock = path + ".lock"
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    deadline = time.time() + timeout_s
    while True:
        try:
            fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
            with os.fdopen(fd, "w") as handle:
                handle.write(str(os.getpid()))
            break
        except FileExistsError:
            try:
                with open(lock, "r") as handle:
                    holder = int(handle.read().strip() or 0)
            except (OSError, ValueError):
                holder = None
            if holder is not None and not pid_alive(holder):
                with contextlib.suppress(OSError):
                    os.remove(lock)
                continue
            if time.time() > deadline:
                raise SelftestError("the gate file %s is locked by pid %s" % (path, holder))
            time.sleep(0.2)
    try:
        yield
    finally:
        with contextlib.suppress(OSError):
            os.remove(lock)


def unity_major(unity_version):
    return str(unity_version or "").split(".", 1)[0]


def gate_inputs_hash(packs, task_id, gate_inputs, repo_root=None):
    """gateInputsHash of a task (contract 9): the runner (minus usage/ and report.py), the task's public files, its
    private and solutions modules and the maintainer files of GATE_INPUTS.

    For the repo layout (package pack, eval/pack/private, eval/pack/solutions) this equals
    usk_eval.environment.gate_inputs_hash; a pack or folder given on the command line is hashed where it really is,
    so a gate never vouches for files it did not run.
    """
    repo_root = repo_root or REPO
    runner = os.path.join(repo_root, "SkillsForUnity", "Evaluation~")
    entries = []

    def add(path):
        if path and os.path.isfile(path):
            entries.append((os.path.relpath(path, repo_root).replace(os.sep, "/"), path))

    add(os.path.join(runner, "run_eval.py"))
    for rel, path in runner_environment.tree_files(os.path.join(runner, "usk_eval")):
        if rel.endswith(".py") and not rel.startswith("usage/") and rel != "report.py":
            add(path)
    for _rel, path in runner_environment.tree_files(packs.pack.task_dir(task_id)):
        add(path)
    add(packs.private_path(task_id))
    add(packs.solutions_path(task_id))
    for pattern in gate_inputs:
        for path in sorted(glob.glob(os.path.join(repo_root, *pattern.split("/")))):
            add(path)
    return runner_environment.hash_files(sorted(set(entries)))


def gate_key(task_id, case_version, unity_version, plugin_version, pipeline, inputs_hash):
    """canonical_digest of {taskId, caseVersion, unityMajor, pluginVersion, pipeline, gateInputsHash} (contract 9)."""
    return store.canonical_digest({"taskId": task_id, "caseVersion": case_version,
                                   "unityMajor": unity_major(unity_version), "pluginVersion": plugin_version,
                                   "pipeline": pipeline, "gateInputsHash": inputs_hash})


def gate_status(gate, packs, task_id, unity_version, plugin_version, pipeline, gate_inputs, repo_root=None):
    """{green, key, entry, hash, reason} of a task for the current inputs; green needs a green record under the key."""
    inputs = gate_inputs_hash(packs, task_id, gate_inputs, repo_root)
    key = gate_key(task_id, packs.meta(task_id)["caseVersion"], unity_version, plugin_version, pipeline, inputs)
    entry = (gate.get("gates") or {}).get(key)
    if entry is None:
        reason = "no gate recorded for the current inputs"
    elif not entry.get("green"):
        reason = "recorded red: %s" % ", ".join(entry.get("failing") or ["?"])
    else:
        reason = "green"
    return {"green": bool(entry and entry.get("green")), "key": key, "entry": entry, "hash": inputs,
            "reason": reason}


def record_gate(gate, task_id, case_version, unity_version, plugin_version, pipeline, inputs_hash, green, cases,
                failing):
    """Writes the entry for this exact input set and drops the entries it supersedes (same task, case version, Unity
    major, plugin version and pipeline under another inputs hash); other pipelines keep their entries."""
    key = gate_key(task_id, case_version, unity_version, plugin_version, pipeline, inputs_hash)
    major = unity_major(unity_version)
    for other, entry in list(gate["gates"].items()):
        if other != key and (entry.get("taskId"), entry.get("caseVersion"), entry.get("unityMajor"),
                             entry.get("pluginVersion"), entry.get("pipeline")) == (task_id, case_version, major,
                                                                                    plugin_version, pipeline):
            del gate["gates"][other]
    gate["gates"][key] = {"taskId": task_id, "caseVersion": case_version, "unityMajor": major,
                          "pluginVersion": plugin_version, "pipeline": pipeline, "gateInputsHash": inputs_hash,
                          "green": bool(green), "cases": cases, "failing": list(failing),
                          "recordedAt": store.now_iso()}
    return key


# ---------------------------------------------------------------- the selftest


def failed_ids(result):
    return sorted(row["id"] for row in ((result or {}).get("score") or {}).get("assertions") or []
                  if not row.get("pass"))


class Selftest(object):
    def __init__(self, runner, packs, tasks, out=None, repeat=MIN_REPEAT, extra_seeds=(), case_filter=(),
                 record=True, gate_file=DEFAULT_GATE_FILE, runs_dir=DEFAULT_RUNS_DIR, gate_inputs=(),
                 min_negatives=MIN_NEGATIVES, min_alternatives=MIN_ALTERNATIVES, abort_active=False,
                 cleanup_stale=False, quiet_wait_s=900.0, lease_wait_s=0.0, lang="en", repo_root=None,
                 busy_waits=20):
        self.runner, self.packs, self.tasks, self.out = runner, packs, list(tasks), out or echo
        self.repeat, self.extra_seeds, self.case_filter = repeat, list(extra_seeds), list(case_filter)
        self.record = record and not case_filter
        self.gate_file, self.runs_dir, self.gate_inputs = gate_file, runs_dir, tuple(gate_inputs)
        self.min_negatives, self.min_alternatives = min_negatives, min_alternatives
        self.abort_active, self.cleanup_stale = abort_active, cleanup_stale
        self.quiet_wait_s, self.lease_wait_s, self.lang = quiet_wait_s, lease_wait_s, lang
        self.busy_waits = busy_waits
        self.repo_root = repo_root or REPO
        self.agent = D.ScriptedAgent(runner, packs, with_private=True, log=self.say)
        self.rows = []
        self.unity_version = self.plugin_version = self.pipeline = None

    # ------------------------------------------------------------------ output

    def say(self, message):
        self.out(message)

    def echo(self, row):
        notes = []
        if row.get("problems"):
            notes.append("problems=%s" % ",".join(row["problems"]))
        if row.get("note"):
            notes.append(row["note"])
        self.say("[%s] %-5s %-34s status=%-12s failed=%s%s" % (
            "ok" if row["ok"] else "!!", row["task"], row["case"], row.get("status"), ",".join(row.get("failed") or []),
            ("  " + "  ".join(notes)) if notes else ""))
        if not row["ok"]:
            if row.get("expect") not in (None, "pass"):
                self.say("      expected: %s" % (row["expect"],))
            for reason in (row.get("reasons") or [])[:6]:
                self.say("      reason: %s" % reason)
            for record in row.get("foreign") or []:
                self.say("      foreign record: %s" % record)

    # ------------------------------------------------------------------ environment

    def wait_quiet(self):
        """Block until the editor runs no job and is not compiling; a timeout ends the run (exit 2)."""
        try:
            return D.wait_quiet(self.agent.client, timeout_s=self.quiet_wait_s, log=self.say)
        except D.EditorBusy as exc:
            raise SelftestError(str(exc))

    def preflight(self):
        """Locate the editor, wait until it runs no job, read the versions the gate key needs."""
        client = self.agent.client
        if self.wait_quiet():
            self.say("editor is quiet now")
        health = client.health()
        self.unity_version, self.plugin_version = health.get("unityVersion"), health.get("version")
        self.say("editor: Unity %s, UnitySkills %s, mode %s, instance %s" % (
            self.unity_version, self.plugin_version, health.get("currentMode"), health.get("instanceId")))
        if self.cleanup_stale:
            cleaned = self.runner.run("cleanup", "--stale", session=False)
            self.say("cleanup --stale: %s" % (cleaned.describe() if not cleaned.ok else "removed %s" % (
                [r["runId"] for r in cleaned.data.get("removed") or []] or "nothing")))

    # ------------------------------------------------------------------ one case

    def start_args(self, task_id, seed=None):
        meta = self.packs.meta(task_id)
        args = ["--track", meta["track"], "--tier", meta["tier"], "--tasks", task_id, "--mode", "suite",
                "--lang", self.lang, "--no-usage"]
        if seed is not None:
            args += ["--seed", str(seed)]
        return args

    def finish_session(self, row):
        """Leave no session behind: an unfinished one (failed build, pending verification) is aborted."""
        current = self.agent.state.current()
        if current and current.get("status") == "active":
            self.agent.runner.session = current["sessionId"]
            aborted = self.agent.abort("selftest_cleanup")
            if not aborted.ok:
                row["note"] = (row.get("note") or "") + " abort failed: %s" % aborted.describe()

    def case(self, task_id, name, solver, expect, expect_failed=None, seed=None):
        """Runs one case; returns its row (always: a case that could not run is a failed row, not an exception).

        A failed case is not run again. The one repeat is for a case that could not start because the editor was
        compiling or importing (EDITOR_BUSY, often another tool's file changes): nothing ran, so it waits and begins.
        """
        waits = 0
        while True:
            row, fatal = self.attempt_case(task_id, name, solver, expect, expect_failed, seed)
            if fatal is None and "EDITOR_BUSY" in row["problems"] and waits < self.busy_waits:
                waits += 1
                self.say("[wait] %-5s %-34s the editor is compiling or importing" % (task_id, name))
                self.wait_quiet()
                continue
            break
        row["busyWaits"] = waits
        row["foreign"] = self.foreign(row)
        if not row["foreign"]:
            del row["foreign"]
        self.rows.append(row)
        self.echo(row)
        if fatal is None:
            self.observe_pipeline(row.get("pipeline"))
        if fatal is not None:
            raise fatal
        return row

    def observe_pipeline(self, pipeline):
        """The render pipeline of the run, from the runner's own snapshot at issue; it must not change within a run."""
        if not pipeline:
            return
        if self.pipeline is None:
            self.pipeline = pipeline
        elif pipeline != self.pipeline:
            raise SelftestError("the render pipeline changed from %s to %s during the run" % (self.pipeline, pipeline))

    def attempt_case(self, task_id, name, solver, expect, expect_failed, seed):
        row = {"task": task_id, "case": name, "expect": expect if expect_failed is None else sorted(expect_failed),
               "status": None, "failed": [], "ok": False, "reasons": [], "problems": [], "attempt": None,
               "session": None, "fingerprint": None, "pipeline": None, "seed": seed, "seconds": None}
        began = time.time()
        fatal = None
        try:
            self.execute(row, task_id, solver, expect_failed, seed)
        except SelftestError as exc:
            fatal = exc
        except KeyboardInterrupt:
            raise
        except (ProblemError, RestError, D.DriverError, store.StoreError) as exc:
            row["note"] = "%s: %s" % (type(exc).__name__, str(exc)[:300])
        except Exception as exc:
            row["note"] = "driver error %s: %s" % (type(exc).__name__, str(exc)[:300])
            row["trace"] = traceback.format_exc()
        finally:
            row["seconds"] = round(time.time() - began, 1)
            try:
                self.finish_session(row)
            except Exception as exc:
                row["note"] = (row.get("note") or "") + " cleanup error %s" % type(exc).__name__
        return row, fatal

    def foreign(self, row):
        """Journal records of a failed attempt that SelftestSolver did not make: another tool's file changes imported by
        the runner's refresh, edits made in the editor. Printed with the red row; they never turn a red row green."""
        if not row.get("session") or not row.get("attempt") or row["ok"]:
            return []
        try:
            records = D.foreign_violations(self.agent.state, row["session"], row["attempt"])
            if row.get("status") == "contaminated":
                state = self.agent.state.state(row["session"], row["attempt"])
                scope = state["scope"]
                path = os.path.join(self.agent.state.paths.attempt_dir(row["session"], row["attempt"]), "journal.jsonl")
                flat = runner_journal.flatten(runner_journal.load_entries(path), state["journal"].get("refreshWindow"))
                records += runner_scope.non_rest(flat, scope["assetRoot"], scope["scenePath"])
        except (OSError, KeyError, store.StoreError):
            return []
        return [D.describe_record(rec) for rec in records[:5]]

    def execute(self, row, task_id, solver, expect_failed, seed):
        runner = self.runner
        runner.session = None
        started = self.agent.start(*self.start_args(task_id, seed))
        # `start` hands back an active session with the same selection (created False): that one is someone else's,
        # and must be neither driven nor judged here.
        stale = started.data.get("sessionId") if started.ok and not started.data.get("created") else None
        if stale or "SESSION_ACTIVE" in started.codes:
            if self.abort_active:
                current = self.agent.state.current()
                runner.session = stale or (current or {}).get("sessionId")
                self.agent.abort("selftest_stale_session")
                runner.session = None
                started = self.agent.start(*self.start_args(task_id, seed))
                stale = started.data.get("sessionId") if started.ok and not started.data.get("created") else None
            if stale or "SESSION_ACTIVE" in started.codes:
                raise SelftestError("an evaluation session is still active in this project; abort it (python3 -B %s "
                                    "abort --project %s) or pass --abort-active" % (runner.entry, runner.project))
        if not started.ok:
            row["problems"] = started.codes
            row["note"] = "start: " + started.describe()
            if started.codes and "EDITOR_BUSY" not in started.codes and \
                    all(self.problem_exit(code) == ENVIRONMENT_EXIT for code in started.codes):
                raise SelftestError("the environment is not ready for %s: %s" % (task_id, started.describe()))
            return
        row["session"] = started.data["sessionId"]
        if "PACK_MODIFIED" in started.notice_codes and not getattr(self, "_warned_modified", False):
            self._warned_modified = True
            self.say("warning: pack files differ from the manifest hashes (run eval/tools/build_pack_manifest.py "
                     "before a release)")
        run = self.agent.run_attempt(solver, submitted_by="driver")
        if run.attempt is None:
            row["problems"] = run.next_result.codes
            row["note"] = "next: " + run.next_result.describe()
            return
        row["attempt"] = run.attempt.attempt_id
        state = self.agent.state.state(row["session"], row["attempt"])
        row["fingerprint"] = (state or {}).get("fingerprint")
        row["pipeline"] = (((state or {}).get("env") or {}).get("atIssue") or {}).get("pipelineType")
        if run.solver_error:
            row["status"] = "solver_error"
            row["note"] = run.solver_error.strip().splitlines()[-1][:300]
            row["trace"] = run.solver_error
            return
        if run.submit_result is not None and not run.submit_result.ok:
            row["problems"] = run.submit_result.codes
        result = self.agent.state.result(row["session"], row["attempt"])
        if result is None:
            row["note"] = "no result.json after submit: %s" % (run.submit_result.describe() if run.submit_result
                                                                 else "not submitted")
            return
        row["status"] = result["status"]
        row["failed"] = failed_ids(result)
        row["reasons"] = result.get("reasons") or []
        if expect_failed is None:
            row["ok"] = result["status"] == "pass"
        else:
            row["ok"] = result["status"] in ("partial", "fail") and set(row["failed"]) == set(expect_failed)

    @staticmethod
    def problem_exit(code):
        from usk_eval.problems import BLOCKING
        return BLOCKING.get(code, (4,))[0]

    # ------------------------------------------------------------------ one task

    def wanted(self, name):
        return not self.case_filter or any(part in name for part in self.case_filter)

    def run_task(self, task_id):
        meta = self.packs.meta(task_id)
        mine = len(self.rows)
        before = gate_inputs_hash(self.packs, task_id, self.gate_inputs, self.repo_root)
        problems = []
        try:
            solutions = self.packs.solutions(task_id)
            problems = solutions.problems(self.min_negatives, self.min_alternatives)
        except D.DriverError as exc:
            solutions, problems = None, [str(exc)]
        if problems:
            row = {"task": task_id, "case": "solutions", "expect": "usable solutions module", "status": "unusable",
                   "failed": [], "ok": False, "reasons": problems, "problems": [], "attempt": None}
            self.rows.append(row)
            self.echo(row)
            return self.rows[mine:], before
        references = []
        for index in range(self.repeat):
            name = "reference#%d" % index
            if self.wanted(name) or self.wanted("reference"):
                references.append(self.case(task_id, name, solutions.reference, "pass"))
        if len(references) >= MIN_REPEAT:
            prints = [r.get("fingerprint") for r in references]
            same = len(set(prints)) == 1 and None not in prints
            row = {"task": task_id, "case": "determinism", "expect": "same fingerprint", "ok": same,
                   "status": "ok" if same else "differs", "failed": [], "reasons": [], "problems": [],
                   "attempt": None, "fingerprints": prints}
            self.rows.append(row)
            self.echo(row)
        for seed in list(meta.get("seeds") or [])[1:] + [s for s in self.extra_seeds if s not in meta.get("seeds")]:
            name = "reference@%d" % seed
            if self.wanted(name):
                self.case(task_id, name, solutions.reference, "pass", seed=seed)
        for neg in solutions.negatives:
            name = "negative:" + neg.name
            if self.wanted(name):
                self.case(task_id, name, neg.apply, "partial|fail", expect_failed=neg.expect_failed)
        for alt_name, alt in solutions.alternatives:
            name = "alternative:" + alt_name
            if self.wanted(name):
                self.case(task_id, name, alt, "pass")
        after = gate_inputs_hash(self.packs, task_id, self.gate_inputs, self.repo_root)
        if after != before:
            row = {"task": task_id, "case": "inputs_changed", "expect": "stable inputs", "ok": False,
                   "status": "changed", "failed": [], "reasons": ["a gate input changed while the task was running"],
                   "problems": [], "attempt": None}
            self.rows.append(row)
            self.echo(row)
        return self.rows[mine:], before

    # ------------------------------------------------------------------ whole run

    def run(self):
        started_at = store.now_iso()
        try:
            with D.EditorLease(self.runner.project, "selftest3", wait_s=self.lease_wait_s, log=self.say):
                self.preflight()
                for task_id in self.tasks:
                    rows, inputs = self.run_task(task_id)
                    if self.record:
                        self.record_task(task_id, rows, inputs)
        except KeyboardInterrupt:
            self.say("interrupted; aborting the active session")
            self.agent.abort_quietly("selftest_interrupted")
            raise
        finally:
            if self.rows:
                self.write_rows(started_at)
        bad = [row for row in self.rows if not row["ok"]]
        self.say("selftest3: %d/%d ok" % (len(self.rows) - len(bad), len(self.rows)))
        if not self.record:
            self.say("gate not recorded (%s)" % ("case filter" if self.case_filter else "--no-gate"))
        return 1 if bad else 0

    def record_task(self, task_id, rows, inputs):
        """Writes the task's gate entry as soon as the task is done, under the gate file's lock."""
        if self.pipeline is None:
            self.say("gate %s not recorded: no attempt was issued, so the render pipeline is unknown" % task_id)
            return
        meta = self.packs.meta(task_id)
        failing = [row["case"] for row in rows if not row["ok"]]
        with gate_lock(self.gate_file):
            gate = load_gate(self.gate_file)
            key = record_gate(gate, task_id, meta["caseVersion"], self.unity_version, self.plugin_version,
                              self.pipeline, inputs, not failing, len(rows), failing)
            save_gate(self.gate_file, gate)
        self.say("gate %s %s (Unity %s, %s, %s)" % (task_id, "GREEN" if not failing else "RED", self.unity_version,
                                                    self.pipeline, key[:12]))

    def write_rows(self, started_at):
        stamp = datetime.datetime.now().strftime("%Y%m%dT%H%M%S")
        path = os.path.join(self.runs_dir, stamp + ".json")
        document = {"schema": 1, "startedAt": started_at, "finishedAt": store.now_iso(),
                    "project": self.runner.project, "unityVersion": self.unity_version,
                    "pluginVersion": self.plugin_version, "pipeline": self.pipeline,
                    "pack": {"id": self.packs.pack.id,
                                                                   "version": self.packs.pack.version,
                                                                   "modified": list(self.packs.pack.modified)},
                    "rows": self.rows}
        try:
            store.atomic_write_bytes(path, (store.dumps(document) + "\n").encode("utf-8"))
            self.say("rows: %s" % path)
        except store.StoreError as exc:
            self.say("could not write %s: %s" % (path, exc))


# ---------------------------------------------------------------- command line


def split_tasks(values):
    out = []
    for value in values or []:
        out.extend(part.strip() for part in str(value).split(",") if part.strip())
    return out


def open_packs(args, environ):
    """PackAccess for the command line options: --pack-dir (self-contained) or the package pack + --private-dir."""
    if args.pack_dir and args.private_dir:
        raise SelftestError("--pack-dir and --private-dir are mutually exclusive")
    pack_dir = os.path.abspath(args.pack_dir) if args.pack_dir else None
    private_dir = None if pack_dir else os.path.abspath(args.private_dir or DEFAULT_PRIVATE_DIR)
    solutions_dir = os.path.abspath(args.solutions_dir) if args.solutions_dir else (
        os.path.join(pack_dir, "solutions") if pack_dir else DEFAULT_SOLUTIONS_DIR)
    try:
        return D.PackAccess(pack_dir=pack_dir, private_dir=private_dir, solutions_dir=solutions_dir,
                            environ=environ)
    except ProblemError as exc:
        raise SelftestError("cannot open the pack: %s" % "; ".join(p["message"] for p in exc.problems))


def select_tasks(packs, wanted):
    known = packs.ids()
    if not wanted:
        return known
    unknown = [t for t in wanted if t not in known]
    if unknown:
        raise SelftestError("not in the pack: %s (have %s)" % (", ".join(unknown), ", ".join(known) or "none"))
    return wanted


def require_project(project):
    from usk_eval import paths as runner_paths
    if not project or not runner_paths.is_project(project):
        raise SelftestError("%s is not a Unity project; pass --project" % (project,))
    return os.path.realpath(project)


def runner_environ(extra):
    """The process environment plus `extra`, without the variables that make a run non-hermetic (host ids, overrides)."""
    base = dict(os.environ)
    base.update(extra or {})
    return D.clean_environ(base)


def run_from_args(args, gate_inputs, out=echo, environ=None, inproc=False):
    """selftest3 entry used by eval/cli.py (and the tests); returns the exit code."""
    try:
        packs = open_packs(args, environ)
        tasks = select_tasks(packs, split_tasks(args.task))
        if args.repeat < MIN_REPEAT:
            raise SelftestError("selftest3 requires at least %d reference repeats" % MIN_REPEAT)
        if args.dry_run:
            return dry_run(packs, tasks, args, gate_inputs, out)
        project = require_project(args.project)
        runner = D.Runner(project, environ=runner_environ(environ), inproc=inproc)
        test = Selftest(runner, packs, tasks, out=out, repeat=args.repeat, extra_seeds=args.extra_seed or (),
                        case_filter=split_tasks(args.case), record=not args.no_gate,
                        gate_file=os.path.abspath(args.gate_file) if args.gate_file else DEFAULT_GATE_FILE,
                        runs_dir=os.path.dirname(os.path.abspath(args.gate_file)) if args.gate_file
                        else DEFAULT_RUNS_DIR, gate_inputs=gate_inputs, abort_active=args.abort_active,
                        cleanup_stale=args.cleanup_stale, quiet_wait_s=args.wait_quiet,
                        lease_wait_s=args.wait_lease, lang=args.lang)
        return test.run()
    except SelftestError as exc:
        out("selftest3: %s" % exc)
        return ENVIRONMENT_EXIT
    except D.EditorBusy as exc:
        out("selftest3: %s" % exc)
        return ENVIRONMENT_EXIT
    except (ProblemError, RestError) as exc:
        detail = "; ".join(p["fix"] for p in exc.problems) if isinstance(exc, ProblemError) else str(exc)
        out("selftest3: the editor is not usable: %s" % detail)
        return ENVIRONMENT_EXIT


def dry_run(packs, tasks, args, gate_inputs, out):
    """Everything except the editor: pack, solutions shape, case list, gate inputs hash."""
    bad = 0
    out("pack %s %s (%s); %d task(s)" % (packs.pack.id, packs.pack.version, packs.pack.source, len(tasks)))
    if packs.pack.modified:
        out("warning: files differ from the manifest: %s" % ", ".join(packs.pack.modified))
    for task_id in tasks:
        meta = packs.meta(task_id)
        try:
            solutions = packs.solutions(task_id)
            problems = solutions.problems(MIN_NEGATIVES, MIN_ALTERNATIVES)
        except D.DriverError as exc:
            solutions, problems = None, [str(exc)]
        out("%s (%s/%s, caseVersion %s): gate inputs %s" % (task_id, meta["track"], meta["tier"], meta["caseVersion"],
                                                          gate_inputs_hash(packs, task_id, gate_inputs)[:12]))
        if problems:
            bad += 1
            for problem in problems:
                out("  !! %s" % problem)
            continue
        cases = ["reference#%d" % i for i in range(args.repeat)] + ["negative:" + n.name for n in solutions.negatives] \
            + ["alternative:" + name for name, _fn in solutions.alternatives]
        out("  cases: %s" % ", ".join(cases))
    return 1 if bad else 0


def gate_from_args(args, gate_inputs, out=echo, environ=None, inproc=False):
    """gate3 entry: is the recorded gate green for the current inputs? Exit 0 when every task is, else 1.

    The Unity version, the plugin version and the render pipeline come from the options; what is missing is asked of
    the editor of --project.
    """
    try:
        packs = open_packs(args, environ)
        tasks = select_tasks(packs, split_tasks(args.task))
        plugin_version = args.plugin_version or package_version()
        unity_version, pipeline = args.unity_major, args.pipeline
        if not unity_version or not pipeline:
            project = require_project(args.project)
            runner = D.Runner(project, environ=runner_environ(environ), inproc=inproc)
            client = D.ScriptedAgent(runner, packs).client
            if not unity_version:
                health = client.health()
                unity_version, plugin_version = health.get("unityVersion"), args.plugin_version or health.get("version")
            if not pipeline:
                pipeline = client.read("project_get_render_pipeline").get("pipelineType")
        if not pipeline:
            raise SelftestError("the render pipeline is unknown; pass --pipeline")
        gate = load_gate(os.path.abspath(args.gate_file) if args.gate_file else DEFAULT_GATE_FILE)
    except SelftestError as exc:
        out("gate3: %s" % exc)
        return ENVIRONMENT_EXIT
    except (ProblemError, RestError) as exc:
        out("gate3: the editor is not usable: %s" % exc)
        return ENVIRONMENT_EXIT
    red = 0
    for task_id in tasks:
        status = gate_status(gate, packs, task_id, unity_version, plugin_version, pipeline, gate_inputs)
        red += 0 if status["green"] else 1
        out("[%s] %-5s caseVersion %s, Unity %s, %s: %s" % ("ok" if status["green"] else "!!", task_id,
                                                             packs.meta(task_id)["caseVersion"],
                                                             unity_major(unity_version), pipeline, status["reason"]))
    return 1 if red else 0

# Producer:Betsy
