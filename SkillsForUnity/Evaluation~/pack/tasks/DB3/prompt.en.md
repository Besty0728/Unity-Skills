<!-- usk-eval prompt; task DB3; lang en -->
R1. Under `{scripts}`, create the script `{cls}.cs` declaring `public class {cls} : MonoBehaviour` with a serialized float field `interval` (public, or marked [SerializeField]), in seconds, whose default value in the code is {default}.
R2. Every `interval` seconds of game time the component toggles the enabled state of the Light component on the GameObject it sits on: during the first interval after Play starts the light keeps the state it started in (on), then it goes off, after another interval on again, and so on; the component changes nothing at Start and nothing else.
R3. Attach exactly one `{cls}` component to `{lamp}` and set its `interval` to {interval} in the scene (the value must be stored in the scene, not only as the code default).
R4. Do not attach `{cls}` to any other object, do not modify other objects (`{other}` must stay on), and add no code file other than this script (editor scripts included).
R5. When the scene runs in Play Mode no error or exception may be logged, and sampled 0.5, 1.5, 2.5 and 3.5 intervals after Play starts, the Light of `{lamp}` reads on, off, on, off.
R6. In the answer, report the asset path of the script, the instanceId of the light object carrying the component, and the interval value stored in the scene.
