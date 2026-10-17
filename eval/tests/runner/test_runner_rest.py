"""rest.py against an editor that is starting a domain reload: an empty or cut-short answer is unreadable (reads
retry, writes are WriteUncertain), and a skill missing from a schema answer is asked for again, never cached."""
import http.client
import socket
import unittest
from unittest import mock

from .runner_support import Bench, rest


class ReloadingAnswerTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        target = type("T", (), {"instance_id": self.b.unity.instance_id,
                                "base_url": "http://127.0.0.1:%d" % self.b.unity.port})()
        self.client = rest.Client(target, retry_window_s=5.0)
        patcher = mock.patch.object(rest, "TRANSIENT_WAIT_S", 0.01)
        patcher.start()
        self.addCleanup(patcher.stop)

    def sent(self, path):
        return sum(1 for _method, sent_path, _agent in self.b.unity.requests if sent_path == path)

    def test_an_empty_answer_to_a_read_is_retried_and_counted(self):
        self.b.unity.spoil["/health"] = ["empty", "empty"]
        before = rest.REQUESTS.value
        self.assertEqual(self.client.health()["instanceId"], self.b.unity.instance_id)
        self.assertEqual((self.sent("/health"), rest.REQUESTS.value - before), (3, 3))

    def test_an_empty_answer_to_a_write_is_uncertain_and_not_replayed(self):
        self.b.unity.spoil["/skill/gameobject_create"] = ["empty"]
        with self.assertRaises(rest.WriteUncertain) as caught:
            self.client.write("gameobject_create", name="Probe")
        self.assertIn("empty answer", str(caught.exception))
        self.assertEqual(self.sent("/skill/gameobject_create"), 1)
        self.assertEqual(self.b.unity.objects, {})

    def test_a_cut_short_answer_is_unreadable(self):
        self.b.unity.spoil["/health"] = ["cut"]
        self.assertEqual(self.client.health()["instanceId"], self.b.unity.instance_id)
        self.assertEqual(self.sent("/health"), 2)
        self.b.unity.spoil["/skill/gameobject_create"] = ["cut"]
        with self.assertRaises(rest.WriteUncertain) as caught:
            self.client.write("gameobject_create", name="Probe")
        self.assertIn("cut-short answer", str(caught.exception))
        self.assertEqual([o["name"] for o in self.b.unity.objects.values()], ["Probe"])

    def test_a_garbled_answer_is_unreadable(self):
        self.b.unity.spoil["/health"] = ["garbled"]
        self.assertEqual(self.client.health()["instanceId"], self.b.unity.instance_id)
        self.assertEqual(self.sent("/health"), 2)
        self.b.unity.spoil["/skill/gameobject_create"] = ["garbled"]
        with self.assertRaises(rest.WriteUncertain) as caught:
            self.client.write("gameobject_create", name="Probe")
        self.assertIn("garbled answer", str(caught.exception))
        self.assertEqual(self.sent("/skill/gameobject_create"), 1)

    def test_a_timeout_while_reading_the_body_counts_one_request(self):
        before = rest.REQUESTS.value
        with mock.patch.object(http.client.HTTPResponse, "read", side_effect=socket.timeout("timed out")):
            with self.assertRaises(socket.timeout):
                self.client._once("GET", "/health")
        self.assertEqual(rest.REQUESTS.value - before, 1)

    def test_an_empty_schema_answer_does_not_poison_the_cache(self):
        self.b.unity.spoil["/skills/schema"] = ["empty"]
        self.assertEqual(self.client.read("scene_get_info")["scenePath"], "Assets/Scenes/Main.unity")
        self.assertIs(self.client.read("editor_get_state")["isPlaying"], False)
        self.assertEqual(self.sent("/skills/schema"), 3)

    def test_a_name_left_out_of_a_schema_answer_is_asked_for_again(self):
        answers = [{"wire": "v2", "skills": []}]
        real_get = self.client.get

        def get(path, params=None, timeout=None):
            return answers.pop(0) if answers else real_get(path, params, timeout)

        with mock.patch.object(self.client, "get", side_effect=get) as patched:
            self.assertTrue(self.client.is_read_only("scene_get_info"))
            self.assertFalse(self.client.is_read_only("gameobject_create"))
        self.assertEqual(patched.call_count, 3)

    def test_a_skill_the_editor_does_not_list_is_reported_and_not_cached(self):
        with mock.patch.object(self.client, "get", return_value={"wire": "v2", "skills": []}) as patched:
            for _ in range(2):
                with self.assertRaises(rest.RestError) as caught:
                    self.client.read("no_such_skill")
                self.assertEqual(str(caught.exception), "/skills/schema has no entry for no_such_skill (asked twice)")
        self.assertEqual(patched.call_count, 4)
        self.assertNotIn("no_such_skill", self.client._schema)

    def test_writers_are_still_refused_as_reads(self):
        with self.assertRaises(rest.RestError) as caught:
            self.client.read("gameobject_create", name="Probe")
        self.assertEqual(str(caught.exception), "refused non-read-only skill gameobject_create")
        self.assertEqual(self.sent("/skill/gameobject_create"), 0)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
