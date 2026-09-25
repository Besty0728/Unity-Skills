#!/usr/bin/env python3
"""Per-module coverage floor for a Unity NUnit results file: counts the tests whose full name starts with each
module's prefix and that actually passed. A fixture that ignores itself (package missing, probe broken) passes CI
with zero failures, so only a floor on *passed* tests catches a module that silently stopped running.

    nunit_floors.py <results.xml> <floors.json> <key>

floors.json maps key -> {module: {"prefix": "...", "min": N}}. Exit 1 when any module is below its floor.
"""
from __future__ import annotations

import json
import sys
import xml.etree.ElementTree as ET


def passed_by_module(xml_text: str, floors: dict) -> dict:
    root = ET.fromstring(xml_text)
    passed = [case.get("fullname", "") for case in root.iter("test-case") if case.get("result") == "Passed"]
    return {module: sum(1 for name in passed if name.startswith(spec["prefix"])) for module, spec in floors.items()}


def check(xml_text: str, floors: dict) -> list:
    if not floors:
        return ["no module floors registered"]
    counts = passed_by_module(xml_text, floors)
    problems = []
    for module, spec in sorted(floors.items()):
        minimum = int(spec["min"])
        if minimum <= 0:
            problems.append(f"{module}: floor must be positive, got {minimum}")
        elif counts[module] < minimum:
            problems.append(f"{module}: {counts[module]} passed < floor {minimum}")
    return problems


def main(argv: list) -> int:
    if len(argv) != 4:
        print(__doc__)
        return 2
    with open(argv[1], encoding="utf-8") as f:
        xml_text = f.read()
    with open(argv[2], encoding="utf-8") as f:
        floors = json.load(f).get(argv[3]) or {}
    counts = passed_by_module(xml_text, floors) if floors else {}
    for module in sorted(counts):
        print(f"{module}: {counts[module]} passed (floor {floors[module]['min']})")
    problems = check(xml_text, floors)
    for problem in problems:
        print(f"FLOOR MISSED  {problem}")
    print("floors: " + ("FAIL" if problems else "PASS"))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

# Producer:Betsy
