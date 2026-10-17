"""The real runner with the real pack.py and private.py against a fake editor and a fake download server.

Covers what the runner does with them: reachability in check, a failed download turning the attempt into
pending_verification, retries with a URL, a zip or a folder, and what is left on disk afterwards.
"""
import errno
import json
import os
import shutil
import sys
import tempfile
import time
import unittest
from unittest import mock

try:
    from .runner_support import Bench
    from .test_pack_support import (GOOD, FakeServer, SelfSignedServer, edit_json, make_zip, openssl_available, pack,
                                    private, problems, read_bytes, write_bytes)
except ImportError:
    from runner_support import Bench
    from test_pack_support import (GOOD, FakeServer, SelfSignedServer, edit_json, make_zip, openssl_available, pack,
                                   private, problems, read_bytes, write_bytes)

GOOD_VERIFIER = read_bytes(GOOD / "private" / "F01.py")


class FlowCase(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self, stand_ins=False)
        self.pack_root = os.path.join(self.b.tmp, "fixture-pack")
        shutil.copytree(str(GOOD), self.pack_root)
        edit_json(os.path.join(self.pack_root, "manifest.json"), lambda m: m["private"].update(refs=["r1"]))
        self.server = FakeServer()
        self.addCleanup(self.server.close)
        self.temp_root = os.path.join(self.b.tmp, "temp")
        os.makedirs(self.temp_root)
        for patcher in (mock.patch.object(pack, "PACK_DIR", self.pack_root),
                        mock.patch.object(tempfile, "tempdir", self.temp_root)):
            patcher.start()
            self.addCleanup(patcher.stop)

    def with_server(self):
        """Point the manifest's URL templates at the fake server through the runner's environ (not os.environ).

        Like the real hook this also makes a session unofficial, so sessions that should stay official must not use it.
        """
        self.b.env[private.OVERRIDE_ENV] = json.dumps([self.server.url("/{ref}/{file}")])

    def serve(self, data=GOOD_VERIFIER):
        self.server.route("/r1/F01.py", data)

    def start_and_next(self):
        code, env = self.b.run("start", "--lang", "en")
        self.assertEqual(code, 0, env)
        sid = env["data"]["sessionId"]
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        self.b.answer(env["data"], {"name": "x", "declaredComplete": True})
        return sid, env["data"]

    def submit(self, sid, data, *extra):
        return self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], *extra)

    def leftovers(self):
        return sorted(os.listdir(self.temp_root))

    def private_modules(self):
        return [name for name in sys.modules if name.startswith(private.MODULE_PREFIX)]


