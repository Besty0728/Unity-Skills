"""DB2 reference, negatives, and alternatives (Health MonoBehaviour with TakeDamage, Heal, and death event)."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative, address

SOURCE = """using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default};
    public int currentHealth;
    public int deathCount;
    public UnityEvent onDied;
    public event Action Died;

    void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        int prev = currentHealth;
        currentHealth = Mathf.Max(0, currentHealth - amount);
        if (prev > 0 && currentHealth == 0)
        {{
            deathCount += 1;
            onDied?.Invoke();
            Died?.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        return currentHealth;
    }}
}}
"""

SERIALIZE_FIELD_SOURCE = """using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    [SerializeField] private int maxHealth = {default};
    [SerializeField] private int currentHealth;
    [SerializeField] private int deathCount;
    [SerializeField] private UnityEvent onDied;
    public event Action Died;

    private void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0)
        {{
            return currentHealth;
        }}
        bool wasAlive = currentHealth > 0;
        currentHealth = currentHealth > amount ? currentHealth - amount : 0;
        if (wasAlive && currentHealth == 0)
        {{
            deathCount++;
            if (onDied != null) onDied.Invoke();
            if (Died != null) Died.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0)
        {{
            return currentHealth;
        }}
        int next = currentHealth + amount;
        currentHealth = next > maxHealth ? maxHealth : next;
        return currentHealth;
    }}
}}
"""

COMBINED_SOURCE = """using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    // the code default stays "maxHealth = 100"; one line declares both counters
    [SerializeField, Min(1)] private int maxHealth = {default};
    [Tooltip("hit points and deaths, see https://docs.unity3d.com")] public int currentHealth, deathCount;
    [SerializeField] private UnityEvent onDied = new UnityEvent();
    public event Action Died;

    private void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = Mathf.Max(0, currentHealth - amount);
        if (currentHealth == 0) Die();
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        return currentHealth;
    }}

    private void Die()
    {{
        deathCount++;
        onDied?.Invoke();
        Died?.Invoke();
    }}
}}
"""

UNCLAMPED_DAMAGE_SOURCE = """// usk-fake: unclamped_damage
using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default};
    public int currentHealth;
    public int deathCount;
    public UnityEvent onDied;
    public event Action Died;

    void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        int prev = currentHealth;
        currentHealth = currentHealth - amount;
        if (prev > 0 && currentHealth <= 0)
        {{
            deathCount += 1;
            onDied?.Invoke();
            Died?.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        return currentHealth;
    }}
}}
"""

REPEAT_DEATH_SOURCE = """// usk-fake: repeat_death
using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default};
    public int currentHealth;
    public int deathCount;
    public UnityEvent onDied;
    public event Action Died;

    void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0) return currentHealth;
        currentHealth = Mathf.Max(0, currentHealth - amount);
        if (currentHealth == 0)
        {{
            deathCount += 1;
            onDied?.Invoke();
            Died?.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        return currentHealth;
    }}
}}
"""

UNCAPPED_HEAL_SOURCE = """// usk-fake: uncapped_heal
using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default};
    public int currentHealth;
    public int deathCount;
    public UnityEvent onDied;
    public event Action Died;

    void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        int prev = currentHealth;
        currentHealth = Mathf.Max(0, currentHealth - amount);
        if (prev > 0 && currentHealth == 0)
        {{
            deathCount += 1;
            onDied?.Invoke();
            Died?.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = currentHealth + amount;
        return currentHealth;
    }}
}}
"""

HEAL_WHEN_DEAD_SOURCE = """// usk-fake: heal_when_dead
using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default};
    public int currentHealth;
    public int deathCount;
    public UnityEvent onDied;
    public event Action Died;

    void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        int prev = currentHealth;
        currentHealth = Mathf.Max(0, currentHealth - amount);
        if (prev > 0 && currentHealth == 0)
        {{
            deathCount += 1;
            onDied?.Invoke();
            Died?.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0) return currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        return currentHealth;
    }}
}}
"""

NEGATIVE_DAMAGE_HEALS_SOURCE = """// usk-fake: negative_damage_heals
using System;
using UnityEngine;
using UnityEngine.Events;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default};
    public int currentHealth;
    public int deathCount;
    public UnityEvent onDied;
    public event Action Died;

    void Awake()
    {{
        currentHealth = maxHealth;
        deathCount = 0;
    }}

    public int TakeDamage(int amount)
    {{
        if (currentHealth <= 0) return currentHealth;
        int prev = currentHealth;
        currentHealth = Mathf.Max(0, currentHealth - amount);
        if (prev > 0 && currentHealth == 0)
        {{
            deathCount += 1;
            onDied?.Invoke();
            Died?.Invoke();
        }}
        return currentHealth;
    }}

    public int Heal(int amount)
    {{
        if (amount <= 0 || currentHealth <= 0) return currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        return currentHealth;
    }}
}}
"""

BROKEN_SOURCE = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int maxHealth = {default}
    void Awake() {{ currentHealth = maxHealth; }}
}}
"""

