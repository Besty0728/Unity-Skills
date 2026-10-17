"""Synthetic task F01 for the pack loader tests: it never talks to the Editor."""
from usk_eval.task_api import PublicTask


class F01(PublicTask):
    id = "F01"
    track = "ops"
    tier = "basic"
    case_version = 2
    minutes = 5
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        return {"props": ctx.path("Props")}

    def snapshot(self, ctx):
        return {"data": {}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"props": ctx.path("Props")}

    def answer_schema(self):
        return {"type": "object", "required": ["name"], "properties": {"name": {"type": "string"}}}


TASK = F01()

# Producer:Betsy
