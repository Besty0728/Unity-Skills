# CodeGenKit & UIKit

Sub-doc of [qframework-design](./SKILL.md). Covers code generation (ViewController + Bind) and the UI panel/element workflow built on top of it.

## CodeGenKit：两阶段生成

生成代码不是一次性写完的，是「写文件 → 触发编译 → 编译完成后回填组件引用」两阶段过程。`CodeGenKitPipeline.cs` 里点击生成按钮先写 `.Designer.cs` 文件，随后 Unity 编译；编译完成由 `[DidReloadScripts]` 回调触发，把生成的字段用 `SerializedProperty` 挂到对应 GameObject 上（`CodeGenKitPipeline.cs:179-183`）。

```csharp
[DidReloadScripts]
static void Compile()
{
    Default.OnCompile();
}
```

这意味着：生成代码后如果编译报错，组件绑定不会完成——先修编译错误，再看绑定是否生效，别在编译失败时怀疑生成器坏了。

### 规则：Designer 文件每次覆盖，逻辑文件只生成一次

`Player.Designer.cs` 每次点「生成代码」都会被整个重写；`Player.cs`（逻辑文件）只在第一次生成时创建，之后永远不会被覆盖（Doc.md:5176-5235）。这就是为什么大家可以放心在 `Player.cs` 里写业务代码——它不会被生成流程吃掉。

```csharp
// Player.Designer.cs —— 每次生成都重写
namespace QFramework.Example
{
    public partial class Player
    {
        public Transform Weapon;
        public Transform GroundCheck; // 新增字段追加在这里
    }
}
```

### 规则：命名空间改了要手动改逻辑文件

生成的 Designer 文件顶部固定带这段提示注释（Doc.md:5186-5187,5219-5220）：

```csharp
// 1.请在菜单 编辑器扩展/Namespace Settings 里设置命名空间
// 2.命名空间更改后，生成代码之后，需要把逻辑代码文件（非 Designer）的命名空间手动更改
```

CodeGenKit 只会把新命名空间写进 `.Designer.cs`；已经生成过的 `.cs` 逻辑文件命名空间不会跟着联动改写，两个文件就会命名空间不一致导致编译错误。改完命名空间后，记得手动同步逻辑文件。

### 规则：ScriptsFolder 为空不会 fallback 到 Assets/Scripts

`CodeGenKitPipeline.cs:126-170` 里，只有当 GameObject **没有** `ViewController` 组件时才会把 `ScriptsFolder` 强制置为 `"Assets/Scripts"`；只要挂了 `ViewController`，即使它的 `ScriptsFolder` 字段是空字符串，也会原样写入 `serializedObject`，不会做默认值兜底：

```csharp
var codeGenerateInfo = gameObject.GetComponent<ViewController>();
if (codeGenerateInfo)
{
    serializedObject.FindProperty("ScriptsFolder").stringValue = codeGenerateInfo.ScriptsFolder; // 空串也原样写入
    // ...
}
else
{
    serializedObject.FindProperty("ScriptsFolder").stringValue = "Assets/Scripts"; // 仅无 ViewController 时兜底
}
```

排查“脚本生成到了奇怪目录”的问题时，先确认 GameObject 上有没有 `ViewController` 组件，而不是去查 fallback 逻辑——大多数时候压根不会走到 fallback 分支。

### 规则：ViewController 嵌套要重新指定目录

ViewController 之间可以嵌套（父子结构各自挂一个 ViewController），子级 ViewController 默认生成目录和父级不是同一个，需要手动改成同一目录，否则子级引用生成在别处，父级代码里 `Weapon.xxx` 访问不到子级字段（Doc.md:5297-5344）。

```csharp
[ViewControllerChild]
public abstract class PowerUp : ViewController
{
    // 继承 ViewController 并加上 ViewControllerChildAttribute 即可自定义公共父类
    // 若父类是抽象类且有抽象方法/属性，首次生成会自动实现
}
```

### 规则：跨层级引用用 OtherBinds，不是 Bind

`ViewController` + `Bind` 只支持父子结构下的引用。要引用场景里其他 GameObject 或 Assets 资源，要在 ViewController 面板点「添加 Other Binds」，把目标拖进空白区域再生成（Doc.md:5455-5493）。这两套机制生成的字段最终都落进同一个 Designer 文件，互不冲突。

## UIKit：界面管理工作流

UIKit 推荐工作流的设计初衷是让每个界面只负责展示数据和监听输入，界面之间互相独立、可独立测试（Doc.md:7350）；配套要求是**每个界面创建一个对应的测试场景**，用 `UIPanelTester`（编辑器专用）单独运行（Doc.md:7358, 7436-7443）。

### 规则：UI prefab 默认必须放 Assets/Art/UIPrefab，除非改配置

`Assets/Art` 只是资源存放的推荐位置，非强制；但 `Assets/Art/UIPrefab` 这个具体路径在**代码生成时会被读取**，默认情况下必须遵守（Doc.md:7384-7386）：