HELPER_SOURCE = """using UnityEngine;

public class Helper_{cls} : MonoBehaviour
{{
    public int extra = 1;
}}
"""


def _write_script(ctx, content, name=None):
    h = ctx.handles
    res = ctx.c.write("script_create", scriptName=name or h["className"], folder=h["scripts"], content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res


def _attach(ctx, target_iid, max_health=None, private_field=False):
    """component_set_property reaches public members only; a [SerializeField] private field takes the serialized
    property path."""
    cls = ctx.handles["className"]
    ctx.c.write("component_add", componentType=cls, **address(target_iid))
    if max_health is not None and private_field:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="maxHealth",
                    value=str(max_health), **address(target_iid))
    elif max_health is not None:
        ctx.c.write(
            "component_set_property",
            componentType=cls,
            propertyName="maxHealth",
            value=str(max_health),
            **address(target_iid),
        )


def _answer(ctx, unit_iid=None, max_health=None):
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    uid = h["unit"] if unit_iid is None else unit_iid
    return {
        "scriptPath": h["script"],
        "unitInstanceId": int(uid) if isinstance(uid, str) else uid,
        "maxHealth": s["maxHealth"] if max_health is None else max_health,
        "declaredComplete": True,
    }


def reference(ctx):
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def serialize_field_form(ctx):
    """Alternative: uses [SerializeField] private fields and explicit null checks instead of public fields."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, SERIALIZE_FIELD_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"], private_field=True)
    return _answer(ctx)


def save_run_scene(ctx):
    """Alternative: saves the active run scene (allowed by G2)."""
    ans = reference(ctx)
    ctx.c.write("scene_save")
    return ans


def combined_declarations(ctx):
    """Alternative: an attribute list on maxHealth, both counters on one line, the event fired from a Die() helper."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, COMBINED_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"], private_field=True)
    return _answer(ctx)


# ---------------------------------------------------------------- negatives


def wrong_unit(ctx):
    """Attaches the component to the other unit instead of the requested unit."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["otherUnit"], s["maxHealth"])
    return _answer(ctx, unit_iid=h["otherUnit"])


def default_only(ctx):
    """Attaches the component without setting maxHealth in the scene (leaves the code default 100)."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], max_health=None)
    return _answer(ctx, max_health=s["defaultMaxHealth"])


