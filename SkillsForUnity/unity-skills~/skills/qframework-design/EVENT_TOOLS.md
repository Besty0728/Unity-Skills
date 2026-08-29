# EVENT_TOOLS — TypeEventSystem / EasyEvent / IOCContainer vs IOCKit / 注销模式

`Doc.md:3198` 原文："QFramework 还提供三个可以脱离架构使用的工具 TypeEventSystem、EasyEvent、BindableProperty、IOCContainer"——原文写"三个"却列了四个名字，是教程笔误，不代表工具数量真是三个；这四个工具本身就是 `Architecture<T>` 内部拼出来的构件（BindableProperty 见 [BINDABLE_QUERY.md](./BINDABLE_QUERY.md)）。

## 1. TypeEventSystem

```csharp
public class TypeEventSystem
{
    private readonly EasyEvents mEvents = new EasyEvents();
    public static readonly TypeEventSystem Global = new TypeEventSystem();

    public void Send<T>() where T : new() => mEvents.GetEvent<EasyEvent<T>>()?.Trigger(new T());
    public void Send<T>(T e) => mEvents.GetEvent<EasyEvent<T>>()?.Trigger(e);
    public IUnRegister Register<T>(Action<T> onEvent) => mEvents.GetOrAddEvent<EasyEvent<T>>().Register(onEvent);
    public void UnRegister<T>(Action<T> onEvent) => mEvents.GetEvent<EasyEvent<T>>()?.UnRegister(onEvent);
}
```
`QFramework.cs:624-641`。内部按**泛型实参的类型**（不是事件值的运行时类型）在 `EasyEvents` 字典里查表。`Architecture<T>.SendEvent/RegisterEvent` 就是转发到私有的 `mTypeEventSystem` 实例（`QFramework.cs:197-205`），架构内的事件系统和 `TypeEventSystem.Global` 是两个独立实例，互不干扰。

**事件继承的关键细节**：`Register<IEventA>` 会在字典里建一条 `EasyEvent<IEventA>`；`Send<IEventA>(new EventB())` 能命中这条（`EventB : IEventA` 隐式转换）；但 `Send<EventB>()` 是查 `EasyEvent<EventB>` 这条**从未注册过的键**，`GetEvent` 返回 `default` 再 `?.Trigger` 短路，**静默无效、不报错**（`Doc.md:3288-3301`，"无效"标注在 3291）。踩坑点：调用方以为事件类型看运行时类型，实际上看的是调用 `Send<T>`/`Register<T>` 时写的那个 `T`。

**接口事件模式**依赖 `IOnEvent<T>` + `OnGlobalEventExtension`：
```csharp
public static class OnGlobalEventExtension
{
    public static IUnRegister RegisterEvent<T>(this IOnEvent<T> self) where T : struct =>
        TypeEventSystem.Global.Register<T>(self.OnEvent);
    public static void UnRegisterEvent<T>(this IOnEvent<T> self) where T : struct =>
        TypeEventSystem.Global.UnRegister<T>(self.OnEvent);
}
```
`QFramework.cs:208-220`。**这两个扩展方法要求 `T : struct`**——接口事件模式只对结构体事件生效，用 class 定义事件类型走这条路会直接编译不过。普通的 `Action<T>` 显式注册（`TypeEventSystem.Global.Register<T>(handler)`）没有这个限制，class/struct 事件都能用。

## 2. 手动注销 vs 自动注销

- **手动注销必须用具名方法**：`Register<EventA>(OnEventA)` 配 `UnRegister<EventA>(OnEventA)`（`Doc.md:3305-3338`）；如果注册时传的是匿名 lambda，没有引用可以拿来 `-=`，就没法手动注销
- **自动注销**：`.UnRegisterWhenGameObjectDestroyed(gameObject)` 挂 `UnRegisterOnDestroyTrigger`，`.UnRegisterWhenDisabled(gameObject)` 挂 `UnRegisterOnDisableTrigger`（二者都是 `GetOrAddComponent<T>` 幂等添加，`QFramework.cs:592-608`）
- **`.UnRegisterWhenCurrentSceneUnloaded()`**（`QFramework.cs:610-611`）不需要传任何 GameObject：内部是一个 `DontDestroyOnLoad` 且 `HideFlags.HideInHierarchy` 的单例 `UnRegisterCurrentSceneUnloadedTrigger`，订阅 `SceneManager.sceneUnloaded`，当前场景卸载时自动触发注销（`QFramework.cs:546-573`）
- **非 MonoBehaviour 类**用 `IUnRegisterList` + `AddToUnregisterList` + `UnRegisterAll()` 手动批量清理（`QFramework.cs:473-492`，示例 `Doc.md:3417-3434`）

小结（`Doc.md:3440-3461`）：事件定义建议用 `struct`——GC 更少；接口事件模式约束更强、能借 IDE 生成样板代码，但受限于 `struct`。

