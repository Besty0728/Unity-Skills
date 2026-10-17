<!-- usk-eval prompt; task T4; lang en -->
R1. Under `{room}`, `Car` and `CarTwin` are two cubes with two material slots each: slot 0 is their shared body paint `{body}`, slot 1 is `{trim}`. Give `Car` a new body material as its slot 0: save the material as an asset under `{A}` (name and path up to you), using the default Lit shader of this project's render pipeline, with main colour RGBA (0.8, 0.1, 0.1, 1).
R2. Create a new material `{glow}`, save it as `{glowpath}`, using the default Lit shader of this project's render pipeline, with emission enabled and the HDR emission colour's RGB set to (2, 1, 0).
R3. Replace `Car`'s slot 1, `{trim}`, with `{glow}`; `Car` still has exactly two material slots, and slot 0 stays the new body material.
R4. `CarTwin`'s appearance and its two material slots (`{body}`, `{trim}`) must not change; the contents of the material assets `{body}` and `{trim}` must not change either.
R5. Apart from the changes above, do not make any other modification: `Car` itself (name, position, components) and every other object and asset stay unchanged.
R6. In the answer, report the asset paths of the materials used by `Car`'s slot 0 and slot 1.
