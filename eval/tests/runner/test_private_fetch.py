"""private.fetch over HTTP: order, pinning, limits, redirects, proxies, probes (contract 5.9).

A real HTTP server on 127.0.0.1:0 plays raw.githubusercontent.com and jsDelivr; the USK_EVAL_PRIVATE_URLS hook points the
manifest's templates at it.
"""
import http.client
import json
import os
import socket
import ssl
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
from unittest import mock

try:
    from .test_pack_support import (GOOD, FakeServer, PrivateCase, SelfSignedServer, edit_json, free_port,
                                    openssl_available, pack, private, problems, read_bytes, store)
except ImportError:
    from test_pack_support import (GOOD, FakeServer, PrivateCase, SelfSignedServer, edit_json, free_port,
                                   openssl_available, pack, private, problems, read_bytes, store)

import usk_eval  # noqa: E402

UA = "unity-skills-eval/9.9.9"


class UrlCase(PrivateCase):
    def setUp(self):
        PrivateCase.setUp(self)
        self.pack_root = self.copy_pack()
        edit_json(os.path.join(self.pack_root, "manifest.json"), lambda m: m["private"].update(refs=["r1", "r2", "r3"]))
        self.pack = pack.open_pack(base_dir=self.pack_root)
        self.server = FakeServer()
        self.addCleanup(self.server.close)
        self.good = read_bytes(GOOD / "private" / "F01.py")
        self.entry = self.pack.private_entry("F01")
        self.env = {private.OVERRIDE_ENV: json.dumps([self.server.url("/{ref}/{file}")])}

    def url(self, ref, name="F01.py"):
        return self.server.url("/%s/%s" % (ref, name))

    def fetch(self, task="F01", **kwargs):
        kwargs.setdefault("environ", self.env)
        kwargs.setdefault("timeout", 5)
        kwargs.setdefault("user_agent", UA)
        return private.fetch(self.pack, task, **kwargs)

    def failure(self, **kwargs):
        with self.assertRaises(private.PrivateUnavailable) as exc:
            self.fetch(**kwargs)
        return exc.exception

    def failure_of(self, opened, **kwargs):
        """The PrivateUnavailable of fetching F01 from another pack, with the manifest's own URL list (no override)."""
        kwargs.setdefault("environ", {})
        kwargs.setdefault("plugin_version", "3.0.0")
        kwargs.setdefault("timeout", 5)
        with self.assertRaises(private.PrivateUnavailable) as exc:
            private.fetch(opened, "F01", **kwargs)
        return exc.exception


