"""Bounded execution/verification loop; repair decisions belong to the caller."""

import json
import time


class VerificationInterrupted(RuntimeError):
    """Execution may have happened; never automatically replay it."""


def run_verified_scenario(client, steps, repair_callback=None, max_repairs=3,
                          timeout_seconds=1800, execution_mode="play", compilation_job_id=None):
    """Compile, assert behavior, then optionally repair and rerun.

    repair_callback(evidence, repair_number, remaining_seconds) may edit relevant
    files/assets and return replacement steps (or None to retain the steps).
    It must obey the remaining budget; this helper cannot kill a blocked callback.
    Permission failures and uncertain execution are never repair/replay triggers.
    """
    if not 0 <= max_repairs <= 3 or not 0 < timeout_seconds <= 1800:
        raise ValueError("Limits are 0..3 repairs and 0..1800 seconds.")
    if execution_mode not in ("play", "edit"):
        raise ValueError("execution_mode must be play or edit")
    evidence_not_before = time.time() - 60
    deadline = time.monotonic() + timeout_seconds
    attempts = []
    active_job = None
    active_pending = False
    original_play = None

    def remaining():
        budget = deadline - time.monotonic()
        if budget <= 0:
            raise VerificationInterrupted("Total verification deadline expired.")
        return budget

    def execute(skill, **arguments):
        nonlocal active_job, active_pending
        preview = client.dry_run_skill(skill, timeout=max(1, min(30, remaining())),
                                       _retries=0, **arguments)
        if preview.get("success") is False or preview.get("valid") is False or preview.get("error"):
            raise VerificationInterrupted("Preview refused: " + str(preview))
        result = client.call(skill, timeout=max(1, min(30, remaining())), _retries=0,
                             dry_run_token=preview.get("dryRunToken"), **arguments)
        if not result.get("success"):
            raise VerificationInterrupted("Call refused or execution uncertain: " + str(result))
        active_job = result.get("jobId")
        if not active_job:
            return result
        active_pending = True
        result = client.wait_for_job(active_job, timeout=remaining())
        active_pending = result.get("status") not in ("completed", "failed", "cancelled")
        if result.get("timedOut") or result.get("status") in ("cancelled", "interrupted") or result.get("currentStage") in ("interrupted", "timeout") or (result.get("details") or {}).get("executionUncertain"):
            raise VerificationInterrupted(str(result))
        if result.get("status") not in ("completed", "failed"):
            raise VerificationInterrupted("Job completion uncertain: " + str(result))
        return result

    def validate_steps(value):
        if not isinstance(value, list) or not any(
                step.get("kind") in ("assert", "wait_condition") for step in value):
            raise ValueError("A scenario requires an explicit behavioral assertion.")

    def prepare_supplied_compilation(job_id):
        nonlocal active_job, active_pending, original_play
        active_job, active_pending = job_id, True
        while True:
            snapshot = client.get_job(job_id, _timeout=min(5, remaining()))
            if snapshot.get("jobId") == job_id:
                if snapshot.get("kind") != "compile" or snapshot.get("startedAt", 0) < evidence_not_before:
                    raise VerificationInterrupted("Supplied compilation job is stale or is not a compile job.")
                if snapshot.get("terminal") or snapshot.get("status") in ("completed", "failed", "cancelled"):
                    return
                if snapshot.get("currentStage") == "waiting_play_mode_exit":
                    state = client.call("editor_get_state", _retries=0,
                                        timeout=max(1, min(30, remaining())))
                    if not state.get("success"):
                        raise VerificationInterrupted("Cannot establish mode for deferred compilation: " + str(state))
                    if original_play is None:
                        original_play = bool(state.get("isPlaying"))
                    if state.get("isPlaying"):
                        execute("editor_stop")
                        active_job, active_pending = job_id, True
            elif not snapshot.get("retryable"):
                raise VerificationInterrupted("Cannot recover accepted compilation job: " + str(snapshot))
            time.sleep(min(0.5, remaining()))

    validate_steps(steps)
    report = {"status": "unverified", "attempts": attempts, "repairCount": 0}
    try:
        if compilation_job_id:
            prepare_supplied_compilation(compilation_job_id)
        state = client.call("editor_get_state", _retries=0, timeout=max(1, min(30, remaining())))
        if not state.get("success"):
            raise VerificationInterrupted("Cannot establish original Editor mode: " + str(state))
        if original_play is None:
            original_play = bool(state.get("isPlaying"))
        for repair_number in range(max_repairs + 1):
            remaining()
            evidence = {"attempt": repair_number + 1}
            attempts.append(evidence)
            if compilation_job_id and repair_number > 0:
                prepare_supplied_compilation(compilation_job_id)
            state = client.call("editor_get_state", _retries=0, timeout=max(1, min(30, remaining())))
            if not state.get("success"):
                raise VerificationInterrupted("Cannot establish mode before compilation.")
            if state.get("isPlaying"):
                execute("editor_stop")
            if compilation_job_id:
                active_job = compilation_job_id
                active_pending = True
                compilation = client.wait_for_job(active_job, timeout=remaining())
                active_pending = compilation.get("status") not in ("completed", "failed", "cancelled")
                snapshot = compilation.get("snapshot") or {}
                if snapshot.get("kind") != "compile" or snapshot.get("startedAt", 0) < evidence_not_before:
                    raise VerificationInterrupted("Supplied compilation job is stale or is not a compile job.")
                if compilation.get("timedOut") or active_pending:
                    raise VerificationInterrupted("Compilation job completion is uncertain: " + str(compilation))
                compilation_job_id = None
            else:
                compilation = execute("debug_force_recompile")
            evidence["compilation"] = compilation
            details = (compilation.get("details") or {}).get("compilation") or {}
            # A new successful cycle and loaded assemblies are mandatory evidence.
            compiled = compilation.get("success") and details.get("compiled") is True and details.get("success") is True and details.get("cycleId") is not None \
                and details.get("domainLoaded") is True
            if not compiled and compilation.get("currentStage") == "no_compilation":
                report.update(status="unverified", error="No new assembly compilation occurred; supply a fresh script-mutation compile job.")
                break
            if compiled:
                scenario = execute("editor_run_scenario", steps=json.dumps(steps),
                                   executionMode=execution_mode,
                                   timeoutSeconds=max(1, min(600, int(remaining()))))
                evidence["scenario"] = scenario
                data = scenario.get("details") or {}
                if data.get("interrupted") or scenario.get("currentStage") == "interrupted":
                    raise VerificationInterrupted("Scenario interrupted; no automatic replay.")
                if scenario.get("success") and data.get("passed") is True:
                    report["status"] = "passed"
                    break
                # Permission errors inside a scenario are not code defects.
                encoded = json.dumps(data)
                if any(code in encoded for code in ("PERMISSION_DENIED", "CONFIRMATION_REQUIRED",
                                                     "DRYRUN_REQUIRED", "MODE_RESTRICTED", "MODE_FORBIDDEN",
                                                     "SURFACE_EXCLUDED", "GRANT_PENDING_APPROVAL", "INVALID_TOKEN")):
                    raise VerificationInterrupted("Scenario permission gate refused execution.")
            if repair_callback is None or repair_number == max_repairs:
                report["status"] = "failed"
                break
            replacement = repair_callback(evidence, repair_number + 1, remaining())
            remaining()
            report["repairCount"] += 1
            if replacement is not None:
                if isinstance(replacement, dict):
                    compilation_job_id = replacement.get("compilationJobId")
                    replacement = replacement.get("steps", steps)
                    evidence_not_before = time.time() - 60
                validate_steps(replacement)
                steps = replacement
    except VerificationInterrupted as error:
        report.update(status="interrupted", error=str(error), jobId=active_job)
    except Exception as error:
        report.update(status="failed", error=str(error), jobId=active_job)
    finally:
        if active_pending and active_job:
            try:
                cancelled = client.call("job_cancel", jobId=active_job, _retries=0, timeout=5)
                report["cancellation"] = cancelled
                if not cancelled.get("success") or cancelled.get("status") not in ("cancelled", "completed", "failed"):
                    report["cleanupError"] = "Active job cancellation was not confirmed."
            except Exception as error:
                report["cleanupError"] = "Active job cancellation uncertain: " + str(error)
        # Restore initial Play ownership even when compilation required an exit.
        if original_play is not None:
            try:
                state = client.call("editor_get_state", _retries=0, timeout=5)
                if not state.get("success"):
                    raise VerificationInterrupted("Mode restoration could not be verified.")
                if bool(state.get("isPlaying")) != original_play:
                    skill = "editor_play" if original_play else "editor_stop"
                    preview = client.dry_run_skill(skill, _retries=0, timeout=5)
                    if preview.get("valid") is False or preview.get("error"):
                        raise VerificationInterrupted("Cleanup preview refused: " + str(preview))
                    restored = client.call(skill, _retries=0, timeout=5,
                                           dry_run_token=preview.get("dryRunToken"))
                    if not restored.get("success"):
                        raise VerificationInterrupted(str(restored))
                    if restored.get("jobId"):
                        restored = client.wait_for_job(restored["jobId"], timeout=30)
                        if not restored.get("success"):
                            raise VerificationInterrupted(str(restored))
            except Exception as error:
                report["cleanupError"] = str(error)
                report["status"] = "interrupted"
    return report


# Producer:Betsy
