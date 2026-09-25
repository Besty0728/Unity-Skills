#!/usr/bin/env python3
"""Tests for nunit_floors.py: a module counts only tests that passed, so an all-ignored fixture fails its floor.

    python3 -m unittest discover -s .github/scripts/tests -t .github/scripts/tests
"""
from __future__ import annotations

import importlib.util
import os
import unittest

SCRIPT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "nunit_floors.py")
spec = importlib.util.spec_from_file_location("nunit_floors", SCRIPT)
floors_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(floors_module)

PREFIX = "UnitySkills.Tests.OptionalPackages."


def results(*cases) -> str:
    body = "".join(f'<test-case fullname="{name}" result="{result}"/>' for name, result in cases)
    return f'<test-run><test-suite>{body}</test-suite></test-run>'


FLOORS = {
    "Urp": {"prefix": PREFIX + "UrpSkillsLiveTests.", "min": 2},
    "Decal": {"prefix": PREFIX + "DecalSkillsLiveTests.", "min": 1},
}


class CheckTestFloorsTests(unittest.TestCase):
    def test_passes_when_every_module_meets_its_floor(self):
        xml = results((PREFIX + "UrpSkillsLiveTests.A", "Passed"), (PREFIX + "UrpSkillsLiveTests.B", "Passed"),
                      (PREFIX + "DecalSkillsLiveTests.A", "Passed"))
        self.assertEqual(floors_module.check(xml, FLOORS), [])

    def test_ignored_and_failed_tests_do_not_count(self):
        xml = results((PREFIX + "UrpSkillsLiveTests.A", "Passed"), (PREFIX + "UrpSkillsLiveTests.B", "Skipped"),
                      (PREFIX + "DecalSkillsLiveTests.A", "Failed"))
        problems = floors_module.check(xml, FLOORS)
        self.assertEqual(len(problems), 2, problems)

    def test_prefixes_do_not_bleed_into_similar_class_names(self):
        xml = results((PREFIX + "UrpSkillsLiveTestsExtra.A", "Passed"), (PREFIX + "UrpSkillsLiveTestsExtra.B", "Passed"),
                      (PREFIX + "DecalSkillsLiveTests.A", "Passed"))
        self.assertEqual(len(floors_module.check(xml, FLOORS)), 1)

    def test_missing_floors_and_non_positive_floors_fail(self):
        xml = results((PREFIX + "UrpSkillsLiveTests.A", "Passed"))
        self.assertEqual(floors_module.check(xml, {}), ["no module floors registered"])
        self.assertTrue(floors_module.check(xml, {"Urp": {"prefix": PREFIX + "UrpSkillsLiveTests.", "min": 0}}))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