class FetchOrderTests(UrlCase):
    def test_first_404_second_wrong_hash_third_good(self):
        self.server.route("/r2/F01.py", b"print('something else')\n")
        self.server.route("/r3/F01.py", self.good)
        fetched = self.fetch()
        self.assertEqual((fetched.kind, fetched.file, fetched.via, fetched.official), ("url", "F01.py", self.url("r3"),
                                                                                       True))
        self.assertEqual(fetched.sha256, self.entry["sha256"])
        self.assertEqual(fetched.data, self.good)
        self.assertEqual(fetched.attempts, [{"via": self.url("r1"), "error": "http 404"},
                                            {"via": self.url("r2"), "error": "hash_mismatch"}])
        self.assertEqual(self.server.paths(), ["/r1/F01.py", "/r2/F01.py", "/r3/F01.py"])
        self.assertFalse(fetched.deleted)

    def test_the_first_match_ends_the_search(self):
        self.server.route("/r1/F01.py", self.good)
        self.server.route("/r2/F01.py", self.good)
        fetched = self.fetch()
        self.assertEqual((fetched.via, fetched.attempts), (self.url("r1"), []))
        self.assertEqual(self.server.paths(), ["/r1/F01.py"])

    def test_every_source_failing_is_pending_verification(self):
        self.server.route("/r2/F01.py", b"x", status=500)
        self.server.route("/r3/F01.py", b"wrong")
        exc = self.failure()
        self.assertEqual(exc.code, "VERIFICATION_PENDING")
        self.assertIn(exc.code, problems.BLOCKING)
        self.assertEqual(exc.attempts, [{"via": self.url("r1"), "error": "http 404"},
                                        {"via": self.url("r2"), "error": "http 500"},
                                        {"via": self.url("r3"), "error": "hash_mismatch"}])
        self.assertEqual(str(exc), "no verifier source matched: http 404, http 500, hash_mismatch")

    def test_attempts_are_copies(self):
        original = [{"via": "u", "error": "e"}]
        private.PrivateUnavailable(original).attempts[0]["error"] = "changed"
        self.assertEqual(original[0]["error"], "e")

    def test_each_task_uses_its_own_file_name(self):
        self.server.route("/r1/F02.py", read_bytes(GOOD / "private" / "F02.py"))
        fetched = self.fetch("F02")
        self.assertEqual((fetched.file, fetched.via), ("F02.py", self.url("r1", "F02.py")))

    def test_unknown_tasks_are_not_fetched(self):
        with self.assertRaises(problems.ProblemError) as exc:
            self.fetch("F09")
        self.assertEqual(exc.exception.problems[0]["code"], "TASKS_INVALID")
        self.assertEqual(self.server.requests, [])

    def test_candidates_try_every_template_of_a_ref_before_the_next_ref(self):
        opened = pack.open_pack(pack_dir=str(GOOD))
        raw = "https://raw.githubusercontent.com/Besty0728/Unity-Skills/%s/eval/pack/private/F01.py"
        mirror = "https://cdn.jsdelivr.net/gh/Besty0728/Unity-Skills@%s/eval/pack/private/F01.py"
        self.assertEqual(private.url_candidates(opened, "F01", plugin_version="3.0.0", environ={}),
                         [("v3.0.0", raw % "v3.0.0"), ("v3.0.0", mirror % "v3.0.0"), ("main", raw % "main"),
                          ("main", mirror % "main"), ("beta", raw % "beta"), ("beta", mirror % "beta")])

    def test_a_ref_that_needs_the_plugin_version_is_skipped_without_one(self):
        opened = pack.open_pack(pack_dir=str(GOOD))
        refs = [ref for ref, _url in private.url_candidates(opened, "F01", plugin_version="not-a-version", environ={})]
        self.assertEqual(refs, ["main", "main", "beta", "beta"])

    def test_the_package_version_stands_in_for_a_missing_plugin_version(self):
        opened = pack.open_pack(pack_dir=str(GOOD))
        first = private.url_candidates(opened, "F01", plugin_version=None, environ={})[0][0]
        self.assertEqual(first, "v" + usk_eval.package_version())

    def test_duplicate_urls_are_fetched_once(self):
        env = {private.OVERRIDE_ENV: json.dumps([self.server.url("/same/{file}")])}
        exc = self.failure(environ=env)
        self.assertEqual([item["via"] for item in exc.attempts], [self.server.url("/same/F01.py")])
        self.assertEqual(self.server.paths(), ["/same/F01.py"])

    def test_templates_are_tried_in_order_for_one_ref(self):
        env = {private.OVERRIDE_ENV: json.dumps([self.server.url("/a/{ref}/{file}"), self.server.url("/b/{ref}/{file}")])}
        self.server.route("/b/r1/F01.py", self.good)
        fetched = self.fetch(environ=env)
        self.assertEqual(self.server.paths(), ["/a/r1/F01.py", "/b/r1/F01.py"])
        self.assertEqual(fetched.via, self.server.url("/b/r1/F01.py"))


