"""Prompt prefix v2, prompt-file parsing and rendering, answer schema, session prompts, command lines (2.5, 5.3, 5.4, 6)."""
import json
import os
import re
import shutil
import sys

from . import RUNNER_SCRIPT

LANGS = ("zh-CN", "en")
BUILTINS = ("session", "k", "n", "track", "tier", "project", "unity", "pipeline", "port", "instance", "R", "A",
            "scene", "minutes", "submit_cmd", "requirements", "schema")
DECLARED_KEY = "declaredComplete"
SCALARS = ("string", "integer", "number", "boolean")
TYPES = SCALARS + ("object", "array")
MAX_SCHEMA_ERRORS = 20

PREFIX_ZH = """你正在参加 UnitySkills 评测（会话 {session}，第 {k}/{n} 题，赛道 {track}，难度 {tier}）。工程 {project}（Unity {unity}，{pipeline}）已在 Unity Editor 中打开；UnitySkills REST 服务 http://127.0.0.1:{port}，instanceId {instance}。
通用规则：
G1 只允许修改场景对象根 `{R}`（下称 R）下的对象和资产根 `{A}`（下称 A）下的资产，题目另有明确要求的除外。
G2 可以保存本轮场景 `{scene}`；不要打开、切换、重新加载、新建、另存或保存其他任何场景。
G3 不要修改操作模式、档位、dryRun 策略、已安装的包或工程设置，题目另有明确要求的除外。
G4 [ops] 场景和资产的修改只能通过 UnitySkills 技能完成：不要直接写 .unity/.prefab/.mat/.asset/.meta 文件，不要新建或修改 C# 脚本。
   [dev] 可以在 A/Scripts 下新建或修改 C# 脚本；其他场景和资产修改只能通过 UnitySkills 技能完成。
G5 每个请求都锁定实例 {instance}（X-Expect-Instance 头或 unity_skills.py 的 target）；不要向其他端口发请求。
G6 不要读取或修改评测程序、它的状态目录 Library/UnitySkills/eval、验收文件或其他报告，不要尝试获取参考答案。
G7 遇到权限限制、缺少依赖或编译重载，按 UnitySkills 文档的正常协议处理，不得绕过。
G8 建议用时 {minutes} 分钟。完成（或确认无法完成）后，按下方格式把答案写成 JSON 文件，然后只运行一次：
   {submit_cmd}
   提交只触发程序验收，不返回分数，请不要自行评分；无法完成时把 declaredComplete 写为 false 并照常提交。
任务：
{requirements}
答案格式（JSON）：{schema}"""

PREFIX_EN = """You are taking a UnitySkills evaluation (session {session}, task {k}/{n}, track {track}, tier {tier}). Project {project} (Unity {unity}, {pipeline}) is open in the Unity Editor; UnitySkills REST server http://127.0.0.1:{port}, instanceId {instance}.
General rules:
G1 Modify only objects under the scene root `{R}` (R) and assets under `{A}` (A), unless the task explicitly says otherwise.
G2 You may save this run's scene `{scene}`. Do not open, switch, reload, create, save-as or save any other scene.
G3 Do not change the operating mode, token tier, dryRun policy, installed packages or project settings unless the task explicitly says so.
G4 [ops] Make scene and asset changes only through UnitySkills skills: do not write .unity/.prefab/.mat/.asset/.meta files directly; do not create or edit C# scripts.
   [dev] You may create or edit C# scripts under A/Scripts; make every other scene and asset change through UnitySkills skills.
G5 Pin every request to instance {instance} (X-Expect-Instance header or unity_skills.py target); send nothing to other ports.
G6 Do not read or modify the evaluation runner, its state directory Library/UnitySkills/eval, verification files or other reports; do not try to obtain reference solutions.
G7 Handle permission limits, missing dependencies and compile reloads through the normal UnitySkills protocol; never bypass them.
G8 Suggested time: {minutes} minutes. When done (or sure you cannot finish), write the answer as a JSON file in the format below, then run exactly once:
   {submit_cmd}
   Submitting only triggers program verification and returns no score; do not grade yourself. If you cannot finish, set declaredComplete to false and submit anyway.
Task:
{requirements}
Answer format (JSON): {schema}"""

SESSION_ZH_SUITE = """这是一次 UnitySkills 评测（会话 {session}，整套模式，共 {n} 题，赛道 {track}，难度 {tier}）。请严格按以下流程进行：
1. 运行：{next_cmd}
2. 如果返回的 data.done 为 true：告诉用户评测已完成，请到 Unity 中 UnitySkills 窗口的“评测”页查看报告，然后结束。
3. 否则读取 data.promptFile（UTF-8 文本），按其中的通用规则和任务完成本题，并按其中的提交命令提交一次。
4. 提交后回到第 1 步。不要回头修改已经提交的题。
任何命令返回的 problems 不为空时，把每一条的 fix 原样转告用户，然后停止。"""