## 3. EasyEvent 家族

```csharp
public class EasyEvent : IEasyEvent           // 无参
public class EasyEvent<T> : IEasyEvent        // 1 个泛型
public class EasyEvent<T, K> : IEasyEvent      // 2 个泛型
public class EasyEvent<T, K, S> : IEasyEvent    // 3 个泛型，QFramework 支持的上限
```
`QFramework.cs:800-883`。全部通过 `IUnRegister Register(...)` 返回一个 `CustomUnRegister`，`Trigger(...)` 触发。`EasyEvents`（`QFramework.cs:885-913`）是按类型存 `IEasyEvent` 的字典，`TypeEventSystem` 内部就是包了一层 `EasyEvents`。

对比取舍（`Doc.md:3549-3560`）：

| | 自动注销 | 相对性能 | 参数可读性 | 典型场景 |
|---|---|---|---|---|
| C# 原生委托/事件 | 无 | 最高 | 有名字 | — |
| `EasyEvent` | 有 | 接近原生委托 | 无名字，需自己记参数顺序 | 通用系统内部（背包、对话），早期原型快速迭代 |
| `TypeEventSystem` | 有 | 略低于 EasyEvent | 靠事件类型自文档化 | 跨模块广播、协作项目、长期维护 |

`OrEvent`（`QFramework.cs:921-957`）可以把多个 `IEasyEvent`（包括 `BindableProperty`，因为它实现了 `IEasyEvent`）合并成"任意一个触发就触发"：`a.Or(b).Or(c).Register(...)`。

## 4. IOCContainer（核心） vs IOCKit（Toolkits）——两套完全不同的容器

### 核心 IOCContainer
```csharp
public class IOCContainer
{
    private Dictionary<Type, object> mInstances = new Dictionary<Type, object>();
    public void Register<T>(T instance) { /* 存在则覆盖，否则 Add */ }
    public T Get<T>() where T : class { /* 查不到返回 null，不抛异常 */ }
    public IEnumerable<T> GetInstancesByType<T>() => mInstances.Values.Where(i => typeof(T).IsInstanceOfType(i)).Cast<T>();
    public void Clear() => mInstances.Clear();
}
```
`QFramework.cs:647-684`。就是一个 `Dictionary<Type,object>` 的极薄封装：**只支持单例注册/获取**，没有工厂、没有对象池、没有反射注入。`Architecture<T>` 内部私有持有一个实例（`QFramework.cs:130`），`RegisterSystem/RegisterModel/RegisterUtility`/`GetSystem/GetModel/GetUtility` 全部委托给它。作者原话：这是"非常简易版本的控制反转容器"，对比 Zenject 这类"内置对象池和对象工厂"的容器故意做得更简单（`Doc.md:3641-3647`）。

### Toolkits IOCKit（`_CoreKit/IOCKit/IOCKit.cs`，不在 QFramework.cs 里）
接口 `IQFrameworkContainer`（`IOCKit.cs:33-136`）比核心 `IOCContainer` 重得多：

- `[Inject]` 特性（`IOCKit.cs:19-31`，`AttributeUsage(Field | Property)`，可选 `Name` 做命名注入）
- `Register<TSource,TTarget>(name)` / `RegisterInstance<TBase>(instance, name, injectNow)`：既支持类型映射（按需 `Activator.CreateInstance`），也支持直接注册实例，都可以带命名空间式的 `name` 做多实现区分
- `RegisterRelation<TFor,TBase,TConcrete>()` / `ResolveRelation<TFor,TBase>()`：**"在 TFor 这个语境下，把 TBase 解析成 TConcrete"**的关系映射，核心 IOCContainer 完全没有这个概念
- `Inject(object obj)`（`IOCKit.cs:239-263`）用反射扫成员（默认只扫 public，见 `MemberSearchModes`，`IOCKit.cs:143-150,220-233`），凡带 `[Inject]` 的 Field/Property 都用 `Resolve(memberType, name)` 塞值
- `CreateInstance(Type, args)`（`IOCKit.cs:315-358`）会挑**参数最多的公开构造函数**，递归对每个参数类型调用 `Resolve`，构造完再 `Inject`——这是核心 IOCContainer 完全不具备的"自动装配"能力

**结论：不要把 `[Inject]`/`IQFrameworkContainer` 相关的用法套到 `Architecture<T>` 的核心容器上，也不要以为 `RegisterSystem/RegisterModel/RegisterUtility` 背后支持特性注入——那是 IOCKit 的能力，核心架构只做 `Dictionary<Type,object>` 级别的单例登记。** 两者除了都叫"IOC"、都用 `Register`/`Resolve`（核心叫 `Get`）这类相似动词外，没有任何代码共享关系。