class OverrideTests(UrlCase):
    def test_the_override_must_be_a_json_array_of_usable_templates(self):
        for raw in ("not json", "{}", "[]", '["http://example.com/{file}"]', '["https://x.test/{nope}/{file}"]', "[5]",
                    '["https://x.test/{ref}"]', '["http://127.0.0.1:99999/{file}"]'):
            with self.subTest(raw):
                exc = self.failure(environ={private.OVERRIDE_ENV: raw})
                self.assertEqual(exc.attempts, [{"via": None, "error": "bad_url_override"}])
        self.assertEqual(self.server.requests, [])

    def test_the_override_is_read_from_os_environ_by_default(self):
        self.server.route("/r1/F01.py", self.good)
        with mock.patch.dict(os.environ, self.env):
            fetched = private.fetch(self.pack, "F01", timeout=5)
        self.assertEqual(fetched.via, self.url("r1"))

    def test_loopback_http_is_only_allowed_through_the_override(self):
        self.pack.manifest["private"]["urlTemplates"] = [self.server.url("/{ref}/{file}")]
        exc = self.failure(environ={})
        self.assertEqual(exc.attempts, [{"via": None, "error": "bad_url_template"}])
        self.assertEqual(self.server.requests, [])

    def test_no_candidates(self):
        self.pack.manifest["private"]["refs"] = ["{plugin}"]
        exc = self.failure(plugin_version="not-a-version")
        self.assertEqual(exc.attempts, [{"via": None, "error": "no_url_candidates"}])

    def test_a_pack_without_url_fields_has_nothing_to_try(self):
        self.pack.manifest.pop("private")
        exc = self.failure(environ={})
        self.assertEqual(exc.attempts, [{"via": None, "error": "no_url_candidates"}])


