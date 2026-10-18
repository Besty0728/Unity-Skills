"""Problem codes (contract 3): exit codes, English messages and fix templates with positional args."""

EXIT_OK = 0

# code: (exit code, message, fix template)
BLOCKING = {
    "PYTHON_TOO_OLD": (2, "Python 3.9 or newer is required",
                       "Install Python 3.9 or newer and run the command with it."),
    "PROJECT_NOT_FOUND": (2, "No Unity project was found",
                          "Run the command inside the Unity project folder, or pass --project <path to the project>."),
    "PROJECT_AMBIGUOUS": (2, "Several running Unity editors match and the current directory belongs to none of them",
                          "Several Unity editors are running; pass --project <path to the project>."),
    "PACKAGE_NOT_FOUND": (2, "The com.besty.unity-skills package root was not found",
                          "Install com.besty.unity-skills in this project and let Unity finish resolving packages."),
    "RUNNER_NOT_FOUND": (2, "The installed package has no Evaluation~/run_eval.py",
                         "The installed UnitySkills package has no evaluation runner; update UnitySkills to 3.0 or newer."),
    "INSTANCE_NOT_RUNNING": (2, "No running UnitySkills server answers for this project",
                             "Open the project in Unity and make sure the UnitySkills server is running "
                             "(Window > UnitySkills)."),
    "INSTANCE_MISMATCH": (2, "The answering editor is not the one registered for this project",
                          "Another editor answers for this project; close duplicate editors and restart the "
                          "UnitySkills server."),
    "VERSION_MISMATCH": (2, "The editor runs a different UnitySkills version than this runner",
                         "The editor runs UnitySkills {0} but this runner is {1}; wait until Unity finishes importing "
                         "the package (or restart the editor), then retry."),
    "EDITOR_BUSY": (2, "Unity is compiling or importing",
                    "Wait until Unity finishes compiling and importing, then retry."),
    "PLAY_MODE_ACTIVE": (2, "Unity is in Play Mode", "Exit Play Mode in Unity, then retry."),
    "MODE_NOT_BYPASS": (2, "The operating mode is not the Bypass mode the tasks require",
                        "Switch the UnitySkills operating mode to Bypass (Evaluation tab: Switch to Bypass; it is "
                        "restored when the session ends)."),
    "SURFACE_NOT_FULL": (2, "The skill surface is not Full",
                         "Set the UnitySkills skill surface to Full in the settings drawer."),
    "PIPELINE_UNSUPPORTED": (2, "The render pipeline does not match the selected tasks",
                             "Use a project whose render pipeline is one the selected tasks support ({0})."),
    "PROJECT_NOT_DEDICATED": (2, "The selected tasks need a dedicated evaluation project",
                              "Mark an empty URP project as the dedicated evaluation project in the Evaluation tab, "
                              "or choose the basic tier."),
    "PACKAGE_MISSING": (2, "A package required by a selected task is not installed",
                        "Install the package {0} required by task {1}, or choose other tasks."),
    "TMP_RESOURCES_MISSING": (2, "TextMesh Pro is installed but its Essential Resources were never imported",
                              "Import the TMP Essential Resources: in the Unity editor choose Window > TextMeshPro > "
                              "Import TMP Essential Resources and click Import; or, with the editor closed, run "
                              "Unity -batchmode -projectPath \"{1}\" -importPackage \"{2}\" -quit. The selected tasks "
                              "({0}) build uGUI, whose UI skills create TextMesh Pro text; without the resources "
                              "TextMesh Pro then opens its importer window, a batchmode or -nographics editor logs "
                              "errors for it, and it logs them again on every Play Mode entry."),
    "SCENE_DIRTY": (2, "A loaded scene has unsaved changes", "Save or discard the unsaved changes in {0}, then retry."),
    "SCENE_UNTITLED": (2, "The active scene has no file path",
                       "Open or save a scene that has a file path, then retry."),
    "RESIDUE_FOUND": (2, "Leftover evaluation run data exists",
                      "Leftover evaluation data found ({0}); click Clean up in the Evaluation tab or run cleanup "
                      "--stale."),
    "PRIVATE_PACK_TLS": (2, "Every download of the verification files failed TLS certificate verification",
                         "TLS certificate verification failed; install the root certificates for your Python, or "
                         "download the private pack zip from the release page and pass --private-zip <zip>."),
    "PRIVATE_PACK_UNREACHABLE": (2, "The verification files could not be downloaded",
                                 "Allow access to raw.githubusercontent.com or cdn.jsdelivr.net, or download the "
                                 "private pack zip from the release page and pass --private-zip <zip>."),
    "VERIFICATION_PENDING": (2, "The attempt is waiting for its verification files",
                             "Verification files could not be downloaded. Restore network access (or get the private "
                             "pack zip), then run: {0}"),
    "PACK_PLUGIN_RANGE": (2, "The installed UnitySkills version is outside the task pack's supported range",
                          "This task pack needs UnitySkills {0} to {1}; update the package."),
    "CLEANUP_NEEDS_SCENE": (2, "A run scene is open and no resting scene was recorded for it",
                            "Open one of your own scenes in Unity, then run cleanup again."),
    "BAD_ARGUMENTS": (3, "Invalid command line", "Check the command line: {0}"),
    "SESSION_ACTIVE": (3, "Another evaluation session is still active",
                       "Session {0} is still running; continue it with next, or abort it first."),
    "NO_ACTIVE_SESSION": (3, "There is no active evaluation session",
                          "Start an evaluation session first (Evaluation tab or the start command)."),
    "SESSION_NOT_FOUND": (3, "The session does not exist", "Use a session id listed in the Evaluation tab."),
    "SESSION_NOT_FINISHED": (3, "The session has not finished",
                             "The report is available after the last task is submitted or the session is aborted."),
    "ATTEMPT_NOT_FOUND": (3, "The attempt does not exist", "Use the attempt id returned by next."),
    "ATTEMPT_NOT_ACTIVE": (3, "The attempt is not the current task",
                           "Attempt {0} is not the current task; run next to get the current task."),
    "ATTEMPT_NOT_READY": (3, "The attempt is still being prepared",
                          "The task is still being prepared; run next and wait for its prompt."),
    "ANSWER_NOT_FOUND": (3, "The answer file does not exist",
                         "Write the answer JSON to {0}, then run the same submit command again."),
    "RETRY_NOT_APPLICABLE": (3, "The attempt is not waiting for verification",
                             "Only attempts that are waiting for verification can be retried."),
    "STRICT_NEEDS_NEW_SESSION": (3, "This agent session already did a task in strict mode",
                                 "This agent session has already done one task. Open a new agent session and paste "
                                 "the same session prompt."),
    "TASKS_INVALID": (3, "The task selection is invalid", "Fix the task selection: {0}"),
    "REST_UNREACHABLE": (4, "Unity did not answer within the retry window",
                         "Unity stopped answering; make sure the editor is open and responsive, then retry."),
    "REST_ERROR": (4, "Unity rejected a runner request",
                   "Unity rejected an evaluation request ({0}); see the runner log {1}."),
    "FIXTURE_FAILED": (4, "Preparing the task failed",
                       "Preparing the task failed; run next again. If it fails again, report the runner log {0}."),
    "TEARDOWN_FAILED": (4, "Cleaning up the task failed",
                        "Cleaning up the task failed; click Clean up in the Evaluation tab or run cleanup --stale."),
    "PACK_INVALID": (4, "The evaluation pack is invalid",
                     "The evaluation pack is damaged; reinstall the UnitySkills package."),
    "STATE_CORRUPT": (4, "An evaluation state file is damaged",
                      "Evaluation state is damaged; abort the session in the Evaluation tab. If that fails, delete "
                      "Library/UnitySkills/eval."),
    "SCHEMA_UNSUPPORTED": (4, "An evaluation state file was written by a newer runner",
                           "Evaluation state was written by a newer UnitySkills; update the package."),
    "IO_ERROR": (4, "A file could not be read or written", "Could not read or write {0}: {1}"),
    "INTERNAL_ERROR": (4, "Unexpected runner error", "Unexpected runner error; see the runner log {0} and report it."),
    "LOCK_TIMEOUT": (5, "Another evaluation command holds the lock",
                     "Another evaluation command is still running (pid {0}: {1}); wait for it to finish, then retry."),
}

