"""Headless Claude Code and Codex subject runs with neutral frozen documentation.

The subject gets a fresh empty cwd and a copy of the frozen installed docs, both at neutral
random /tmp paths outside the repo and the Unity project, so neither labels nor eval files are
reachable through its working directory. The whole process group is killed on timeout so no
curl/python child keeps writing to Unity after the attempt ends.
"""
import json
import os
import re
import secrets
import shutil
import signal
import subprocess
import tempfile
import time

SANDBOX_ROOT = "/tmp/eve-s"
DOCS_ROOT = "/tmp/eve-d"
FLAGS = [
    "--output-format", "stream-json", "--verbose",
    "--safe-mode",
    "--strict-mcp-config",
    "--disable-slash-commands",
    "--setting-sources", "project",
    "--dangerously-skip-permissions",
    "--no-session-persistence",
    "--tools", "Bash,Read,Write,Edit",
]
WARM_PREFACE = ("You have already read this project's Unity Editor automation protocol earlier in "
                "this session. Its full text follows; treat it as known context.\n\n")


# Session-linked variables of the controlling Claude Code session, and tuning knobs that would make
# a subject's reasoning/output budget depend on whoever launched the harness instead of --effort.
DROP_ENV = {
    "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_CHILD_SESSION",
    "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
    "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_EXECPATH", "CLAUDE_PID", "CLAUDE_PLUGIN_DATA",
    "CLAUDE_EFFORT", "MAX_THINKING_TOKENS", "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "CLAUDE_AUTOCOMPACT_PCT_OVERRIDE",
    "CLAUDE_CODE_FILE_READ_MAX_OUTPUT_TOKENS", "ENABLE_TOOL_SEARCH", "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS",
    "CLAUDE_CODE_NO_FLICKER", "CLAUDE_CODE_SCROLL_SPEED", "CLAUDE_CODE_SKIP_FAST_MODE_NETWORK_ERRORS",
    "BASH_ENV", "ENV", "ZDOTDIR",
}
SECRET_HINTS = ("TOKEN", "KEY", "SECRET", "PASSWORD")
RECORD_PREFIXES = ("CLAUDE", "ANTHROPIC", "ENABLE_", "MAX_THINKING", "DISABLE_", "BASH_DEFAULT")
CODEX_DISABLE = ("plugins", "apps", "hooks", "memories", "multi_agent", "multi_agent_v2",
                 "browser_use", "browser_use_external", "computer_use", "image_generation",
                 "workspace_dependencies", "skill_search", "skill_mcp_dependency_install")
CLAUDE_AUTH_ENV = ("ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL")


def child_env(host="claude"):
    env = {k: v for k, v in os.environ.items() if k not in DROP_ENV
           and (host != "codex" or not k.startswith(("CODEX_", "CLAUDE_")))}
    if host == "claude":
        source = os.environ.get("CLAUDE_CONFIG_DIR", os.path.expanduser("~/.claude"))
        try:
            with open(os.path.join(source, "settings.json"), "r", encoding="utf-8-sig") as handle:
                settings = json.load(handle)
            configured = settings.get("env") or {}
            if isinstance(configured, dict):
                for key in CLAUDE_AUTH_ENV:
                    if key not in env and isinstance(configured.get(key), str):
                        env[key] = configured[key]
        except (OSError, ValueError, AttributeError):
            pass
    return env


def env_record(host="claude"):
    """Names (and non-secret values) of the host-relevant variables the subject inherits."""
    out = {}
    for key, value in sorted(child_env(host).items()):
        if key.startswith(RECORD_PREFIXES + (("OPENAI",) if host == "codex" else ())):
            out[key] = "<set>" if any(h in key for h in SECRET_HINTS) else value
    return out


def neutral_dirs(frozen_docs):
    token = secrets.token_hex(6)
    sandbox = os.path.join(SANDBOX_ROOT, token)
    docs_parent = os.path.join(DOCS_ROOT, secrets.token_hex(6))
    docs = os.path.join(docs_parent, "docs")
    os.makedirs(sandbox)
    try:
        shutil.copytree(frozen_docs, docs)
    except OSError:
        shutil.rmtree(sandbox, ignore_errors=True)
        shutil.rmtree(docs_parent, ignore_errors=True)
        raise
    return sandbox, docs, docs_parent


