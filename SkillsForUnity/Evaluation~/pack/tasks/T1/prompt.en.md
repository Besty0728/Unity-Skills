<!-- usk-eval prompt; task T1; lang en -->
R1. Under `{scripts}`, create the two C# scripts below, each file named after its class, with exactly this content (add or remove nothing).
   `{cfg_class}.cs`:{cfg_src}
   `{ai_class}.cs`:{ai_src}
R2. Once both scripts compile, create the `{cfg_class}` asset `{asset}` with fireRate = 2.5, burstCount = 3 and projectilePrefab pointing at `{prefab}`.
R3. `{turrets}` holds three Turret objects with the same name; add the `{ai_class}` component only to the one with the largest world X, with config pointing at the asset from R2, target pointing at the Transform of the Dummy tagged Respawn under `{range}`, and turnSpeed = 90.
R4. Apart from these two scripts, do not add or change any code file (editor scripts included).
R5. Do not modify any other objects or assets.
R6. When the scene runs in Play Mode, no error or exception may be logged, and at runtime the `{ai_class}` component still has the config, target and turnSpeed set above.
R7. In the answer, report the asset paths of the two scripts, the path of the config asset, the instanceId of the Turret that got the component, and the instanceId of the Dummy that target points at.