# Non-blocking notices (data.notices); the message doubles as the fix.
NOTICES = {
    "SESSION_ACTIVE": BLOCKING["SESSION_ACTIVE"][2],
    "TELEMETRY_DISABLED": "Telemetry is off, so skill-call evidence will be missing. Turn it on in the UnitySkills "
                          "settings.",
    "SKILL_DOCS_OUTDATED": "The installed skill docs at {0} are version {1}; reinstall them from the AI Config tab.",
    "SKILL_DOCS_MISSING": "No installed UnitySkills skill docs were found; install them from the AI Config tab.",
    "SETTINGS_NONSTANDARD": "The dryRun policy ({0}) or token level ({1}) differs from the reference setup "
                            "(off / maximum); results are less comparable.",
    "PACK_MODIFIED": "Pack files differ from the manifest ({0}); this session is marked unofficial.",
    "PROJECT_USER": "This is not a dedicated evaluation project; the report marks the environment as user-project.",
    "PACKAGE_ROOT_AMBIGUOUS": "Several cached copies of the package exist; using {0}.",
    "ISOLATION_UNVERIFIED": "The agent session cannot be identified, so strict-mode isolation is not verified.",
    "RERUN": "The previous attempt of this task was invalid for technical reasons; please do the task again.",
    "USAGE_UNAVAILABLE": "Token usage could not be read ({0}); you can enter it in the Evaluation tab.",
}


