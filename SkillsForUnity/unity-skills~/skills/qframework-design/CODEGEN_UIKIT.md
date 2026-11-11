# CodeGenKit & UIKit

Sub-doc of [qframework-design](./MODULE.md). Covers code generation (ViewController + Bind) and the UI panel/element workflow built on top of it.

## CodeGenKit: two-phase generation

Code generation is not written in one step. It is a two-phase process: write files, trigger compilation, then after compilation fill in the component references. Clicking the generate button in `CodeGenKitPipeline.cs` first writes the `.Designer.cs` file, then Unity compiles. When compilation finishes, the `[DidReloadScripts]` callback attaches the generated fields to the matching GameObjects via `SerializedProperty` (`CodeGenKitPipeline.cs:179-183`).

```csharp
[DidReloadScripts]
static void Compile()
{
    Default.OnCompile();
}
```

This means: if compilation fails after generation, component binding does not complete. Fix the compile errors first, then check whether the binding took effect. Do not suspect the generator when compilation has failed.

### Rule: Designer files are overwritten each time; logic files are generated only once

`Player.Designer.cs` is fully rewritten every time you click "生成代码" ("Generate Code"). `Player.cs` (the logic file) is created only on first generation and is never overwritten afterward (Doc.md:5176-5235). This is why you can safely write business code in `Player.cs`: the generation flow will not consume it.

```csharp
// Player.Designer.cs - rewritten on every generation
namespace QFramework.Example
{
    public partial class Player
    {
        public Transform Weapon;
        public Transform GroundCheck; // new fields are appended here
    }
}
```

### Rule: after changing the namespace, update the logic file manually

The generated Designer file starts with a fixed hint comment (Doc.md:5186-5187, 5219-5220):

```csharp
// 1. Set the namespace in the menu "编辑器扩展/Namespace Settings" (Editor Extensions / Namespace Settings)
// 2. After changing the namespace, generate the code, then manually change the namespace of the logic code file (non-Designer)
```

CodeGenKit writes only the new namespace into `.Designer.cs`. Logic `.cs` files that were already generated do not follow along, so the two files end up with mismatched namespaces and cause compile errors. After changing the namespace, remember to sync the logic file manually.

### Rule: an empty ScriptsFolder does not fall back to Assets/Scripts

In `CodeGenKitPipeline.cs:126-170`, `ScriptsFolder` is forced to `"Assets/Scripts"` only when the GameObject does **not** have a `ViewController` component. If it has a `ViewController`, even when its `ScriptsFolder` field is an empty string, that value is written to `serializedObject` as-is, with no default fallback:

```csharp
var codeGenerateInfo = gameObject.GetComponent<ViewController>();
if (codeGenerateInfo)
{
    serializedObject.FindProperty("ScriptsFolder").stringValue = codeGenerateInfo.ScriptsFolder; // an empty string is written as-is
    // ...
}
else
{
    serializedObject.FindProperty("ScriptsFolder").stringValue = "Assets/Scripts"; // fallback only when there is no ViewController
}
```

When investigating "scripts were generated into a strange directory", first confirm whether the GameObject has a `ViewController` component, rather than inspecting the fallback logic. In most cases the fallback branch is never reached.

### Rule: nested ViewControllers must have their directories set explicitly

ViewControllers can nest (parent and child each carry a ViewController). A child ViewController's default generation directory differs from its parent's, so you must change it manually to the same directory. Otherwise the child's references are generated elsewhere, and `Weapon.xxx` in the parent code cannot access the child's fields (Doc.md:5297-5344).

```csharp
[ViewControllerChild]
public abstract class PowerUp : ViewController
{
    // Inherit ViewController and add ViewControllerChildAttribute to define a custom common base class
    // If the base class is abstract with abstract methods/properties, they are implemented automatically on first generation
}
```

### Rule: cross-hierarchy references use OtherBinds, not Bind

`ViewController` + `Bind` supports only parent-child references. To reference other GameObjects in the scene or Assets resources, click "添加 Other Binds" ("Add Other Binds") in the ViewController panel, drag the target into the empty area, and then generate (Doc.md:5455-5493). Both mechanisms produce fields that land in the same Designer file without conflict.

## UIKit: panel management workflow

The design intent of the recommended UIKit workflow is that each panel only displays data and listens for input, panels are independent of one another, and each can be tested on its own (Doc.md:7350). The companion requirement is **one test scene per panel**, run individually with `UIPanelTester` (editor-only) (Doc.md:7358, 7436-7443).

### Rule: UI prefabs must live under Assets/Art/UIPrefab by default, unless configuration is changed