def build_command(prompt, model, effort, docs, budget_usd, warm, host="claude"):
    if host == "codex":
        cmd = ["codex", "exec", "--json", "--ephemeral", "--skip-git-repo-check",
               "--ignore-user-config", "--ignore-rules", "--color", "never",
               "--dangerously-bypass-approvals-and-sandbox", "--add-dir", docs,
               "--model", model, "-c", "model_reasoning_effort=" + json.dumps(effort),
               "-c", "project_doc_max_bytes=0", "-c", 'web_search="disabled"',
               "-c", "features.skip_host_skill_discovery=true"]
        for feature in CODEX_DISABLE:
            cmd += ["--disable", feature]
        if warm:
            with open(os.path.join(docs, "SKILL.md"), "r", encoding="utf-8-sig") as handle:
                cmd += ["-c", "developer_instructions=" + json.dumps(WARM_PREFACE + handle.read())]
        return cmd + [prompt]
    if host != "claude":
        raise ValueError("unknown subject host: %s" % host)
    cmd = ["claude", "-p", prompt, "--model", model, "--effort", effort] + FLAGS + \
          ["--max-budget-usd", "%.2f" % budget_usd, "--add-dir", docs]
    if warm:
        with open(os.path.join(docs, "SKILL.md"), "r", encoding="utf-8") as handle:
            cmd += ["--append-system-prompt", WARM_PREFACE + handle.read()]
    return cmd


def redact(cmd, prompt):
    out, skip = [], False
    for token in cmd:
        if skip:
            out.append("<SKILL.md contents>")
            skip = False
        elif token == "--append-system-prompt":
            out.append(token)
            skip = True
        elif token.startswith("developer_instructions="):
            out.append("developer_instructions=<SKILL.md contents>")
        elif token.startswith("model_providers."):
            out.append(token.split("=", 1)[0] + "=<transport configuration>")
        else:
            out.append("<prompt>" if token == prompt else token)
    return out


def run(prompt_template, model, effort, frozen_docs, out_dir, timeout_s, budget_usd, warm=True,
        host="claude"):
    """Run one subject; prompt_template may contain {docs} for the neutral docs path."""
    os.makedirs(out_dir, exist_ok=True)
    sandbox, docs, docs_parent = neutral_dirs(frozen_docs)
    prompt = prompt_template.replace("{docs}", docs)
    cmd = []
    stream_path = os.path.join(out_dir, "subject.stream.jsonl")
    stderr_path = os.path.join(out_dir, "subject.stderr.txt")
    with open(os.path.join(out_dir, "subject.prompt.txt"), "w", encoding="utf-8") as handle:
        handle.write(prompt)
    started = time.time()
    timed_out = False
    codex_home = None
    identity_proxy = None
    launch_error = None
    finalization_errors = []
    isolation_verified = False if host == "codex" else None
    returncode = None
    process_cleanup = {"attempted": False, "ok": True, "errors": []}
    launch_env = child_env(host)
    try:
        cmd = build_command(prompt, model, effort, docs, budget_usd, warm, host=host)
        if host == "codex":
            codex_home, transport, identity_proxy = _codex_home(timeout_s=timeout_s)
            launch_env["CODEX_HOME"] = codex_home
            isolation = _codex_isolation(cmd, sandbox, launch_env, out_dir)
            isolation_verified = True
            # Transport and isolation overrides must precede the positional prompt.
            cmd = cmd[:-1] + isolation + transport + cmd[-1:]
        with open(stream_path, "w", encoding="utf-8") as out, open(stderr_path, "w", encoding="utf-8") as err:
            proc = subprocess.Popen(cmd, cwd=sandbox, stdin=subprocess.DEVNULL, stdout=out, stderr=err, env=launch_env,
                                    start_new_session=True)
            try:
                returncode = proc.wait(timeout=timeout_s)
            except subprocess.TimeoutExpired:
                timed_out = True
                process_cleanup = _kill_group(proc)
                # _kill_group performs bounded waits and captures the leader's final status.
                # Never wait again without a deadline when group cleanup is uncertain.
                returncode = process_cleanup.get("leaderReturnCode")
                if returncode is None:
                    returncode = proc.poll()
    except (OSError, ValueError) as exc:
        launch_error = "%s: %s" % (type(exc).__name__, exc)
        with open(stderr_path, "a", encoding="utf-8") as err:
            err.write(launch_error + "\n")
        open(stream_path, "a", encoding="utf-8").close()
    finally:
        wall = time.time() - started
        outputs = []

        def finalize(label, action):
            # Preserve cleanup certainty even if collection or proxy shutdown fails.
            try:
                return action()
            except Exception as exc:
                finalization_errors.append("%s: %s: %s" % (label, type(exc).__name__, exc))
                return None

        outputs = finalize("collect_outputs", lambda: _collect_outputs(
            sandbox, os.path.join(out_dir, "subject_outputs"))) or []
        finalize("remove_sandbox", lambda: shutil.rmtree(sandbox, ignore_errors=True))
        finalize("remove_docs", lambda: shutil.rmtree(docs_parent, ignore_errors=True))
        if identity_proxy:
            finalize("close_identity_proxy", identity_proxy.close)

            def write_identity():
                with open(os.path.join(out_dir, "subject.identity.json"), "w", encoding="utf-8") as handle:
                    json.dump(identity_proxy.metadata(), handle, indent=2)

            finalize("write_identity", write_identity)
        if codex_home:
            finalize("remove_codex_home", lambda: shutil.rmtree(codex_home, ignore_errors=True))
    answer, answer_error = _read_answer(os.path.join(out_dir, "subject_outputs", "final.json"))
    finalization_errors.extend("collect_outputs %s: %s" % (item["path"], item["error"])
                               for item in outputs if "error" in item)
    if finalization_errors:
        launch_error = "; ".join(([launch_error] if launch_error else []) + finalization_errors)
    return {"returncode": returncode, "timedOut": timed_out, "wallSeconds": round(wall, 2),
            "stream": stream_path, "stderr": stderr_path, "command": redact(cmd, prompt),
            "host": host, "model": model, "effort": effort, "warm": warm,
            "budgetUsd": budget_usd if host == "claude" else None,
            "budgetSupported": host == "claude", "requestedBudgetUsd": budget_usd,
            "hostError": launch_error, "processCleanup": process_cleanup,
            "finalizationErrors": finalization_errors,
            "identity": os.path.join(out_dir, "subject.identity.json") if host == "codex" else None,
            "isolation": os.path.join(out_dir, "subject.isolation.json") if host == "codex" else None,
            "isolationVerified": isolation_verified,
            "timeoutSeconds": timeout_s, "outputs": outputs, "answer": answer, "answerError": answer_error,
            "env": env_record(host),
            "startedAt": started}