class CheckTests(FlowCase):
    def setUp(self):
        FlowCase.setUp(self)
        self.with_server()

    def test_reachability_is_probed_for_the_first_selected_task(self):
        code, env = self.b.run("check")
        self.assertEqual(code, 2)
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_UNREACHABLE"])
        probe = env["data"]["privatePack"]
        self.assertEqual((probe["kind"], probe["probedTask"], probe["reachable"], probe["via"]), ("url", "F01", False,
                                                                                                 None))
        self.assertEqual(probe["attempts"], [{"via": self.server.url("/r1/F01.py"), "error": "http 404"}])
        self.serve()
        code, env = self.b.run("check")
        self.assertEqual(code, 0, env)
        self.assertTrue(env["data"]["ready"])
        self.assertEqual(env["data"]["privatePack"]["via"], self.server.url("/r1/F01.py"))
        self.assertEqual(env["data"]["pack"], {"id": "fixture-pack", "version": "1.0.0", "source": "package",
                                               "official": True, "modified": []})
        self.assertEqual(env["data"]["selection"]["tasks"], ["F01"])
        self.assertEqual(self.leftovers(), [])

    def test_a_zip_and_a_folder_are_probed_without_the_network(self):
        archive = make_zip(os.path.join(self.b.tmp, "private.zip"), {"F01.py": GOOD_VERIFIER})
        code, env = self.b.run("check", "--private-zip", archive)
        self.assertEqual(code, 0, env)
        self.assertEqual((env["data"]["privatePack"]["kind"], env["data"]["pack"]["source"]),
                         ("zip", "zip"))
        code, env = self.b.run("check", "--private-dir", os.path.join(self.pack_root, "private"))
        self.assertEqual(code, 0, env)
        self.assertEqual(env["data"]["privatePack"]["kind"], "private-dir")
        self.assertEqual(self.server.requests, [])

    def test_an_edited_public_file_is_reported_and_unofficial(self):
        self.serve()
        with open(os.path.join(self.pack_root, "tasks", "F01", "prompt.en.md"), "a", encoding="utf-8") as handle:
            handle.write("R3. More.\n")
        code, env = self.b.run("check")
        self.assertEqual(code, 0, env)
        self.assertEqual(env["data"]["pack"]["modified"], ["F01/prompt.en.md"])
        self.assertFalse(env["data"]["pack"]["official"])
        self.assertIn("PACK_MODIFIED", [n["code"] for n in env["data"]["notices"]])

    def test_the_plugin_range_is_enforced(self):
        self.serve()
        edit_json(os.path.join(self.pack_root, "manifest.json"), lambda m: m["plugin"].update(min="99.0.0"))
        code, env = self.b.run("check")
        self.assertEqual(code, 2)
        self.assertEqual([p["code"] for p in env["problems"]], ["PACK_PLUGIN_RANGE"])
        self.assertEqual(env["problems"][0]["args"], ["99.0.0", "latest"])

    def test_a_broken_manifest_is_pack_invalid(self):
        edit_json(os.path.join(self.pack_root, "manifest.json"), lambda m: m.update(packId=""))
        code, env = self.b.run("check")
        self.assertEqual(code, 4)
        self.assertIn("PACK_INVALID", [p["code"] for p in env["problems"]])

    def test_unknown_tasks_in_the_selection(self):
        self.serve()
        code, env = self.b.run("check", "--tasks", "F01,Q9")
        self.assertEqual(code, 3)
        self.assertEqual([p["code"] for p in env["problems"]], ["TASKS_INVALID"])