class LimitAndFailureTests(UrlCase):
    def test_oversized_responses_are_cut_off(self):
        big = b"x" * (self.entry["bytes"] + private.SLACK_BYTES + 10)
        self.server.route("/r1/F01.py", big)                                  # Content-Length announces too much
        self.server.route("/r2/F01.py", big, length=False)                    # no Content-Length: stopped while streaming
        self.server.route("/r3/F01.py", self.good)
        fetched = self.fetch()
        self.assertEqual([item["error"] for item in fetched.attempts], ["too_large", "too_large"])
        self.assertEqual(fetched.via, self.url("r3"))

    def test_a_response_of_exactly_the_limit_is_read_but_must_still_match(self):
        body = b"y" * (self.entry["bytes"] + private.SLACK_BYTES)
        self.server.route("/r1/F01.py", body)
        self.server.route("/r2/F01.py", self.good)
        fetched = self.fetch()
        self.assertEqual(fetched.attempts, [{"via": self.url("r1"), "error": "hash_mismatch"}])

    def test_a_nonsense_content_length_is_ignored(self):
        self.server.route("/r1/F01.py", self.good, length=False, headers={"Content-Length": "abc"})
        fetched = self.fetch()
        self.assertEqual((fetched.via, fetched.attempts), (self.url("r1"), []))

    def test_http_errors_carry_their_status(self):
        for status in (403, 404, 429, 500, 503):
            self.server.route("/r1/F01.py", b"no", status=status)
            self.server.route("/r2/F01.py", self.good)
            self.assertEqual(self.fetch().attempts, [{"via": self.url("r1"), "error": "http %d" % status}])

    def test_a_timeout_ends_the_requests_to_that_host(self):
        self.server.route("/r1/F01.py", self.good, delay=1.5)
        exc = self.failure(timeout=0.3)
        self.assertEqual(exc.attempts, [{"via": self.url("r1"), "error": "timeout"}])    # skipped URLs are no attempts
        self.assertTrue(self.server.wait_for(1))
        self.assertEqual(self.server.paths(), ["/r1/F01.py"])           # the other two were never requested

    def test_a_refused_connection_ends_the_requests_to_that_host(self):
        base = "http://127.0.0.1:%d" % free_port()
        env = {private.OVERRIDE_ENV: json.dumps([base + "/{ref}/{file}"])}
        exc = self.failure(environ=env)
        self.assertEqual(exc.attempts, [{"via": base + "/r1/F01.py", "error": "connection_refused"}])

    def test_other_hosts_are_still_tried_after_a_host_dies(self):
        dead = "http://127.0.0.1:%d/{ref}/{file}" % free_port()
        live = "http://localhost:%d/{ref}/{file}" % self.server.port                 # a second host name
        self.server.route("/r2/F01.py", self.good)
        env = {private.OVERRIDE_ENV: json.dumps([dead, live])}
        fetched = self.fetch(environ=env)
        self.assertEqual([item["error"] for item in fetched.attempts], ["connection_refused", "http 404"])
        self.assertEqual(fetched.via, "http://localhost:%d/r2/F01.py" % self.server.port)
        self.assertEqual(self.server.paths(), ["/r1/F01.py", "/r2/F01.py"])        # the dead host was asked only once

    def test_a_dropped_connection_does_not_mark_the_host_dead(self):
        self.server.route("/r1/F01.py", drop=True)
        self.server.route("/r2/F01.py", self.good)
        fetched = self.fetch()
        self.assertEqual(fetched.attempts, [{"via": self.url("r1"), "error": "network_error"}])
        self.assertEqual((fetched.via, self.server.paths()), (self.url("r2"), ["/r1/F01.py", "/r2/F01.py"]))

    def test_every_url_is_still_tried_after_resets(self):
        for ref in ("r1", "r2", "r3"):
            self.server.route("/%s/F01.py" % ref, drop=True)
        exc = self.failure()
        self.assertEqual([item["error"] for item in exc.attempts], ["network_error"] * 3)
        self.assertEqual(self.server.paths(), ["/r1/F01.py", "/r2/F01.py", "/r3/F01.py"])

    def test_an_http_error_does_not_mark_the_host_dead(self):
        self.server.route("/r3/F01.py", self.good)
        self.assertEqual(len(self.fetch().attempts), 2)

    def test_a_slow_trickle_hits_the_overall_deadline(self):
        # every read returns within the per-read timeout, but the whole download outlasts 4 x timeout
        self.server.route("/r1/F01.py", self.good, length=False, trickle=(16, 0.12))
        started = time.monotonic()
        exc = self.failure(timeout=0.15)
        self.assertEqual(exc.attempts[0], {"via": self.url("r1"), "error": "timeout"})
        self.assertLess(time.monotonic() - started, 5)


class RedirectTests(UrlCase):
    def test_a_redirect_inside_the_template_hosts_is_followed(self):
        self.server.route("/r1/F01.py", status=302, headers={"Location": "/final/F01.py"})
        self.server.route("/final/F01.py", self.good)
        fetched = self.fetch()
        self.assertEqual((fetched.via, fetched.attempts), (self.url("r1"), []))
        self.assertEqual(self.server.paths(), ["/r1/F01.py", "/final/F01.py"])

    def test_a_redirect_to_another_host_is_not_followed(self):
        for target in ("http://localhost:%d/stolen" % self.server.port, "https://evil.example.test/x",
                       "ftp://127.0.0.1/x", "http://127.0.0.2:%d/x" % self.server.port):
            with self.subTest(target):
                self.server.requests[:] = []
                self.server.route("/r1/F01.py", status=302, headers={"Location": target})
                self.server.route("/stolen", self.good)
                self.server.route("/r2/F01.py", self.good)
                fetched = self.fetch()
                self.assertEqual(fetched.attempts, [{"via": self.url("r1"), "error": "redirect_blocked"}])
                self.assertEqual(fetched.via, self.url("r2"))
                self.assertNotIn("/stolen", self.server.paths())

    def test_a_redirect_loop_is_an_http_error(self):
        self.server.route("/r1/F01.py", status=302, headers={"Location": "/r1/F01.py"})
        self.server.route("/r2/F01.py", self.good)
        self.assertEqual(self.fetch().attempts, [{"via": self.url("r1"), "error": "http 302"}])

    def test_the_redirect_handler_keeps_an_https_only_allow_list(self):
        handler = private._Redirects({"cdn.example.test"}, False)
        request = urllib.request.Request("https://cdn.example.test/a")
        for target, allowed in (("https://cdn.example.test/b", True), ("http://cdn.example.test/b", False),
                                ("https://other.example.test/b", False), ("http://127.0.0.1/b", False)):
            with self.subTest(target):
                if allowed:
                    self.assertIsNotNone(handler.redirect_request(request, None, 302, "Found", {}, target))
                else:
                    with self.assertRaises(private._RedirectBlocked):
                        handler.redirect_request(request, None, 302, "Found", {}, target)