def _prompt_strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from _prompt_strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from _prompt_strings(item)


def _skill_inventory(prompt):
    text = "\n".join(_prompt_strings(prompt))
    roots = dict(re.findall(r"^- `(r\d+)` = `([^`]+)`$", text, re.MULTILINE))
    entries = re.findall(r"\(file: (r\d+)/([^\n)]+)\)", text)
    paths = []
    for alias, relative in entries:
        if alias not in roots:
            raise ValueError("Codex skill catalog contains unresolved root: " + alias)
        paths.append(os.path.join(roots[alias], relative))
    if "### Skill roots" in text and not roots:
        raise ValueError("Codex skill catalog roots could not be parsed")
    return roots, paths


def _codex_isolation(cmd, sandbox, launch_env, out_dir):
    """Disable each discovered host skill and verify a local, model-free prompt render.

    debug prompt-input lacks exec's --ignore-user-config. Its equivalent here is the fresh
    CODEX_HOME with no config.toml; both invocations use identical -c and feature overrides.
    Transport overrides are added after this check and cannot affect skill discovery.
    """
    home = launch_env["CODEX_HOME"]
    if os.path.exists(os.path.join(home, "config.toml")):
        raise ValueError("Codex isolation requires an empty user config")
    debug = [cmd[0], "debug", "prompt-input"]
    index = 2
    while index < len(cmd) - 1:
        option = cmd[index]
        if option in ("-c", "--enable", "--disable"):
            debug.extend(cmd[index:index + 2])
            index += 2
        elif option == "--model":
            debug.extend(["-c", "model=" + json.dumps(cmd[index + 1])])
            index += 2
        elif option in ("--color", "--add-dir"):
            index += 2
        else:
            index += 1
    allowed = os.path.realpath(os.path.join(home, "skills", ".system"))

    def is_external(path):
        resolved = os.path.realpath(path)
        return resolved != allowed and not resolved.startswith(allowed + os.sep)

    def render(extra):
        try:
            result = subprocess.run(debug + extra + ["isolation preflight"], cwd=sandbox,
                                    env=launch_env, stdin=subprocess.DEVNULL, capture_output=True,
                                    text=True, timeout=20, check=False)
        except subprocess.TimeoutExpired as exc:
            raise ValueError("Codex isolation prompt render timed out") from exc
        if result.returncode:
            raise ValueError("Codex isolation prompt render failed (rc=%s)" % result.returncode)
        try:
            prompt = json.loads(result.stdout)
        except ValueError as exc:
            raise ValueError("Codex isolation prompt render returned invalid JSON") from exc
        if not isinstance(prompt, list) or not prompt:
            raise ValueError("Codex isolation prompt render returned no prompt messages")
        return _skill_inventory(prompt)

    evidence = {"verified": False, "mechanism": "explicit skill file disables + offline prompt render",
                "allowedSystemRoot": allowed, "disabledPaths": [], "initialRoots": {}, "verifiedRoots": {}}
    try:
        roots, paths = render([])
        evidence["initialRoots"] = roots
        disabled = sorted({path for path in paths if is_external(path)} |
                          {os.path.realpath(path) for path in paths if is_external(path)})
        evidence["disabledPaths"] = disabled
        config = "skills.config=" + _toml_value([{"path": path, "enabled": False} for path in disabled])
        override = ["-c", config]
        verified, _ = render(override)
        if not verified:
            raise ValueError("Codex isolation render has no verifiable skill roots")
        evidence["verifiedRoots"] = verified
        external = [root for root in verified.values() if is_external(root)]
        if external:
            raise ValueError("Codex isolation failed: external skill roots remain: " + ", ".join(external))
        evidence["verified"] = True
        return override
    except (OSError, ValueError) as exc:
        evidence["error"] = "%s: %s" % (type(exc).__name__, exc)
        raise
    finally:
        with open(os.path.join(out_dir, "subject.isolation.json"), "w", encoding="utf-8") as handle:
            json.dump(evidence, handle, indent=2)


