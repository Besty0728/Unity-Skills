"""Public task API (contract 5.2, 5.6, 5.7): what pack task modules, private verifiers and solutions see.

Build contexts carry the runner's read-write client; verify contexts only a read-only view (verify can never write
to the editor); solver contexts the selftest solver's client. Scope helpers delegate to usk_eval.scope.

Objects: from Unity 6000.4 on every instanceId is 0. Keep verifylib.object_id(result) in handles (the non-zero
instanceId, else the entityId text), name objects with address(id) / address(id, "parent"), compare them with
verifylib.same_id(a, b); ctx.root is the canonical id of the object root R.
"""
import random
from types import MappingProxyType

from . import submodule
from .fixture import Scope, address  # noqa: F401  (address is re-exported for task modules)
from .rest import RUNNER_AGENT, ReadOnlyView


class PublicTask(object):
    id = None
    track = "ops"               # "ops" | "dev"
    tier = "basic"              # "basic" | "standard" | "challenge"
    case_version = 1
    minutes = 10
    # Read-only default; a task that needs other requirements assigns its own dict.
    requires = MappingProxyType({"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": ()})
    layer_pairs = ()            # physics layer pairs snapshotted before the build and restored at teardown
    needs_focus = False         # build/teardown triggers compilation

    def build(self, ctx):
        raise NotImplementedError

    def snapshot(self, ctx):
        raise NotImplementedError

    def prompt_vars(self, ctx):
        raise NotImplementedError

    def answer_schema(self):
        raise NotImplementedError

    def teardown(self, ctx):
        return None


class Negative(object):
    def __init__(self, name, apply, expect_failed, note=""):
        self.name = name
        self.apply = apply              # fn(ctx) -> answer dict (performs a wrong solution)
        self.expect_failed = set(expect_failed)
        self.note = note


class _Context(object):
    def __init__(self, reader, scope, seed, project_path, env, lang, track, tier, task, handles=None, log=None):
        self.scope = scope
        self.seed = seed
        self.project_path = project_path
        self.env = dict(env or {})
        self.lang = lang
        self.track = track
        self.tier = tier
        self.task = task
        self.handles = handles if handles is not None else {}
        self._reader = reader
        self._probe = None
        self._log = log

    @property
    def R(self):
        return self.scope.object_root

    @property
    def A(self):
        return self.scope.asset_root

    @property
    def scene(self):
        return self.scope.scene_path

    @property
    def root(self):
        """Canonical id of the object root R (pass it to address() or Probe)."""
        return self.scope.root_id

    def path(self, *parts):
        return "/".join((self.R,) + tuple(parts))

    def asset(self, *parts):
        return "/".join((self.A,) + tuple(parts))

    def read(self, skill, **args):
        return self._reader.read(skill, **args)

    @property
    def probe(self):
        if self._probe is None:
            self._probe = submodule("verifylib").Probe(self._reader)
        return self._probe

    def log(self, message):
        if self._log is not None:
            self._log(str(message))


class BuildContext(_Context):
    """build / snapshot / prompt_vars / teardown."""

    def __init__(self, client, scope, seed, project_path, env, lang, track, tier, task, handles=None, log=None):
        if not isinstance(seed, int) or isinstance(seed, bool):
            raise ValueError("build needs an integer seed, got %r" % (seed,))   # Random(None) would be nondeterministic
        _Context.__init__(self, client, scope, seed, project_path, env, lang, track, tier, task, handles, log)
        self.c = client
        self.client = client
        self.rng = random.Random(seed)

    def wait_idle(self, **kwargs):
        return self.client.wait_idle(**kwargs)

    def wait_job(self, job_id, **kwargs):
        return self.client.wait_job(job_id, **kwargs)


class VerifyContext(_Context):
    """verify / verify_runtime / allowed_change: frozen evidence and a read-only view, no write client.

    log() writes nothing here: what a private verifier derives must not reach runner.log (contract 4.2).
    """

    c = None
    client = None

    def __init__(self, client, scope, seed, project_path, env, lang, track, tier, task, handles=None, log=None,
                 c0=None, c0_digests=None, answer=None, answer_error=None, answer_schema_errors=(), records=(),
                 journal_complete=False, journal=None, telemetry=None, allowed_change=None):
        _Context.__init__(self, ReadOnlyView(client), scope, seed, project_path, env, lang, track, tier, task,
                          handles, log)
        self.c0 = c0 if c0 is not None else {}
        self.c0_digests = c0_digests if c0_digests is not None else {}
        self.answer = answer
        self.answer_error = answer_error
        self.answer_schema_errors = list(answer_schema_errors)
        self.records = list(records)
        self.journal_complete = journal_complete
        self.journal = journal or {}
        self.telemetry = telemetry or {}
        self.scenario = None
        self._allowed_change = allowed_change

    def allowed(self, extra=None):
        if extra is not None and not callable(extra):
            raise TypeError("violations(extra) takes a callable(rec) -> bool; the private module's allowed_change "
                            "is applied automatically")

        def check(rec):
            if self._allowed_change is not None and self._allowed_change(self, rec):
                return True
            return bool(extra is not None and extra(rec))
        return check

    def violations(self, extra=None):
        """Scope violations, ignoring Play Mode records and the runner's own attempt-window records."""
        records = [r for r in self.records if not r.get("isPlaying")
                   and not (r.get("agent") == RUNNER_AGENT and r.get("origin") == "attempt")]
        return submodule("scope").scope_violations(records, self.R, self.A, self.scene,
                                                   extra_allowed=self.allowed(extra))

    def non_rest(self):
        return submodule("scope").non_rest(self.records, self.A, self.scene, allowed=self.allowed())

    def direct_writes(self):
        return submodule("scope").direct_writes(self.records, self.A, self.scene)

    def write_records(self):
        return submodule("scope").write_records(self.records, self.scene, RUNNER_AGENT)

    def saved_scenes(self):
        return [r for r in self.records if r.get("kind") == "scene_saved"]

    def saved_other_scenes(self):
        scene = self.scene.replace("\\", "/")
        return [r for r in self.saved_scenes() if str(r.get("path") or "").replace("\\", "/") != scene]


class SolverContext(_Context):
    """Selftest solvers and E2E agents: the SelftestSolver client plus the private module for shared derivations."""

    def __init__(self, client, scope, seed, project_path, env, lang, track, tier, task, handles=None, log=None,
                 private=None):
        _Context.__init__(self, client, scope, seed, project_path, env, lang, track, tier, task, handles, log)
        self.c = client
        self.client = client
        self.private = private

    def wait_idle(self, **kwargs):
        return self.client.wait_idle(**kwargs)

    def wait_job(self, job_id, **kwargs):
        return self.client.wait_job(job_id, **kwargs)


def context_args(session, state, task, project_path, env=None):
    """Positional args shared by every context, rebuilt from session.json + state.json."""
    selection = session.get("selection") or {}
    return (Scope.from_dict(state["scope"]), state["seed"], project_path,
            env if env is not None else (state.get("env") or {}).get("atIssue") or {},
            session.get("lang"), state.get("track") or selection.get("track"),
            state.get("tier") or selection.get("tier"), task)


def solver_context(client, session, state, task, project_path, private=None, log=None):
    """SolverContext for an issued attempt (selftest driver / E2E agent, contract 5.6)."""
    return SolverContext(client, *context_args(session, state, task, project_path), handles=state.get("handles"),
                         log=log, private=private)

# Producer:Betsy
