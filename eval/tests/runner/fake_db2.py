"""In-memory Unity fake editor for DB2 offline tests.

Subclasses FakeEditorDB to add serialized-field parsing beyond `public type name = value;` (a [SerializeField] field
of any access, attribute lists, comma-separated declarators: each serialized declarator is handed to the base parser
as its own `public` line), Health_<word>_<runId> component initialization in Play Mode (with scene state restoration
on exit), and TakeDamage(int) / Heal(int) method execution via component_invoke_method honoring the
`// usk-fake: <variant>` markers in the negative solutions.
"""
import copy
import json
import re

from .fake_dev_db import FakeEditorDB
from .pack_fake_editor import RUNNER, _fail
from .pack_offline import OpsFakeEditor

DECL_LINE_RE = re.compile(r"^(?P<indent>[ \t]*)(?P<attrs>(?:\[[^\]\n]*\]\s*)*)"
                          r"(?P<mods>(?:(?:public|private|protected|internal)\s+)*)(?P<type>[A-Za-z_][\w.<>]*)\s+"
                          r"(?P<decls>[A-Za-z_]\w*(?:\s*=\s*[^;,\n]+)?(?:\s*,\s*[A-Za-z_]\w*(?:\s*=\s*[^;,\n]+)?)*)\s*;",
                          re.M)


def normalize(text):
    """Every serialized field declaration as one `public type name [= value];` line per declarator."""
    def rewrite(match):
        serialized = "public" in match.group("mods").split() or "SerializeField" in match.group("attrs")
        if not serialized or match.group("type") in ("class", "event", "return", "using", "new"):
            return match.group(0)
        lines = []
        for part in match.group("decls").split(","):
            name, _eq, init = part.partition("=")
            lines.append("%spublic %s %s%s;" % (match.group("indent"), match.group("type"), name.strip(),
                                                " = " + init.strip() if init.strip() else ""))
        return "\n".join(lines)
    return DECL_LINE_RE.sub(rewrite, text)


class FakeEditorDB2(FakeEditorDB):
    def _compile(self):
        original = self.scripts
        self.scripts = {path: normalize(text) for path, text in original.items()}
        try:
            OpsFakeEditor._compile(self)
        finally:
            self.scripts = original

    def _enter_play(self):
        super(FakeEditorDB2, self)._enter_play()
        for obj, cls in self._components("Health_"):
            if not self.active_in_hierarchy(obj):
                continue
            fields = obj.fields.get(cls) or {}
            max_hp = int(fields.get("maxHealth") if fields.get("maxHealth") is not None else 100)
            fields["currentHealth"] = max_hp
            fields["deathCount"] = 0

    def run_scenario(self, spec, agent=RUNNER):
        saved = copy.deepcopy((self.objects, self.roots))
        try:
            return super(FakeEditorDB2, self).run_scenario(spec, agent)
        finally:
            self.objects, self.roots = saved

    def _skill_component_set_property(self, agent, args):
        """Like the plugin's reflection, reaches public members only: a [SerializeField] private field is not found."""
        cls, name = str(args.get("componentType") or ""), str(args.get("propertyName") or "")
        text = next((text for path, text in self.scripts.items() if path.endswith("/%s.cs" % cls)), "")
        for match in DECL_LINE_RE.finditer(text):
            names = [part.partition("=")[0].strip() for part in match.group("decls").split(",")]
            if name in names and "public" not in match.group("mods").split():
                raise _fail("SEMANTIC_INVALID", "Property/field not found: %s" % name)
        return super(FakeEditorDB2, self)._skill_component_set_property(agent, args)

    def _skill_component_invoke_method(self, agent, args):
        ctype = str(args.get("componentType") or "")
        method = str(args.get("methodName") or "")
        if ctype.startswith("Health_") and method in ("TakeDamage", "Heal"):
            obj = self.find(args)
            matches = [kind for kind, _enabled in obj.components if kind == ctype]
            idx = int(args.get("componentIndex") or 0)
            if idx < 0 or idx >= len(matches) or ctype not in obj.fields:
                raise _fail("COMPONENT_NOT_FOUND", "no %s on %s" % (ctype, obj.name))
            raw_args = args.get("arguments")
            parsed_args = json.loads(raw_args) if isinstance(raw_args, str) and raw_args else (raw_args or [])
            amount = int(parsed_args[0]) if parsed_args else 0
            variants = self._variants(ctype)
            ret = self._exec_health_call(obj.fields[ctype], method, amount, variants)
            return {
                "success": True,
                "status": "completed",
                "returnValue": ret,
                "refOut": {},
                "signature": "%s(System.Int32)" % method,
            }
        return super(FakeEditorDB2, self)._skill_component_invoke_method(agent, args)

    @staticmethod
    def _exec_health_call(fields, method, amount, variants):
        max_hp = int(fields.get("maxHealth") if fields.get("maxHealth") is not None else 100)
        cur_hp = int(fields.get("currentHealth") if fields.get("currentHealth") is not None else max_hp)
        deaths = int(fields.get("deathCount") if fields.get("deathCount") is not None else 0)

        if method == "TakeDamage":
            if "negative_damage_heals" in variants:
                if cur_hp <= 0:
                    return cur_hp
                prev = cur_hp
                cur_hp = max(0, cur_hp - amount)
                if prev > 0 and cur_hp == 0:
                    deaths += 1
            elif "unclamped_damage" in variants:
                if amount <= 0 or cur_hp <= 0:
                    return cur_hp
                prev = cur_hp
                cur_hp = cur_hp - amount
                if prev > 0 and cur_hp <= 0:
                    deaths += 1
            elif "repeat_death" in variants:
                if amount <= 0:
                    return cur_hp
                cur_hp = max(0, cur_hp - amount)
                if cur_hp == 0:
                    deaths += 1
            else:
                if amount <= 0 or cur_hp <= 0:
                    return cur_hp
                prev = cur_hp
                cur_hp = max(0, cur_hp - amount)
                if prev > 0 and cur_hp == 0:
                    deaths += 1
        elif method == "Heal":
            if "uncapped_heal" in variants:
                if amount <= 0 or cur_hp <= 0:
                    return cur_hp
                cur_hp = cur_hp + amount
            elif "heal_when_dead" in variants:
                if amount <= 0:
                    return cur_hp
                cur_hp = min(max_hp, cur_hp + amount)
            else:
                if amount <= 0 or cur_hp <= 0:
                    return cur_hp
                cur_hp = min(max_hp, cur_hp + amount)

        fields["currentHealth"] = cur_hp
        fields["deathCount"] = deaths
        return cur_hp

# Producer:Betsy