def _toml_value(value):
    if isinstance(value, str):
        return json.dumps(value, ensure_ascii=False)
    if isinstance(value, bool):
        return str(value).lower()
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, list):
        return "[" + ",".join(_toml_value(v) for v in value) + "]"
    if isinstance(value, dict):
        return "{" + ",".join(json.dumps(k) + "=" + _toml_value(v) for k, v in value.items()) + "}"
    raise ValueError("unsupported Codex transport setting")


def _codex_home(timeout_s=60):
    """Copy authentication only; carry the selected API transport, never host instructions/settings."""
    import tomllib
    from .codex_proxy import CodexProxy
    source = os.environ.get("CODEX_HOME", os.path.expanduser("~/.codex"))
    provider, entry = "openai", {"name": "OpenAI", "requires_openai_auth": True, "wire_api": "responses"}
    config_path = os.path.join(source, "config.toml")
    if os.path.isfile(config_path):
        with open(config_path, "rb") as handle:
            config = tomllib.load(handle)
        provider = config.get("model_provider") or "openai"
        entry = dict((config.get("model_providers") or {}).get(provider) or entry)
    auth = os.path.join(source, "auth.json")
    if not entry.get("base_url"):
        auth_record = {}
        if os.path.isfile(auth):
            with open(auth, "r", encoding="utf-8") as handle:
                auth_record = json.load(handle)
        chatgpt = auth_record.get("auth_mode") == "chatgpt" or bool(auth_record.get("tokens"))
        entry["base_url"] = os.environ.get("OPENAI_BASE_URL") or (
            "https://chatgpt.com/backend-api/codex" if chatgpt else "https://api.openai.com/v1")
    if not re.fullmatch(r"[A-Za-z0-9_-]+", provider):
        raise ValueError("unsupported Codex provider name")
    target = tempfile.mkdtemp(prefix="eve-c-", dir="/tmp")
    proxy = None
    try:
        if os.path.isfile(auth):
            shutil.copy2(auth, os.path.join(target, "auth.json"))
            os.chmod(os.path.join(target, "auth.json"), 0o600)
        proxy = CodexProxy(entry["base_url"], timeout_s=timeout_s)
        entry["base_url"] = proxy.base_url
        transport = ["-c", "model_provider=" + _toml_value(provider),
                     "-c", "model_providers." + provider + "=" + _toml_value(entry)]
        return target, transport, proxy
    except (OSError, ValueError):
        shutil.rmtree(target, ignore_errors=True)
        if proxy:
            proxy.close()
        raise


