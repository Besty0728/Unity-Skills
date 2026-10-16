"""Task contract, run context and the shared prompt prefix.

A task module defines one evaluation case: its weighted assertions, how the controller builds
the fixture, the task text, how the verifier reads the end state back, a reference solution,
plausible wrong solutions (negatives, each expected to fail named assertions) and at least one
different legal solution (alternatives). The harness owns everything else.
"""
import hashlib
import json
import random

from . import journal as J
from .verify import Probe, Scorecard

PROMPT_PREFIX = """你正在参与 Unity-Skills 配置适配测试。工程 {project}（Unity {unity}，渲染管线 {pipeline}）已在 Unity Editor 中打开，它的 Unity-Skills REST 服务在 http://localhost:{port}，instanceId 为 {instance}；所有请求都要锁定这个实例。本机还开着另一个 Unity 工程，不得向它发送任何请求。Unity-Skills 协议文档在 {{docs}}/SKILL.md。

请通过已安装 Unity-Skills 的正常文档、发现与调用机制完成任务；允许用 shell 或 Python 作为 HTTP 客户端。只允许修改场景对象根 {object_root}（下文记作 R）之下的对象和资产根 {asset_root}（下文记作 A）之下的资产，题目另有说明的除外。不要切换、重新加载或另存场景；不要修改权限模式、档位或其他工程设置。不要读取评测器实现、参考答案或其他轮报告，不要修改评测配置。遇到权限限制、缺失依赖或编译重载，按工具的正常协议处理，不得绕过。

任务：
{body}

完成后在当前工作目录写 final.json，内容为一个 JSON 对象，格式：
{schema}
然后用一句话说明是否完成并结束。结果只用于触发程序验收，不要自行评分。"""


class Negative(object):
    def __init__(self, name, apply, expect_failed, note=""):
        self.name = name
        self.apply = apply              # fn(ctx) -> answer dict (performs a wrong solution)
        self.expect_failed = set(expect_failed)
        self.note = note


class Context(object):
    def __init__(self, client, instance, scope, seed, project_path, env_before, run_dir):
        self.c = client
        self.client = client
        self.instance = instance
        self.probe = Probe(client)
        self.scope = scope
        self.seed = seed
        self.rng = random.Random(seed)
        self.project_path = project_path
        self.env = env_before
        self.run_dir = run_dir
        self.facts = scope.facts
        self.records = []
        self.journal_complete = True
        self.answer = None

    @property
    def R(self):
        return self.scope.object_root

    @property
    def A(self):
        return self.scope.asset_root

    def path(self, *parts):
        return "/".join((self.R,) + parts)

    def asset(self, *parts):
        return "/".join((self.A,) + parts)

    def violations(self, task):
        return J.scope_violations(self.records, self.R, self.A, self.scope.scene_path,
                                  extra_allowed=lambda rec: task.allowed_change(self, rec))

    def saved_scenes(self):
        return [r for r in self.records if r.get("kind") == "scene_saved"]


class Task(object):
    id = None
    title = ""
    tier = "basic"
    case_version = "1"
    timeout_s = 900
    budget_usd = 8.0
    needs_focus = False         # compile happens while the subject runs
    layer_pairs = ()            # collision pairs snapshotted and restored around the attempt

    def assertions(self):
        raise NotImplementedError

    def build(self, ctx):
        raise NotImplementedError

    def after_fixture(self, ctx):
        """C0 snapshots, taken after the fixture is built and saved, before the solver starts."""
        return None

    def prompt(self, ctx):
        raise NotImplementedError

    def answer_schema(self):
        raise NotImplementedError

    def verify(self, ctx, card):
        raise NotImplementedError

    def reference(self, ctx):
        raise NotImplementedError

    def teardown(self, ctx):
        """Restore anything outside the run folder the fixture changed (e.g. TagManager layer slots)."""
        return None

    def negatives(self):
        return []

    def alternatives(self):
        return []

    def allowed_change(self, ctx, rec):
        return False

    # ------------------------------------------------------------------ shared helpers

    def scorecard(self):
        return Scorecard(self.assertions())

    def full_prompt(self, ctx):
        env = ctx.env or {}
        return PROMPT_PREFIX.format(
            project=env.get("projectName"), unity=env.get("unityVersion"), pipeline=env.get("pipelineType"),
            port=ctx.instance.port, instance=ctx.instance.instance_id, object_root=ctx.R, asset_root=ctx.A,
            body=self.prompt(ctx).strip(), schema=self.answer_schema().strip())

    def fingerprint(self, ctx):
        """Normalised initial state without ids: hierarchy (names, order, components, TRS, flags) and the
        relative paths of the fixture's asset files."""
        return {"hierarchy": hierarchy_signature(ctx.probe, ctx.scope.root_iid),
                "assets": asset_listing(ctx.project_path, ctx.A)}


def hierarchy_signature(probe, root_iid, depth=0, limit=8):
    info = probe.go(root_iid) or {}
    node = {"name": info.get("name"), "active": info.get("isActive"), "tag": info.get("tag"),
            "layer": info.get("layer"), "components": info.get("components"),
            "pos": _round(info.get("position")), "rot": _round(info.get("rotation")),
            "scale": _round(info.get("scale"))}
    if depth < limit:
        node["children"] = [hierarchy_signature(probe, c["instanceId"], depth + 1, limit)
                            for c in info.get("children") or []]
    return node


def asset_listing(project_path, asset_root):
    import os
    root = os.path.join(project_path, asset_root)
    out = []
    for base, _dirs, files in os.walk(root):
        for name in files:
            if name.endswith(".meta") or name.startswith("Eval_"):
                continue
            out.append(os.path.relpath(os.path.join(base, name), root).replace(os.sep, "/"))
    return sorted(out)


def _round(vec):
    if not isinstance(vec, dict):
        return vec
    return {k: round(float(v), 4) for k, v in vec.items()}


def digest(obj):
    return hashlib.sha256(json.dumps(obj, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()

# Producer:Betsy