SESSION_ZH_STRICT = """这是一次 UnitySkills 评测（会话 {session}，逐题模式，共 {n} 题，赛道 {track}，难度 {tier}）。本会话只做一题：
1. 运行：{next_cmd}
2. 如果返回的 data.done 为 true：告诉用户评测已完成，请到 Unity 中 UnitySkills 窗口的“评测”页查看报告，然后结束。
3. 否则读取 data.promptFile（UTF-8 文本），按其中的通用规则和任务完成本题，并按其中的提交命令提交一次。
4. 提交后停止，并提醒用户：新开一个会话，粘贴同一段提示，继续下一题。
任何命令返回的 problems 不为空时，把每一条的 fix 原样转告用户，然后停止。"""

SESSION_EN_SUITE = """This is a UnitySkills evaluation (session {session}, suite mode, {n} tasks, track {track}, tier {tier}). Follow this procedure exactly:
1. Run: {next_cmd}
2. If the returned data.done is true, tell the user that the evaluation is finished and that the report is on the "Evaluation" tab of the UnitySkills window in Unity, then stop.
3. Otherwise read data.promptFile (UTF-8 text), complete the task under its general rules, and submit once with the submit command it gives.
4. After submitting, go back to step 1. Do not revisit tasks you have already submitted.
If any command returns a non-empty problems list, pass each fix on to the user verbatim, then stop."""

SESSION_EN_STRICT = """This is a UnitySkills evaluation (session {session}, strict mode, {n} tasks, track {track}, tier {tier}). Do exactly one task in this session:
1. Run: {next_cmd}
2. If the returned data.done is true, tell the user that the evaluation is finished and that the report is on the "Evaluation" tab of the UnitySkills window in Unity, then stop.
3. Otherwise read data.promptFile (UTF-8 text), complete the task under its general rules, and submit once with the submit command it gives.
4. After submitting, stop and remind the user to open a new session and paste this same prompt to continue with the next task.
If any command returns a non-empty problems list, pass each fix on to the user verbatim, then stop."""

PREFIX = {"zh-CN": PREFIX_ZH, "en": PREFIX_EN}
SESSION = {("zh-CN", "suite"): SESSION_ZH_SUITE, ("zh-CN", "strict"): SESSION_ZH_STRICT,
           ("en", "suite"): SESSION_EN_SUITE, ("en", "strict"): SESSION_EN_STRICT}

_REQ_RE = re.compile(r"^R(\d+)\. ")
_COMMENT_RE = re.compile(r"^\s*<!--.*-->\s*$")


def prompt_lang(lang):
    return lang if lang in LANGS else "en"


# ---------------------------------------------------------------- task prompt


def prefix_template(lang, track):
    """The prefix with only this track's G4 line, marker and continuation indent removed (contract 6.3)."""
    lines = PREFIX[prompt_lang(lang)].split("\n")
    out, index = [], 0
    while index < len(lines):
        line = lines[index]
        if line.startswith("G4 [ops] ") and index + 1 < len(lines) and lines[index + 1].startswith("   [dev] "):
            ops, dev = line[len("G4 [ops] "):], lines[index + 1][len("   [dev] "):]
            out.append("G4 " + (dev if track == "dev" else ops))
            index += 2
            continue
        out.append(line)
        index += 1
    return "\n".join(out)


def parse_prompt(text):
    """Requirement lines of a prompt.<lang>.md: optional first-line comment, R1.. Rn, continuation lines."""
    if text.startswith("\ufeff"):
        text = text[1:]
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    if lines and _COMMENT_RE.match(lines[0]):
        lines = lines[1:]
    out, expected = [], 1
    for line in lines:
        if not line.strip():
            continue
        match = _REQ_RE.match(line)
        if match:
            if int(match.group(1)) != expected:
                raise ValueError("requirement R%d out of order (expected R%d)" % (int(match.group(1)), expected))
            expected += 1
        elif not out:
            raise ValueError("prompt must start with R1.")
        out.append(line.rstrip())
    if not out:
        raise ValueError("prompt has no requirements")
    return out


class _StrictVars(dict):
    def __missing__(self, key):
        raise ValueError("unknown placeholder {%s}" % key)


def render_requirements(lines, variables):
    try:
        return "\n".join(lines).format_map(_StrictVars(variables))
    except (IndexError, KeyError) as exc:
        raise ValueError("bad placeholder in prompt: %s" % exc)


def merge_vars(builtins, task_vars):
    task_vars = dict(task_vars or {})
    clash = sorted(set(task_vars) & set(BUILTINS))
    if clash:
        raise ValueError("prompt_vars overrides built-in placeholders: %s" % ", ".join(clash))
    for key, value in task_vars.items():
        if not isinstance(value, (str, int, float)) or isinstance(value, bool):
            raise ValueError("prompt_vars[%s] must be str, int or float" % key)
    merged = dict(builtins)
    merged.update(task_vars)
    return merged


def render_prompt(lang, track, builtins, requirements_text, schema_text, submit_line):
    values = dict(builtins)
    values.update({"requirements": requirements_text, "schema": schema_text, "submit_cmd": submit_line})
    return prefix_template(lang, track).format_map(values)


# ---------------------------------------------------------------- answer schema (contract 5.4)