def _kill_group(proc, grace_s=5.0, verify_s=2.0, poll_s=0.05):
    """Terminate a subject session, escalate only if its process group remains, and report certainty."""
    result = {"attempted": True, "ok": False, "processGroupId": None, "leaderReturnCode": None,
              "groupGone": None, "errors": []}
    try:
        pgid = os.getpgid(proc.pid)
        result["processGroupId"] = pgid
    except ProcessLookupError:
        # The leader may have exited between wait timeout and cleanup. We cannot infer that
        # descendants exited, so use its original session/group id and inspect that group.
        pgid = proc.pid
        result["processGroupId"] = pgid
    except OSError as exc:
        result["errors"].append("getpgid: %s: %s" % (type(exc).__name__, exc))
        pgid = proc.pid
        result["processGroupId"] = pgid

    def signal_group(sig):
        try:
            os.killpg(pgid, sig)
            return True
        except ProcessLookupError:
            return False
        except PermissionError as exc:
            result["errors"].append("killpg(%s): %s: %s" % (signal.Signals(sig).name,
                                                              type(exc).__name__, exc))
            return None
        except OSError as exc:
            result["errors"].append("killpg(%s): %s: %s" % (signal.Signals(sig).name,
                                                              type(exc).__name__, exc))
            return None

    def group_state():
        try:
            os.killpg(pgid, 0)
            return "present"
        except ProcessLookupError:
            return "gone"
        except PermissionError as exc:
            result["errors"].append("probe_group: %s: %s" % (type(exc).__name__, exc))
            return "unknown"
        except OSError as exc:
            result["errors"].append("probe_group: %s: %s" % (type(exc).__name__, exc))
            return "unknown"

    term_sent = signal_group(signal.SIGTERM)
    try:
        proc.wait(timeout=grace_s)
    except subprocess.TimeoutExpired:
        pass
    except OSError as exc:
        result["errors"].append("wait_after_term: %s: %s" % (type(exc).__name__, exc))

    state = group_state()
    if state != "gone":
        kill_sent = signal_group(signal.SIGKILL)
        # If process-group signaling is denied, still ensure the owned leader is killed;
        # the final group check below will keep the cleanup marked uncertain if descendants remain.
        if kill_sent is None:
            try:
                proc.kill()
            except ProcessLookupError:
                pass
            except OSError as exc:
                result["errors"].append("leader_kill: %s: %s" % (type(exc).__name__, exc))
        try:
            proc.wait(timeout=max(grace_s, verify_s))
        except subprocess.TimeoutExpired:
            result["errors"].append("leader did not exit after SIGKILL fallback")
        except OSError as exc:
            result["errors"].append("wait_after_kill: %s: %s" % (type(exc).__name__, exc))

    deadline = time.monotonic() + verify_s
    while True:
        state = group_state()
        if state in ("gone", "unknown") or time.monotonic() >= deadline:
            break
        time.sleep(poll_s)
    result["groupGone"] = state == "gone"
    result["leaderReturnCode"] = proc.poll()
    if not result["groupGone"]:
        result["errors"].append("process group %s remains or could not be inspected" % pgid)
    result["ok"] = result["groupGone"] and result["leaderReturnCode"] is not None
    return result


def _collect_outputs(sandbox, dest):
    files = []
    if not os.path.isdir(sandbox):
        return files
    def walk_error(exc):
        files.append({"path": os.path.relpath(exc.filename or sandbox, sandbox),
                      "error": "%s: %s" % (type(exc).__name__, exc)})

    for base, _, names in os.walk(sandbox, onerror=walk_error):
        for name in names:
            src = os.path.join(base, name)
            rel = os.path.relpath(src, sandbox)
            try:
                if os.path.islink(src):
                    files.append({"path": rel, "skipped": "symbolic link"})
                    continue
                if os.path.getsize(src) > 2 * 1024 * 1024:
                    files.append({"path": rel, "skipped": "larger than 2 MiB"})
                    continue
                target = os.path.join(dest, rel)
                os.makedirs(os.path.dirname(target), exist_ok=True)
                shutil.copy2(src, target)
                files.append({"path": rel, "bytes": os.path.getsize(target)})
            except OSError as exc:
                # A surviving process can remove or replace a file during collection.
                files.append({"path": rel, "error": "%s: %s" % (type(exc).__name__, exc)})

    return files


def _read_answer(path):
    if not os.path.exists(path):
        return None, "final.json missing"
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            return json.load(handle), None
    except OSError as exc:
        return None, "final.json could not be read: %s" % exc
    except ValueError as exc:
        return None, "final.json is not valid JSON: %s" % exc

# Producer:Betsy
