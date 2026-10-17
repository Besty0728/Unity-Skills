"""Synthetic task F02 for the pack loader tests: a dev task that needs a dedicated project."""
from usk_eval.task_api import PublicTask


class F02(PublicTask):
    id = "F02"
    track = "dev"
    tier = "standard"
    case_version = 1
    minutes = 8
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": ["com.unity.cinemachine"]}

    def build(self, ctx):
        return {}

    def snapshot(self, ctx):
        return {"data": {}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"scripts": ctx.asset("Scripts")}

    def answer_schema(self):
        return {"type": "object", "required": [], "properties": {}}

    def teardown(self, ctx):
        return {"restored": True}


TASK = F02()

# Producer:Betsy