class RequestTests(UrlCase):
    def test_request_headers(self):
        self.server.route("/r1/F01.py", self.good)
        self.fetch()
        headers = dict((key.lower(), value) for key, value in self.server.requests[0]["headers"].items())
        self.assertEqual(headers["user-agent"], UA)
        self.assertEqual(headers["accept-encoding"], "identity")
        for name in ("authorization", "cookie", "x-agent-id", "x-expect-instance"):
            self.assertNotIn(name, headers)

    def test_the_default_user_agent_names_the_runner_version(self):
        self.server.route("/r1/F01.py", self.good)
        self.fetch(user_agent=None)
        headers = dict((key.lower(), value) for key, value in self.server.requests[0]["headers"].items())
        self.assertEqual(headers["user-agent"], "unity-skills-eval/" + usk_eval.package_version())

    def test_fetching_writes_nothing_to_disk(self):
        self.server.route("/r1/F01.py", self.good)
        self.fetch()
        self.assertEqual(self.leftovers(), [])

    def test_proxies_follow_the_environment_but_never_for_loopback(self):
        env = {"HTTPS_PROXY": "http://127.0.0.1:3128", "https_proxy": "http://127.0.0.1:3128", "NO_PROXY": "",
               "no_proxy": ""}
        with mock.patch.dict(os.environ, env):
            remote = private._opener("https://cdn.example.test/x", {"cdn.example.test"}, False)
            local = private._opener("http://127.0.0.1:1/x", {"127.0.0.1"}, True)
            self.assertEqual(self.proxies_of(remote).get("https"), "http://127.0.0.1:3128")
            self.assertEqual(self.proxies_of(local), {})

    @staticmethod
    def proxies_of(opener):
        """The proxies an opener will use; an empty ProxyHandler is not even registered by urllib."""
        found = [handler for handler in opener.handlers if isinstance(handler, urllib.request.ProxyHandler)]
        assert len(found) <= 1
        return found[0].proxies if found else {}