`Assets/Art` is only a recommended location for assets, not mandatory. However, the specific path `Assets/Art/UIPrefab` **is read during code generation**, so by default it must be followed (Doc.md:7384-7386):

> But UI panel prefabs must be placed under the Assets/Art/UIPrefab directory, because this part is needed during code generation.

It can be changed in the UIKit settings of the QFramework panel (`Ctrl+E`), backed by `UIKitSettingData` (`Toolkits/UIKit/Editor/UIKitSetttingData.cs`):

| Field | Default |
|---|---|
| `Namespace` | `QFramework.Example` |
| `UIScriptDir` | `/Scripts/UI` |
| `UIPrefabDir` | `/Art/UIPrefab` |

The configuration is persisted in `Assets/QFrameworkData/ProjectConfig/ProjectConfig.json` (`UIKitSetttingData.cs:22-24`), not hard-coded. Deleting that json resets the settings to defaults.

### Pitfall: Apply must select UIBasicPanel, never UIRoot

After attaching `Bind` to child controls, run "Apply" to write the binding information back into the prefab. The official documentation stresses (Doc.md:7463):

> Be careful here: you must select UIBasicPanel before applying. Never select UIRoot.

Selecting UIRoot raises no error on Apply, but the bindings do not take effect. Later, when code is generated, every Bind field comes out empty, and there is no compile error to warn you. This is very easy to miss. When a "Bind is not working" problem appears, the first thing to check is which node was selected when you applied.

The same pitfall applies to the UIElement Apply step: Apply targets the parent UIPanel that owns the UIElement (such as `UIBasicPanel`), not the UIElement itself (Doc.md:7908-7929).

### UIKit.OpenPanel / ClosePanel API

Source signature (`UIKit.cs:51`):

```csharp
public static T OpenPanel<T>(PanelOpenType panelOpenType, UILevel canvasLevel = UILevel.Common,
    IUIData uiData = null,
    string assetBundleName = null,
    string prefabName = null) where T : UIPanel
```

Common usage:

```csharp
UIKit.OpenPanel<UIBasicPanel>();                          // default level, no initial data
UIKit.OpenPanel<UIBasicPanel>(UILevel.Forward);            // specify the level
UIKit.OpenPanel<UIBasicPanel>(new UIHomePanelData { Coin = 10 }); // pass initial data
UIKit.OpenPanel<UIBasicPanel>(prefabName: "UIBasicPanel");  // when the prefab name differs from the panel name
UIKit.ClosePanel<UIBasicPanel>();
this.CloseSelf(); // a panel closes itself from inside; this inherits from UIPanel
```

On WebGL, AssetBundles support only asynchronous loading, so UIKit provides asynchronous entry points (Doc.md:7703-7711):

```csharp
StartCoroutine(UIKit.OpenPanelAsync<UIHomePanel>());
// or
UIKit.OpenPanelAsync<UIHomePanel>().ToAction().Start(this);
```

### UIPanel lifecycle

`OnInit -> OnOpen -> OnShow -> OnHide -> OnClose` (Doc.md:7775-7793). `OnInit` is called once only when UIKit has no cached panel for it. `OnOpen` is called on every `OpenPanel`. `OnClose` is equivalent to `OnDestroy`. Most logic needs only `OnInit` and `OnClose`; `OnOpen` is used occasionally.

### UIElement: grouping Binds

When a panel structure is complex, it is unwieldy for a single UIPanel to manage dozens of Binds directly. In that case, change the Bind's marker type to `Element`, fill in the generated class name, and Apply to the parent UIPanel. This generates a separate class that manages that group of controls (Doc.md:7908-8018). The generated directory gets a subfolder named after the parent panel.

### Stack.Push / Back, and opening multiple of the same type

```csharp
UIKit.Stack.Push(this);           // this is the current Panel
UIKit.Stack.Push<UIHomePanel>();  // UIHomePanel must already be open, otherwise an error is raised
this.Back();                      // pop the top of the stack
```

To open several instances of the same panel type, use `PanelOpenType.Multiple` (Doc.md:8030-8036):

```csharp
UIKit.OpenPanel<UIMultiPanel>(new UIMultiPanelData(), PanelOpenType.Multiple);
```

### Custom panel loading

By default, `UIKit.Config.PanelLoaderPool` is set to use ResKit by `UIKitWithResKitInit` (`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`). To switch to another loading method, such as Resources, inherit `AbstractPanelLoaderPool`, implement `IPanelLoader`, and comment out the framework's default code that registers the ResKit loader. Otherwise the two conflict (Doc.md:8042-8140).

## See also

- For BindableList / BindableDictionary binding queries, see [BINDABLE_QUERY.md](./BINDABLE_QUERY.md).