class PendingTests(FlowCase):
    def setUp(self):
        FlowCase.setUp(self)
        self.with_server()

    def pending_attempt(self):
        self.serve()
        sid, data = self.start_and_next()
        self.server.routes.clear()
        code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 2, env)
        self.assertEqual([p["code"] for p in env["problems"]], ["VERIFICATION_PENDING"])
        self.assertTrue(env["data"]["pending"])
        self.assertEqual(len(env["problems"][0]["args"]), 1)
        self.assertIn("--retry-verify", env["problems"][0]["args"][0])
        return sid, data

    def test_a_failed_download_leaves_the_attempt_waiting_with_the_reasons(self):
        sid, data = self.pending_attempt()
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["phase"], "pending_verification")
        self.assertEqual(state["private"], {"kind": "url", "file": "F01.py", "sha256": None,
                                            "attempts": [{"via": self.server.url("/r1/F01.py"), "error": "http 404"}]})
        self.assertEqual(self.leftovers(), [])
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual([p["code"] for p in env["problems"]], ["VERIFICATION_PENDING"])    # no second fixture

    def test_retrying_with_the_url_scores_the_attempt(self):
        sid, data = self.pending_attempt()
        self.serve()
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual(result["status"], "pass", result["reasons"])
        self.assertEqual(result["reasons"], ["verified_late"])
        self.assertTrue(result["verifiedLate"])
        pinned = pack.open_pack(base_dir=self.pack_root).private_entry("F01")["sha256"]
        self.assertEqual(result["verifier"], {"file": "F01.py", "sha256": pinned, "kind": "url",
                                              "via": self.server.url("/r1/F01.py"), "official": True, "deleted": True})
        self.assertEqual(self.b.state(sid, data["attemptId"])["private"]["sha256"], pinned)
        self.assertEqual((self.leftovers(), self.private_modules()), ([], []))

    def test_retrying_with_a_wrong_download_stays_pending(self):
        sid, data = self.pending_attempt()
        self.serve(b"print('not the verifier')\n")
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual(code, 2)
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["phase"], "pending_verification")
        self.assertEqual(state["private"]["attempts"], [{"via": self.server.url("/r1/F01.py"),
                                                         "error": "hash_mismatch"}])

    def test_retrying_with_a_private_zip(self):
        sid, data = self.pending_attempt()
        archive = make_zip(os.path.join(self.b.tmp, "private.zip"), {"F01.py": GOOD_VERIFIER})
        code, env = self.submit(sid, data, "--retry-verify", "--private-zip", archive)
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["verifier"]["kind"], result["verifier"]["via"],
                          result["verifier"]["official"]), ("pass", "zip", archive, True))
        self.assertEqual(self.leftovers(), [])

    def test_a_zip_with_the_wrong_file_does_not_help(self):
        sid, data = self.pending_attempt()
        archive = make_zip(os.path.join(self.b.tmp, "private.zip"), {"F01.py": b"x = 1\n"})
        code, env = self.submit(sid, data, "--retry-verify", "--private-zip", archive)
        self.assertEqual(code, 2)
        self.assertEqual(self.b.state(sid, data["attemptId"])["private"]["attempts"],
                         [{"via": archive, "error": "hash_mismatch"}])

    def test_retrying_with_a_private_folder(self):
        sid, data = self.pending_attempt()
        folder = os.path.join(self.b.tmp, "verifiers")
        write_bytes(os.path.join(folder, "F01.py"), GOOD_VERIFIER)
        code, env = self.submit(sid, data, "--retry-verify", "--private-dir", folder)
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["verifier"]["kind"], result["verifier"]["official"]), ("private-dir", True))

    def test_a_differing_folder_file_is_scored_but_not_official(self):
        sid, data = self.pending_attempt()
        folder = os.path.join(self.b.tmp, "verifiers")
        write_bytes(os.path.join(folder, "F01.py"), GOOD_VERIFIER + b"# edited\n")
        code, env = self.submit(sid, data, "--retry-verify", "--private-dir", folder)
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["verifier"]["official"]), ("pass", False))
        self.assertNotEqual(result["verifier"]["sha256"], pack.open_pack(base_dir=self.pack_root).private_entry(
            "F01")["sha256"])

    def test_a_machine_that_cannot_hold_the_verifier_leaves_the_attempt_pending(self):
        self.serve()
        sid, data = self.start_and_next()
        with mock.patch.object(tempfile, "mkdtemp", side_effect=OSError(errno.ENOSPC, "No space left on device")):
            code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 2, env)
        self.assertEqual([p["code"] for p in env["problems"]], ["VERIFICATION_PENDING"])
        self.assertTrue(env["data"]["pending"])
        pinned = pack.open_pack(base_dir=self.pack_root).private_entry("F01")["sha256"]
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["phase"], "pending_verification")
        self.assertEqual(state["private"]["sha256"], pinned)                     # the download itself had worked
        self.assertEqual(state["private"]["attempts"], [{"via": self.temp_root, "error": "temp_dir_failed"}])
        self.assertNotIn("hint", state["private"])
        self.assertEqual((self.leftovers(), self.private_modules()), ([], []))
        code, env = self.b.run("next", "--session", sid)                         # still blocked: no second fixture
        self.assertEqual([p["code"] for p in env["problems"]], ["VERIFICATION_PENDING"])
        code, env = self.submit(sid, data, "--retry-verify")                    # the machine recovered
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["reasons"], result["verifiedLate"]), ("pass", ["verified_late"], True))
        self.assertEqual(self.b.state(sid, data["attemptId"])["private"]["attempts"], [])

    def test_a_file_that_cannot_be_written_waits_the_same_way(self):
        self.serve()
        sid, data = self.start_and_next()
        gone = os.path.join(self.b.tmp, "vanished", "usk-eval-abcd1234")        # its parent folder does not exist
        with mock.patch.object(tempfile, "mkdtemp", return_value=gone):
            code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 2, env)
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual((state["phase"], state["private"]["attempts"]),
                         ("pending_verification", [{"via": self.temp_root, "error": "temp_write_failed"}]))
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual(code, 0, env)
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_the_verifier_is_deleted_after_a_normal_submit_too(self):
        self.serve()
        sid, data = self.start_and_next()
        code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["reasons"]), ("pass", []))
        self.assertTrue(result["verifier"]["deleted"])
        self.assertEqual((self.leftovers(), self.private_modules()), ([], []))
        for path in (self.b.paths.state_file(sid, data["attemptId"]), self.b.paths.result_file(sid, data["attemptId"]),
                     self.b.paths.session_log(sid)):
            with open(path, "r", encoding="utf-8") as handle:
                self.assertNotIn("ZZ_PRIVATE_SENTINEL", handle.read())