class HintTests(UrlCase):
    """A TLS failure tells the user how to fix their Python instead of falling back to unverified TLS."""

    def tls_everywhere(self):
        return mock.patch.object(private, "_download", return_value=(None, "tls_error"))

    def test_a_tls_failure_carries_the_certificate_hint(self):
        with self.tls_everywhere():
            exc = self.failure()
        self.assertEqual(exc.attempts, [{"via": self.url("r1"), "error": "tls_error"}])    # one host, one attempt
        self.assertEqual(exc.hint, private.TLS_HINT)
        for needle in ("Install Certificates.command", "python.org", "--private-zip", "SSL_CERT_FILE"):
            self.assertIn(needle, private.TLS_HINT)
        self.assertTrue(all(ord(char) < 128 for char in private.TLS_HINT))
        self.assertLessEqual(len(private.TLS_HINT), 400)           # attempt._wait_for_private stores hints up to 400

    def test_the_hint_never_suggests_turning_verification_off(self):
        text = private.TLS_HINT.lower()
        for word in ("unverified", "insecure", "disable", "cert_none", "verify=false", "--insecure"):
            self.assertNotIn(word, text)

    def test_the_production_layout_records_one_tls_attempt_per_host(self):
        """3 refs x 2 mirrors: a certificate failure must leave nothing but tls_error attempts, because the runner
        reports PRIVATE_PACK_TLS only when every attempt is one."""
        opened = pack.open_pack(base_dir=str(GOOD))
        self.assertEqual(opened.private_kind, "url")
        with self.tls_everywhere():
            exc = self.failure_of(opened)
        self.assertEqual([item["error"] for item in exc.attempts], ["tls_error", "tls_error"])
        self.assertEqual([urllib.parse.urlsplit(item["via"]).hostname for item in exc.attempts],
                         ["raw.githubusercontent.com", "cdn.jsdelivr.net"])
        self.assertTrue(all("/v3.0.0/" in item["via"] or "@v3.0.0/" in item["via"] for item in exc.attempts))
        self.assertEqual(exc.hint, private.TLS_HINT)

    def test_a_tls_failure_on_one_host_leaves_the_other_host_in_play(self):
        calls = []

        def download(url, limit, user_agent, timeout, hosts, loopback_http):
            calls.append(url)
            return (None, "tls_error") if "127.0.0.1" in url else (None, "http 404")
        templates = [self.server.url("/{ref}/{file}"), "http://localhost:%d/{ref}/{file}" % self.server.port]
        env = {private.OVERRIDE_ENV: json.dumps(templates)}
        with mock.patch.object(private, "_download", side_effect=download):
            exc = self.failure(environ=env)
        self.assertEqual([item["error"] for item in exc.attempts], ["tls_error", "http 404", "http 404", "http 404"])
        self.assertEqual(len([url for url in calls if "127.0.0.1" in url]), 1)         # the failing host asked once
        self.assertEqual(len([url for url in calls if "localhost" in url]), 3)         # the other one for every ref
        self.assertEqual(exc.hint, private.TLS_HINT)          # a TLS failure among the attempts is enough for the hint

    def test_other_failures_have_no_hint(self):
        exc = self.failure()                                          # three 404s
        self.assertIsNone(exc.hint)
        for error in ("timeout", "dns_failed", "connection_refused", "network_error", "hash_mismatch", "http 500"):
            self.assertIsNone(private.fix_hint([{"via": "u", "error": error}]), error)
        self.assertIsNone(private.fix_hint([]))
        self.assertIsNone(private.fix_hint(None))

    def test_one_tls_failure_among_others_is_enough(self):
        attempts = [{"via": "a", "error": "http 404"}, {"via": "b", "error": "tls_error"}]
        self.assertEqual(private.fix_hint(attempts), private.TLS_HINT)

    def test_probe_adds_the_hint_only_when_there_is_one(self):
        with self.tls_everywhere():
            result = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
        self.assertFalse(result["reachable"])
        self.assertEqual(result["hint"], private.TLS_HINT)
        plain = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
        self.assertNotIn("hint", plain)
        self.server.route("/r1/F01.py", self.good)
        self.assertNotIn("hint", private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5))
        json.dumps(result)

    def test_the_hint_reaches_the_pending_exception_of_a_retry(self):
        with self.tls_everywhere():
            exc = self.failure()
        self.assertIsInstance(exc, private.PrivateUnavailable)
        self.assertEqual(exc.code, "VERIFICATION_PENDING")
        self.assertEqual(exc.hint, private.TLS_HINT)