def with_declared(schema):
    out = dict(schema or {})
    out["type"] = "object"
    props = dict(out.get("properties") or {})
    props.pop(DECLARED_KEY, None)
    props[DECLARED_KEY] = {"type": "boolean"}
    out["properties"] = props
    required = [r for r in (out.get("required") or []) if r != DECLARED_KEY]
    out["required"] = required + [DECLARED_KEY]
    return out


def _desc(node, lang):
    desc = node.get("desc")
    if isinstance(desc, dict):
        desc = desc.get(prompt_lang(lang)) or desc.get("en")
    return desc if isinstance(desc, str) and desc else None


def render_schema(schema, lang):
    """Single-line JSON-like rendering with English type words and descriptions in the session language."""

    def node(spec):
        spec = spec if isinstance(spec, dict) else {}
        kind = spec.get("type")
        if kind == "object":
            props = spec.get("properties") or {}
            return "{" + ", ".join("%s: %s" % (json.dumps(k, ensure_ascii=False), node(v))
                                   for k, v in props.items()) + "}"
        if kind == "array":
            return "[%s, ...]" % node(spec.get("items"))
        word = kind if kind in SCALARS else "value"
        desc = _desc(spec, lang)
        inner = "<%s%s>" % (word, ": " + desc if desc else "")
        return '"%s"' % inner if word == "string" else inner

    return node(schema)


def _type_ok(kind, value):
    if kind == "string":
        return isinstance(value, str)
    if kind == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if kind == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if kind == "boolean":
        return isinstance(value, bool)
    if kind == "object":
        return isinstance(value, dict)
    if kind == "array":
        return isinstance(value, list)
    return True


def _type_name(value):
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, int):
        return "integer"
    if isinstance(value, float):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    if isinstance(value, dict):
        return "object"
    return "null"


def validate_answer(schema, value):
    """Non-blocking checks: object at top level, required keys, types (integer rejects bool and fractions)."""
    errors = []

    def walk(spec, item, where):
        if len(errors) >= MAX_SCHEMA_ERRORS or not isinstance(spec, dict):
            return
        kind = spec.get("type")
        if kind in TYPES and not _type_ok(kind, item):
            errors.append("%s: expected %s, got %s" % (where or "answer", kind, _type_name(item)))
            return
        if kind == "object":
            for key in spec.get("required") or []:
                if key not in item and len(errors) < MAX_SCHEMA_ERRORS:
                    errors.append("%s: missing required key %s" % (where or "answer", key))
            for key, sub in (spec.get("properties") or {}).items():
                if key in item:
                    walk(sub, item[key], "%s.%s" % (where, key) if where else key)
        elif kind == "array":
            for index, sub in enumerate(item):
                walk(spec.get("items"), sub, "%s[%d]" % (where or "answer", index))

    walk(schema, value, "")
    return errors[:MAX_SCHEMA_ERRORS]


# ---------------------------------------------------------------- session prompt (contract 6.4)


def session_prompt(lang, mode, values):
    return SESSION[(prompt_lang(lang), mode)].format_map(values)


# ---------------------------------------------------------------- command lines (contract 2.5)

_SAFE_POSIX = re.compile(r"^[A-Za-z0-9_\-.,:/=+@%]+$")
_SAFE_WIN = re.compile(r"^[A-Za-z0-9_\-.,:/=+@%\\]+$")


def _short_path(path):
    try:
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.GetShortPathNameW.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
        kernel32.GetShortPathNameW.restype = wintypes.DWORD
        size = kernel32.GetShortPathNameW(path, None, 0)
        if not size:
            return None
        buffer = ctypes.create_unicode_buffer(size)
        if not kernel32.GetShortPathNameW(path, buffer, size):
            return None
        return buffer.value
    except Exception:
        return None


def python_for_line(executable=None, platform=None):
    executable = executable or sys.executable
    platform = platform or sys.platform
    real = os.path.normcase(os.path.realpath(executable))
    for name in ("python3", "python"):
        found = shutil.which(name)
        if found and os.path.normcase(os.path.realpath(found)) == real:
            return name
    if platform == "win32" and re.search(r"\s", executable):
        executable = _short_path(executable) or executable
    return executable


def quote(arg, platform=None):
    platform = platform or sys.platform
    if platform == "win32":
        while len(arg) > 3 and arg.endswith("\\"):
            arg = arg[:-1]
    if arg and (_SAFE_WIN if platform == "win32" else _SAFE_POSIX).match(arg):
        return arg
    return '"' + arg.replace('"', '\\"') + '"'


def entry(environ):
    return environ.get("USK_EVAL_ENTRY") or RUNNER_SCRIPT


def render_command(argv, platform=None):
    return " ".join(quote(str(a), platform) for a in argv)


def next_step(command, args, environ):
    """NextStep {command, line, argv}: line pastes into bash/zsh/PowerShell/cmd, argv is for programs."""
    tail = [command] + [str(a) for a in args]
    line = render_command([python_for_line(), "-B", entry(environ)] + tail)
    return {"command": command, "line": line, "argv": [sys.executable, "-B", entry(environ)] + tail}

# Producer:Betsy