class TlsProblemTests(FlowCase):
    """A certificate problem gets its own code and tells the user how to fix their Python; nothing falls back to
    unverified TLS. Only when every attempt failed verification: any other mix keeps the ordinary problem codes with
    the advice appended."""

    UNREACHABLE = problems.BLOCKING["PRIVATE_PACK_UNREACHABLE"][2]
    PENDING = problems.BLOCKING["VERIFICATION_PENDING"][2]

    def setUp(self):
        FlowCase.setUp(self)
        self.with_server()

    def tls_everywhere(self):
        return mock.patch.object(private, "_download", return_value=(None, "tls_error"))

    def tls_on_one_host(self):
        """Two mirrors: certificate trouble on 127.0.0.1, an ordinary 404 on localhost."""
        self.b.env[private.OVERRIDE_ENV] = json.dumps([self.server.url("/{ref}/{file}"),
                                                       "http://localhost:%d/{ref}/{file}" % self.server.port])

        def download(url, limit, user_agent, timeout, hosts, loopback_http):
            return (None, "tls_error") if "127.0.0.1" in url else (None, "http 404")
        return mock.patch.object(private, "_download", side_effect=download)

    def pending_tls_attempt(self):
        self.serve()
        sid, data = self.start_and_next()
        with self.tls_everywhere():
            code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 2, env)
        return sid, data, env

    # ---- check and start

    def test_check_reports_the_dedicated_tls_problem(self):
        with self.tls_everywhere():
            code, env = self.b.run("check")
        self.assertEqual(code, 2)
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_TLS"])
        problem = env["problems"][0]
        self.assertEqual(problem["fix"], private.TLS_HINT)                       # exactly the advice, nothing else
        self.assertNotIn("args", problem)
        self.assertIn("TLS certificate verification", problem["message"])
        self.assertIsNone(env["next"])
        self.assertFalse(env["data"]["ready"])
        probe = env["data"]["privatePack"]
        self.assertEqual((probe["reachable"], probe["hint"]), (False, private.TLS_HINT))
        self.assertEqual(probe["attempts"], [{"via": self.server.url("/r1/F01.py"), "error": "tls_error"}])

    def test_start_stops_with_the_same_problem_and_creates_no_session(self):
        with self.tls_everywhere():
            code, env = self.b.run("start", "--lang", "en")
        self.assertEqual(code, 2)
        self.assertEqual([(p["code"], p["fix"]) for p in env["problems"]], [("PRIVATE_PACK_TLS", private.TLS_HINT)])
        self.assertFalse(env["data"]["created"])
        self.assertFalse(os.path.exists(self.b.paths.current))

    def test_check_with_ordinary_failures_keeps_the_unreachable_problem_without_a_hint(self):
        code, env = self.b.run("check")                                          # the server answers 404
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_UNREACHABLE"])
        self.assertEqual(env["problems"][0]["fix"], self.UNREACHABLE)
        self.assertNotIn("hint", env["data"]["privatePack"])

    def test_one_tls_failure_among_other_failures_appends_the_hint_to_the_unreachable_problem(self):
        with self.tls_on_one_host():
            code, env = self.b.run("check")
        self.assertEqual(code, 2)
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_UNREACHABLE"])
        self.assertEqual(env["problems"][0]["fix"], self.UNREACHABLE + " " + private.TLS_HINT)
        probe = env["data"]["privatePack"]
        self.assertEqual([a["error"] for a in probe["attempts"]], ["tls_error", "http 404"])
        self.assertEqual(probe["hint"], private.TLS_HINT)

    def test_a_certificate_failure_on_every_mirror_is_still_the_tls_problem(self):
        """Two hosts, both with trouble: the second is its own attempt, the later refs on dead hosts are not."""
        self.b.env[private.OVERRIDE_ENV] = json.dumps([self.server.url("/{ref}/{file}"),
                                                       "http://localhost:%d/{ref}/{file}" % self.server.port])
        edit_json(os.path.join(self.pack_root, "manifest.json"), lambda m: m["private"].update(refs=["r1", "r2", "r3"]))
        with self.tls_everywhere():
            code, env = self.b.run("check")
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_TLS"])
        self.assertEqual([a["error"] for a in env["data"]["privatePack"]["attempts"]], ["tls_error", "tls_error"])

    # ---- a pending attempt

    def test_a_pending_attempt_names_the_tls_problem_and_the_retry_command(self):
        sid, data, env = self.pending_tls_attempt()
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_TLS"])
        problem = env["problems"][0]
        self.assertEqual(len(problem["args"]), 1)
        retry = problem["args"][0]
        self.assertIn("--retry-verify", retry)
        self.assertIn(data["attemptId"], retry)
        self.assertEqual(problem["fix"], private.TLS_HINT + " Then run: " + retry)
        self.assertEqual(env["next"]["line"], retry)                             # the next step is the retry command
        self.assertTrue(env["data"]["pending"])
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["phase"], "pending_verification")
        self.assertEqual(state["private"]["hint"], private.TLS_HINT)
        self.assertEqual(state["private"]["attempts"], [{"via": self.server.url("/r1/F01.py"), "error": "tls_error"}])

    def test_next_keeps_reporting_it_until_the_attempt_is_scored(self):
        sid, data, env = self.pending_tls_attempt()
        retry = env["problems"][0]["args"][0]
        code, env = self.b.run("next", "--session", sid)                         # blocked: no second fixture
        self.assertEqual(code, 2)
        self.assertEqual([p["code"] for p in env["problems"]], ["PRIVATE_PACK_TLS"])
        self.assertEqual(env["problems"][0]["fix"], private.TLS_HINT + " Then run: " + retry)
        self.assertEqual(env["next"]["line"], retry)
        code, env = self.submit(sid, data, "--retry-verify")                    # certificates fixed (or a zip given)
        self.assertEqual(code, 0, env)
        self.assertNotIn("hint", self.b.state(sid, data["attemptId"])["private"])
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_a_pending_attempt_with_one_tls_failure_among_others_keeps_verification_pending(self):
        self.serve()
        sid, data = self.start_and_next()
        with self.tls_on_one_host():
            code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 2, env)
        self.assertEqual([p["code"] for p in env["problems"]], ["VERIFICATION_PENDING"])
        problem = env["problems"][0]
        retry = problem["args"][0]
        self.assertEqual(problem["fix"], self.PENDING.format(retry) + " " + private.TLS_HINT)
        self.assertEqual(env["next"]["line"], retry)
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual([a["error"] for a in state["private"]["attempts"]], ["tls_error", "http 404"])
        self.assertEqual(state["private"]["hint"], private.TLS_HINT)

    def test_a_pending_attempt_without_a_tls_cause_has_no_hint(self):
        self.serve()
        sid, data = self.start_and_next()
        self.server.routes.clear()
        code, env = self.submit(sid, data, "--answer", data["answerFile"])
        problem = env["problems"][0]
        self.assertEqual(problem["code"], "VERIFICATION_PENDING")
        self.assertEqual(problem["fix"], self.PENDING.format(problem["args"][0]))
        self.assertNotIn("hint", self.b.state(sid, data["attemptId"])["private"])

    def test_a_certificate_failure_on_one_mirror_does_not_stop_a_good_one(self):
        """Only a verifier nobody could deliver is a problem; a mirror with trouble is a line in the attempts."""
        self.b.env[private.OVERRIDE_ENV] = json.dumps([self.server.url("/{ref}/{file}"),
                                                       "http://localhost:%d/{ref}/{file}" % self.server.port])
        self.server.route("/r1/F01.py", GOOD_VERIFIER)
        real = private._download

        def download(url, limit, user_agent, timeout, hosts, loopback_http):
            if "127.0.0.1" in url:
                return None, "tls_error"
            return real(url, limit, user_agent, timeout, hosts, loopback_http)
        with mock.patch.object(private, "_download", side_effect=download):
            sid, data = self.start_and_next()
            code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["verifier"]["kind"]), ("pass", "url"))
        self.assertTrue(result["verifier"]["via"].startswith("http://localhost:"))
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["private"]["attempts"], [{"via": self.server.url("/r1/F01.py"), "error": "tls_error"}])
        self.assertNotIn("hint", state["private"])

    # ---- a real handshake

    @unittest.skipUnless(openssl_available(), "needs the openssl command line tool for a throw-away certificate")
    def test_a_real_untrusted_certificate_end_to_end(self):
        self.serve()
        tls = SelfSignedServer(self.b.tmp)
        self.addCleanup(tls.close)
        good = self.b.env[private.OVERRIDE_ENV]
        self.b.env[private.OVERRIDE_ENV] = json.dumps([tls.template()])
        code, env = self.b.run("check")                                          # nothing patched: a real handshake
        self.assertEqual([(p["code"], p["fix"]) for p in env["problems"]], [("PRIVATE_PACK_TLS", private.TLS_HINT)])
        self.assertEqual([a["error"] for a in env["data"]["privatePack"]["attempts"]], ["tls_error"])
        self.b.env[private.OVERRIDE_ENV] = good
        sid, data = self.start_and_next()
        self.b.env[private.OVERRIDE_ENV] = json.dumps([tls.template()])
        code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 2, env)
        problem = env["problems"][0]
        self.assertEqual(problem["code"], "PRIVATE_PACK_TLS")
        self.assertEqual(problem["fix"], private.TLS_HINT + " Then run: " + problem["args"][0])
        self.assertEqual(self.b.state(sid, data["attemptId"])["private"]["attempts"][0]["error"], "tls_error")
        self.b.env[private.OVERRIDE_ENV] = good                                  # the user fixed their certificates
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual(code, 0, env)
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")
        self.assertEqual((self.leftovers(), self.private_modules()), ([], []))