@unittest.skipUnless(openssl_available(), "needs the openssl command line tool for a throw-away certificate")
class RealTlsTests(UrlCase):
    """A real handshake with a certificate nobody trusts, no patching: urllib, ssl and the classifier together."""

    def setUp(self):
        UrlCase.setUp(self)
        self.tls = SelfSignedServer(self.root)
        self.addCleanup(self.tls.close)
        self.env = {private.OVERRIDE_ENV: json.dumps([self.tls.template()])}

    def test_an_untrusted_certificate_is_a_tls_error_with_the_hint(self):
        exc = self.failure()
        self.assertEqual([item["error"] for item in exc.attempts], ["tls_error"])
        self.assertEqual(exc.hint, private.TLS_HINT)
        self.assertIn("/r1/F01.py", exc.attempts[0]["via"])

    def test_probe_reports_it_as_unreachable_with_the_hint(self):
        result = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
        self.assertEqual((result["reachable"], result["hint"]), (False, private.TLS_HINT))
        self.assertEqual([item["error"] for item in result["attempts"]], ["tls_error"])

    def test_the_failure_is_the_verification_not_a_broken_server(self):
        """The same server answers a client that skips verification, so the refusal above really is the certificate."""
        self.failure()
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        with urllib.request.urlopen(self.tls.template().replace("{ref}", "x").replace("{file}", "y"), timeout=5,
                                    context=context) as response:                 # the test's own, unverified client
            self.assertEqual(response.read(), b"ok")


class EnvironTests(UrlCase):
    """fetch/probe/url_candidates take the runner's environ; os.environ is only the default."""

    def test_the_given_environ_beats_os_environ(self):
        dead = {private.OVERRIDE_ENV: json.dumps(["http://127.0.0.1:%d/{ref}/{file}" % free_port()])}
        self.server.route("/r1/F01.py", self.good)
        with mock.patch.dict(os.environ, dead):
            fetched = self.fetch()                                    # self.env points at the server
            self.assertEqual(fetched.via, self.url("r1"))
            probed = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
            self.assertTrue(probed["reachable"])

    def test_an_empty_environ_ignores_the_override_in_os_environ(self):
        with mock.patch.dict(os.environ, self.env):
            plain = private.url_candidates(self.pack, "F01", plugin_version="3.0.0", environ={})
            hooked = private.url_candidates(self.pack, "F01", plugin_version="3.0.0")
        self.assertTrue(all(url.startswith("https://") for _ref, url in plain))
        self.assertTrue(all(url.startswith(self.server.url("/")) for _ref, url in hooked))

    def test_os_environ_is_the_default(self):
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(private.url_candidates(self.pack, "F01")[0][1], self.url("r1"))
        with mock.patch.dict(os.environ, {private.OVERRIDE_ENV: ""}):
            self.assertTrue(private.url_candidates(self.pack, "F01")[0][1].startswith("https://"))

    def test_a_mapping_that_is_not_a_dict_works(self):
        class Env(object):
            def get(self, key, default=None):
                return self.env.get(key, default)
        view = Env()
        view.env = self.env
        self.assertEqual(private.url_candidates(self.pack, "F01", environ=view)[0][1], self.url("r1"))