def _format(template, args):
    padded = list(args) + [""] * 4
    try:
        return template.format(*padded)
    except (IndexError, KeyError, ValueError):
        return template


def problem(code, args=(), detail=None, hint=None, fix=None):
    """A blocking problem. `fix` replaces the table text (PRIVATE_PACK_TLS uses private.TLS_HINT); `hint`, one more
    English sentence such as private.fix_hint(attempts), is appended to it."""
    exit_code, message, template = BLOCKING[code]
    args = [str(a) for a in args]
    fix = fix or _format(template, args)
    if hint:
        fix = "%s %s" % (fix, hint)
    out = {"code": code, "message": message + (": " + str(detail) if detail else ""), "fix": fix}
    if args:
        out["args"] = args
    return out


def notice(code, args=()):
    args = [str(a) for a in args]
    text = _format(NOTICES[code], args)
    out = {"code": code, "message": text, "fix": text}
    if args:
        out["args"] = args
    return out


def store_problem(exc):
    """The problem for a store.StoreError (STATE_CORRUPT, SCHEMA_UNSUPPORTED or IO_ERROR)."""
    if exc.code == "IO_ERROR":
        return problem("IO_ERROR", [exc.path, exc.detail])
    return problem(exc.code, detail="%s %s" % (exc.path, exc.detail))


def exit_code(problems):
    return max([BLOCKING.get(p.get("code"), (4,))[0] for p in problems] or [EXIT_OK])


class ProblemError(Exception):
    """Ends a command with blocking problems; data (optional) still goes into the envelope."""

    def __init__(self, problems, data=None, next_step=None):
        if isinstance(problems, dict):
            problems = [problems]
        Exception.__init__(self, ", ".join(p["code"] for p in problems))
        self.problems = list(problems)
        self.data = data
        self.next_step = next_step


class Outcome(object):
    """What a command returns: envelope data, blocking problems and the NextStep."""

    def __init__(self, data=None, problems=(), next_step=None):
        self.data = data
        self.problems = list(problems)
        self.next_step = next_step


def fail(code, args=(), detail=None):
    raise ProblemError(problem(code, args, detail))

# Producer:Betsy