class VerifierProblemTests(FlowCase):
    """A broken verifier is the pack author's problem (invalid), not a reason to wait and retry."""

    def test_a_verifier_that_fails_to_import_makes_the_attempt_invalid(self):
        folder = os.path.join(self.b.tmp, "verifiers")
        write_bytes(os.path.join(folder, "F01.py"), b"def broken(:\n")
        code, env = self.b.run("start", "--lang", "en", "--private-dir", folder)
        self.assertEqual(code, 0, env)
        sid = env["data"]["sessionId"]
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        data = env["data"]
        self.b.answer(data, {"name": "x", "declaredComplete": True})
        code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 0, env)
        self.assertFalse(env["data"]["pending"])
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual(result["status"], "invalid")
        self.assertEqual(result["reasons"][0],
                         "verifier_error: VerifierImportError: verifier F01.py failed to load: SyntaxError (line 1)")
        self.assertTrue(result["verifier"]["deleted"])
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "torn_down")
        self.assertEqual((self.leftovers(), self.private_modules()), ([], []))


class SessionSourceTests(FlowCase):
    """A session remembers where its verifiers come from; next and submit reopen the pack the same way."""

    def run_session(self, *start_args):
        code, env = self.b.run("start", "--lang", "en", *start_args)
        self.assertEqual(code, 0, env)
        sid = env["data"]["sessionId"]
        official = env["data"]["official"]
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        data = env["data"]
        self.b.answer(data, {"name": "x", "declaredComplete": True})
        code, env = self.submit(sid, data, "--answer", data["answerFile"])
        self.assertEqual(code, 0, env)
        return official, self.b.session(sid), self.b.result(sid, data["attemptId"])

    def test_a_session_with_a_private_zip_never_touches_the_network(self):
        archive = make_zip(os.path.join(self.b.tmp, "private.zip"), {"F01.py": GOOD_VERIFIER})
        official, session, result = self.run_session("--private-zip", archive)
        self.assertTrue(official)
        self.assertEqual(session["pack"]["source"], "zip")
        self.assertEqual(session["pack"]["private"], {"kind": "zip", "path": os.path.abspath(archive)})
        self.assertEqual((result["status"], result["verifier"]["kind"], result["verifier"]["official"]),
                         ("pass", "zip", True))
        self.assertEqual(self.server.requests, [])
        self.assertEqual(self.leftovers(), [])

    def test_a_session_with_a_private_folder(self):
        folder = os.path.join(self.pack_root, "private")
        official, session, result = self.run_session("--private-dir", folder)
        self.assertTrue(official)
        self.assertEqual((session["pack"]["source"], session["pack"]["private"]["kind"]), ("package", "private-dir"))
        self.assertEqual((result["status"], result["verifier"]["kind"]), ("pass", "private-dir"))
        self.assertEqual(self.server.requests, [])

    def test_a_differing_private_folder_makes_the_whole_session_unofficial(self):
        folder = os.path.join(self.b.tmp, "verifiers")
        write_bytes(os.path.join(folder, "F01.py"), GOOD_VERIFIER + b"# edited\n")
        official, session, result = self.run_session("--private-dir", folder)
        self.assertFalse(official)
        self.assertEqual(session["pack"]["modified"], ["private/F01.py"])
        self.assertFalse(session["pack"]["official"])
        self.assertEqual((result["status"], result["verifier"]["official"]), ("pass", False))

    def test_a_pack_dir_session_is_a_dev_session(self):
        official, session, result = self.run_session("--pack-dir", self.pack_root)
        self.assertFalse(official)
        self.assertEqual((session["pack"]["source"], session["pack"]["private"]["kind"]), ("pack-dir", "pack-dir"))
        self.assertEqual((result["verifier"]["kind"], result["verifier"]["official"]), ("pack-dir", False))

    def test_a_url_session_records_no_path_and_the_override_makes_it_unofficial(self):
        self.with_server()
        self.serve()
        official, session, result = self.run_session()
        self.assertFalse(official)                                    # USK_EVAL_PRIVATE_URLS is set in the environ
        self.assertEqual(session["pack"]["private"], {"kind": "url", "path": None})
        self.assertEqual(session["pack"]["manifestSha256"], pack.open_pack(base_dir=self.pack_root).manifest_sha256)

    def test_a_changed_manifest_stops_the_session(self):
        archive = make_zip(os.path.join(self.b.tmp, "private.zip"), {"F01.py": GOOD_VERIFIER})
        code, env = self.b.run("start", "--lang", "en", "--private-zip", archive)
        sid = env["data"]["sessionId"]
        edit_json(os.path.join(self.pack_root, "manifest.json"), lambda m: m["tasks"]["F01"].update(minutes=6))
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 4)
        self.assertEqual([p["code"] for p in env["problems"]], ["PACK_INVALID"])


class StartupCleanupTests(FlowCase):
    def setUp(self):
        FlowCase.setUp(self)
        self.with_server()

    def test_every_runner_command_removes_stale_verifier_folders(self):
        stale = os.path.join(self.temp_root, "usk-eval-stale000")
        fresh = os.path.join(self.temp_root, "usk-eval-fresh000")
        for folder in (stale, fresh):
            write_bytes(os.path.join(folder, "0123456789ab.py"), b"x")
        old = time.time() - 7200
        os.utime(stale, (old, old))
        self.b.run("check")
        self.assertFalse(os.path.exists(stale))
        self.assertTrue(os.path.isdir(fresh))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