class ClassifyTests(unittest.TestCase):
    def test_error_codes(self):
        cases = ((socket.timeout("t"), "timeout"), (TimeoutError("t"), "timeout"),
                 (urllib.error.URLError(socket.timeout("t")), "timeout"),
                 (urllib.error.URLError(ssl.SSLCertVerificationError(1, "bad certificate")), "tls_error"),
                 (ssl.SSLCertVerificationError(1, "self-signed certificate"), "tls_error"),
                 (ssl.CertificateError("hostname mismatch"), "tls_error"),
                 (ssl.SSLError("handshake"), "network_error"),
                 (ssl.SSLEOFError(8, "EOF occurred in violation of protocol"), "network_error"),
                 (ssl.SSLZeroReturnError(6, "TLS/SSL connection has been closed"), "network_error"),
                 (urllib.error.URLError(ssl.SSLError(1, "[SSL: WRONG_VERSION_NUMBER] wrong version number")),
                  "network_error"),
                 (urllib.error.URLError(socket.gaierror(8, "no such host")), "dns_failed"),
                 (urllib.error.URLError(ConnectionRefusedError(61, "refused")), "connection_refused"),
                 (ConnectionResetError("reset"), "network_error"), (http.client.IncompleteRead(b"x"), "network_error"),
                 (http.client.RemoteDisconnected("gone"), "network_error"), (OSError("other"), "network_error"),
                 (urllib.error.URLError("unknown url type: x"), "bad_url"), (ValueError("x"), "bad_url"),
                 (http.client.InvalidURL("x"), "bad_url"), (UnicodeError("x"), "bad_url"))
        for exc, expected in cases:
            with self.subTest(repr(exc)):
                self.assertEqual(private._classify(exc), expected)

    def test_tls_error_means_certificate_verification_only(self):
        """The advice behind tls_error is "install root certificates"; it must not fire for other TLS trouble."""
        self.assertTrue(issubclass(ssl.SSLCertVerificationError, ValueError))      # why the check comes before bad_url
        for exc in (ssl.SSLEOFError(8, "eof"), ssl.SSLError(1, "alert handshake failure"),
                    ssl.SSLZeroReturnError(6, "closed"), ssl.SSLWantReadError(2, "want read")):
            with self.subTest(type(exc).__name__):
                self.assertNotEqual(private._classify(exc), "tls_error")
                self.assertNotEqual(private._classify(urllib.error.URLError(exc)), "tls_error")

    def test_only_dns_refusal_tls_and_timeouts_kill_a_host(self):
        self.assertEqual(set(private.HOST_ERRORS), {"timeout", "dns_failed", "connection_refused", "tls_error"})
        for error in ("network_error", "bad_url", "too_large", "redirect_blocked", "hash_mismatch", "http 500",
                      "http 404"):
            self.assertNotIn(error, private.HOST_ERRORS)


class ProbeTests(UrlCase):
    def test_probe_reports_how_far_the_fetch_got(self):
        self.server.route("/r3/F01.py", self.good)
        result = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
        self.assertEqual(result, {"kind": "url", "probedTask": "F01", "reachable": True, "via": self.url("r3"),
                                  "attempts": [{"via": self.url("r1"), "error": "http 404"},
                                               {"via": self.url("r2"), "error": "http 404"}]})
        self.assertEqual(self.leftovers(), [])

    def test_probe_of_an_unreachable_source(self):
        result = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
        self.assertEqual(sorted(result), ["attempts", "kind", "probedTask", "reachable", "via"])
        self.assertEqual((result["kind"], result["probedTask"], result["reachable"], result["via"]),
                         ("url", "F01", False, None))
        self.assertEqual([item["error"] for item in result["attempts"]], ["http 404"] * 3)

    def test_probe_checks_the_hash_like_a_fetch(self):
        self.server.route("/r1/F01.py", b"wrong")
        result = private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5)
        self.assertFalse(result["reachable"])
        self.assertEqual(result["attempts"][0]["error"], "hash_mismatch")

    def test_probe_result_is_json_serializable(self):
        self.server.route("/r1/F01.py", self.good)
        json.dumps(private.probe(self.pack, "F01", user_agent=UA, environ=self.env, timeout=5))


class PrivacyTests(UrlCase):
    def test_nothing_in_an_error_carries_response_content(self):
        sentinel = b"ZZ_RESPONSE_SENTINEL"
        self.server.route("/r1/F01.py", sentinel, status=500)
        self.server.route("/r2/F01.py", sentinel)
        exc = self.failure()
        self.assertNotIn("ZZ_RESPONSE_SENTINEL", json.dumps(exc.attempts) + str(exc) + repr(exc))

    def test_repr_of_a_fetched_verifier_has_no_source(self):
        self.server.route("/r1/F01.py", self.good)
        fetched = self.fetch()
        self.assertNotIn("ZZ_PRIVATE_SENTINEL", repr(fetched))
        self.assertIn("F01.py", repr(fetched))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