> 但是 UI 界面的 prefab 必须放在 Assets/Art/UIPrefab 目录下，因为这个部分在代码生成的时候需要。

可以改，改的地方是 QFramework 面板（`Ctrl+E`）里的 UIKit 设置，对应 `UIKitSettingData`（`Toolkits/UIKit/Editor/UIKitSetttingData.cs`）：

| 字段 | 默认值 |
|---|---|
| `Namespace` | `QFramework.Example` |
| `UIScriptDir` | `/Scripts/UI` |
| `UIPrefabDir` | `/Art/UIPrefab` |

配置持久化在 `Assets/QFrameworkData/ProjectConfig/ProjectConfig.json`（`UIKitSetttingData.cs:22-24`），不是写死在代码里，删掉这个 json 会让设置回到默认值。

### 硬坑：Apply 一定要选 UIBasicPanel，别选成 UIRoot

给子控件挂完 `Bind` 之后要执行「Apply」把绑定信息写回 prefab；官方文档原话强调（Doc.md:7463）：

> 这里要注意，一定要选定 UIBasicPanel 再进行 Apply，千万别选成 UIRoot 了。

选错成 UIRoot，Apply 不会报错但绑定不会生效——之后生成代码时 Bind 字段全部落空，且没有编译错误提示，非常隐蔽。排查“Bind 没生效”时第一件事就是回头检查 Apply 时选中的是哪个节点。

同样的坑也出现在 UIElement 的 Apply 步骤：Apply 的对象是拥有该 UIElement 的父 UIPanel（如 `UIBasicPanel`），不是 UIElement 自己（Doc.md:7908-7929）。

### UIKit.OpenPanel / ClosePanel API

源码签名（`UIKit.cs:51`）：

```csharp
public static T OpenPanel<T>(PanelOpenType panelOpenType, UILevel canvasLevel = UILevel.Common,
    IUIData uiData = null,
    string assetBundleName = null,
    string prefabName = null) where T : UIPanel
```

常见用法：

```csharp
UIKit.OpenPanel<UIBasicPanel>();                          // 默认层级、无初始数据
UIKit.OpenPanel<UIBasicPanel>(UILevel.Forward);            // 指定层级
UIKit.OpenPanel<UIBasicPanel>(new UIHomePanelData { Coin = 10 }); // 传初始数据
UIKit.OpenPanel<UIBasicPanel>(prefabName: "UIBasicPanel");  // prefab 名与界面名不同时
UIKit.ClosePanel<UIBasicPanel>();
this.CloseSelf(); // 界面内部关闭自己，this 继承自 UIPanel
```

WebGL 平台 AssetBundle 只支持异步加载，UIKit 相应提供异步入口（Doc.md:7703-7711）：

```csharp
StartCoroutine(UIKit.OpenPanelAsync<UIHomePanel>());
// 或
UIKit.OpenPanelAsync<UIHomePanel>().ToAction().Start(this);
```

### UIPanel 生命周期

`OnInit → OnOpen → OnShow → OnHide → OnClose`（Doc.md:7775-7793）。`OnInit` 只在 UIKit 内没有对应缓存界面时调用一次；`OnOpen` 每次 `OpenPanel` 都调用；`OnClose` 相当于 `OnDestroy`。绝大多数逻辑只需要 `OnInit` 和 `OnClose` 两个周期，`OnOpen` 偶尔用。

### UIElement：给 Bind 打组

界面结构复杂时不适合让一个 UIPanel 直接管理几十个 Bind，这时把 Bind 的标记类型改成 `Element` 并填写生成类名，Apply 到父 UIPanel 后生成一个独立类管理这一组控件（Doc.md:7908-8018）。生成目录会以父 Panel 名字建一个子文件夹。

### Stack.Push / Back，同类型多开

```csharp
UIKit.Stack.Push(this);           // this 是当前 Panel
UIKit.Stack.Push<UIHomePanel>();  // 需确保 UIHomePanel 已经处于打开状态，否则报错
this.Back();                      // 弹出栈顶
```

同一类型界面要打开多份，用 `PanelOpenType.Multiple`（Doc.md:8030-8036）：

```csharp
UIKit.OpenPanel<UIMultiPanel>(new UIMultiPanelData(), PanelOpenType.Multiple);
```

### 自定义界面加载

默认 `UIKit.Config.PanelLoaderPool` 由 `UIKitWithResKitInit`（`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`）设置为走 ResKit；要换成别的加载方式（如 Resources），继承 `AbstractPanelLoaderPool` + 实现 `IPanelLoader`，并注释掉框架里默认注册 ResKit 加载器的那段代码，否则两者会冲突（Doc.md:8042-8140）。

## 参见

- BindableList/BindableDictionary 的绑定查询用法见 [BINDABLE_QUERY.md](./BINDABLE_QUERY.md)。
