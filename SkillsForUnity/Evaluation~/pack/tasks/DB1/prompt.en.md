<!-- usk-eval prompt; task DB1; lang en -->
R1. Under `{scripts}`, create the script `{cls}.cs` declaring `public class {cls} : MonoBehaviour` with a serialized float field `degreesPerSecond` (public, or marked [SerializeField]) whose default value in the code is {default}.
R2. Every frame the component rotates the GameObject it sits on around that object's own local Y axis at `degreesPerSecond` degrees per second of game time, independent of the frame rate (scaled by Time.deltaTime, not a fixed angle per frame); it changes nothing else.
R3. Attach exactly one `{cls}` component to `{blades}` and set its `degreesPerSecond` to {speed} in the scene (the value must be stored in the scene, not only as the code default).
R4. Do not attach `{cls}` to any other object (including `{other}`), do not modify other objects, and add no code file other than this script (editor scripts included).
R5. When the scene runs in Play Mode no error or exception may be logged, and `{blades}` keeps turning at that rate: after {wait} seconds it has turned about {turn} degrees.
R6. In the answer, report the asset path of the script, the instanceId of the object carrying the component, and the degreesPerSecond value stored in the scene.
