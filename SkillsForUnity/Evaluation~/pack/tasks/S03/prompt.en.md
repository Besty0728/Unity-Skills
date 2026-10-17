<!-- usk-eval prompt; task S03; lang en -->
R1. Move the object `{lamp}` itself under `{props}` (do not replace it with a newly created object), and make it the last child of `{props}`.
R2. After the move, Lamp's local position, local rotation and local scale relative to its new parent must have exactly the same values as its local values relative to its old parent before the move (keep the local values, not the world pose).
R3. Keep Lamp's children and components (including the components' enabled states and order) unchanged.
R4. Do not modify any other object, and do not change the order of any other object's children.
R5. In the answer, report Lamp's full hierarchy path after the move, and its local position, local Euler angles and local scale after the move.