def unclamped_damage(ctx):
    """TakeDamage subtracts without clamping at 0, allowing negative health."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, UNCLAMPED_DAMAGE_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def repeat_death(ctx):
    """TakeDamage increments deathCount and fires onDied every time health is 0, even when already dead."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, REPEAT_DEATH_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def uncapped_heal(ctx):
    """Heal adds amount without capping at maxHealth."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, UNCAPPED_HEAL_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def heal_when_dead(ctx):
    """Heal revives a dead unit at 0 health instead of leaving it unchanged."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, HEAL_WHEN_DEAD_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def negative_damage_heals(ctx):
    """TakeDamage does not ignore negative amounts, so negative damage increases currentHealth."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, NEGATIVE_DAMAGE_HEALS_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def silent_death(ctx):
    """deathCount counts the death but onDied is never invoked (the runtime numbers are all right)."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, SOURCE.replace("            onDied?.Invoke();\n", "").format(cls=h["className"],
                                                                                default=s["defaultMaxHealth"]))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def wrong_default(ctx):
    """Code default for maxHealth is 50 instead of 100, though the scene value is set to maxHealth."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, SOURCE.format(cls=h["className"], default=50))
    _attach(ctx, h["unit"], s["maxHealth"])
    return _answer(ctx)


def compile_error(ctx):
    """Writes a script with a syntax error that fails to compile."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    _write_script(ctx, BROKEN_SOURCE.format(cls=h["className"], default=s["defaultMaxHealth"]))
    return _answer(ctx)


def extra_script(ctx):
    """Adds an unrequested helper C# script under Assets."""
    h = ctx.handles
    ans = reference(ctx)
    _write_script(ctx, HELPER_SOURCE.format(cls=h["className"]), name="Helper_" + h["className"])
    return ans


def two_components(ctx):
    """Attaches two instances of the Health component to the target unit."""
    h = ctx.handles
    s = ctx.private.spec(ctx.seed)
    ans = reference(ctx)
    _attach(ctx, h["unit"], s["maxHealth"])
    return ans


def wrong_report(ctx):
    """Completes the task properly but reports the wrong unitInstanceId in the answer."""
    ans = reference(ctx)
    other = ctx.handles["otherUnit"]
    ans["unitInstanceId"] = int(other) if isinstance(other, str) else other
    return ans


def outside_root(ctx):
    """Creates a stray GameObject at the scene root outside R (violates G1)."""
    ans = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayUnit")
    return ans


def direct_file(ctx):
    """Writes a non-script file directly to disk under A, bypassing UnitySkills (violates G4)."""
    ans = reference(ctx)
    note = os.path.join(ctx.project_path, *ctx.asset("note.txt").split("/"))
    os.makedirs(os.path.dirname(note), exist_ok=True)
    with open(note, "wb") as f:
        f.write(b"direct")
    return ans


NEGATIVES = [
    Negative("wrong_unit", wrong_unit, {"H3", "H5", "H6"}, "component attached to the other unit instead of the target unit"),
    Negative("default_only", default_only, {"H3", "H6"}, "maxHealth left at the code default 100 instead of set in the scene"),
    Negative("unclamped_damage", unclamped_damage, {"H6"}, "TakeDamage does not clamp currentHealth at 0"),
    Negative("repeat_death", repeat_death, {"H6"}, "death event / deathCount fires again when already dead"),
    Negative("uncapped_heal", uncapped_heal, {"H6"}, "Heal does not cap currentHealth at maxHealth"),
    Negative("heal_when_dead", heal_when_dead, {"H6"}, "Heal revives a dead unit at 0 health"),
    Negative("negative_damage_heals", negative_damage_heals, {"H6"}, "negative TakeDamage amount increases health"),
    Negative("silent_death", silent_death, {"H1"}, "deathCount counts the death but onDied is never invoked"),
    Negative("wrong_default", wrong_default, {"H1"}, "code default of maxHealth is 50 instead of 100"),
    Negative("compile_error", compile_error, {"H1", "H2", "H3", "H6", "H7"}, "script has a syntax error and cannot be attached"),
    Negative("extra_script", extra_script, {"H4"}, "an extra unrequested C# script was created"),
    Negative("two_components", two_components, {"H3", "H6"}, "two Health components attached to the target unit"),
    Negative("wrong_report", wrong_report, {"H7"}, "answer reports the other unit's instanceId"),
    Negative("outside_root", outside_root, {"H8"}, "GameObject created outside the object root R"),
    Negative("direct_file", direct_file, {"H9"}, "non-script file written directly to disk"),
]

ALTERNATIVES = [
    ("serialize_field_form", serialize_field_form),
    ("save_run_scene", save_run_scene),
    ("combined_declarations", combined_declarations),
]

# Producer:Betsy
