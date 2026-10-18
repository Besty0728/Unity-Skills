<!-- usk-eval prompt; task T9; lang en -->
R1. The script `{path}` (class `{cls}`) is on Guard_1, Guard_2 and Guard_3 under `{guards}`, and each Guard has its own speed, waypoint and laps values. In that script, rename the field `speed` to `moveSpeed` and the field `waypoint` to `patrolTarget`; keep the class name, the other fields, and the types and default values of all fields as they are.
R2. Data saved under the old field names must keep loading into the renamed fields: after the rename each of the three Guards still has its previous speed value in moveSpeed, its previous waypoint reference in patrolTarget and its laps value, and the same holds for any scene or prefab saved with the old names that is opened later.
R3. Do not set moveSpeed, patrolTarget, speed or waypoint on the Guards through skills: the values must survive the rename by themselves.
R4. Edit this script file itself: do not delete, recreate, move or rename the file (its GUID stays the same); do not change any other script; add no other code file (editor scripts included).
R5. Do not modify any other objects or assets.
R6. When the scene runs in Play Mode, no error or exception may be logged, and at runtime each Guard's moveSpeed, patrolTarget and laps still hold the values described in R2.
R7. In the answer, report the asset path of the script and, for each Guard, its instanceId, its moveSpeed value and the instanceId of the GameObject that its patrolTarget references.
