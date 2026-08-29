# LAYERS — 四层架构与分层规范

四层：表现层 `IController`、系统层 `ISystem`、数据层 `IModel`、工具层 `IUtility`，外加 `ICommand`/`IQuery<TResult>` 两个无状态操作对象。全部定义在 `QFramework.cs` 单文件里，接口通过组合多个 `ICan*` 能力接口来声明"这一层能做什么"。

## 1. 接口清单与能力组合

| 接口 | 定义位置 | 组合的能力接口 |
|------|---------|----------------|
| `IController` | `QFramework.cs:226` | `IBelongToArchitecture, ICanSendCommand, ICanGetSystem, ICanGetModel, ICanRegisterEvent, ICanSendQuery, ICanGetUtility` |
| `ISystem` | `QFramework.cs:235` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetModel, ICanGetUtility, ICanRegisterEvent, ICanSendEvent, ICanGetSystem, ICanInit` |
| `IModel` | `QFramework.cs:264` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetUtility, ICanSendEvent, ICanInit` |
| `IUtility` | `QFramework.cs:291` | 空接口，不组合任何能力——工具层被设计成"啥都干不了"，只提供基础设施/第三方库封装 |
| `ICommand` / `ICommand<TResult>` | `QFramework.cs:299 / 305` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetSystem, ICanGetModel, ICanGetUtility, ICanSendEvent, ICanSendCommand, ICanSendQuery` |
| `IQuery<TResult>` | `QFramework.cs:342` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetModel, ICanGetSystem, ICanSendQuery`——**只有泛型形式**，没有非泛型 `IQuery` |

能力接口本身是空标记接口（`ICanGetModel`/`ICanSendEvent`/…），真正的方法通过扩展方法挂上去，例如 `CanGetModelExtension.GetModel<T>()`（`QFramework.cs:380-384`）、`CanSendCommandExtension.SendCommand<T>()`（`QFramework.cs:423-433`）。这意味着**给某层新增一种能力，只需要在该层接口上追加一个 `ICan*`**，无需改任何抽象基类。

```csharp
// 放宽示例：让 IController 也能发事件（Doc.md:2793-2805）
public interface IController : IBelongToArchitecture, ICanSendCommand, ICanGetSystem, ICanGetModel,
    ICanRegisterEvent, ICanSendQuery,
    ICanSendEvent // 追加
{
}
```

## 2. 通用规则（理想状态，可按需放宽）

`Doc.md:2682-2820` 给出的规则：

1. `IController` 更改 `ISystem`/`IModel` 状态**必须**用 Command
2. `ISystem`/`IModel` 状态变更后通知 `IController` **必须**用 Event 或 BindableProperty
3. `IController` 可以直接获取 `ISystem`/`IModel` 对象做查询
4. `ICommand`/`IQuery` **不能有状态**（不持有跨调用的字段）
5. 上层可以直接获取下层对象；**下层不能获取上层对象**
6. 下层向上层通信用事件（Event/BindableProperty）
7. 上层向下层通信用方法调用（查询用方法调用，状态变更用 Command）；`IController` 的交互逻辑是特殊情况，只能用 Command

这套规则是"理想状态"，作者原话（`Doc.md:2791`）：**落实到实际项目很可能需要对以上规则做一些修改**，方式就是第 1 节展示的"给接口加 `ICan*`"。不要把这七条当成不可违反的教条——遇到项目里 IController 确实需要发事件的场景，直接扩展接口比绕远路更符合框架设计意图。

## 3. 共享数据判据：什么该放 Model

`Doc.md:4051-4077` 给出三类"需要共享的数据"，只有满足其一才放 `IModel`：

- **时间上共享**：需要持久化，应用关闭重开后数据还在
- **物理上共享**：跨界面/跨 MonoBehaviour/跨场景使用，内存里不会被单个界面的生命周期释放
- **配置表**：开发阶段配置好，运行时供查询和展示

不满足以上任意一条的数据留在 MonoBehaviour 局部就行。**反例（`Doc.md:4064-4066`）：敌人的生命值不放 Model**，交给敌人脚本自己管理；如果生命值需要存储，存储时才转换成对应的数据结构去序列化，Model 不需要持有这种运行时状态。同理，需要查询的"最大生命值"这类静态配置可以放 Model，也可以放 System，也可以就近放在敌人 prefab 上当配置——不是所有查询数据都要塞进 Model。

## 4. Command 与 Query：CQRS 分工

- `ICommand`（`QFramework.cs:299`）：负责数据的**增删改**，`void Execute()`
- `ICommand<TResult>`（`QFramework.cs:305`）：有返回值的命令，`TResult Execute()`
- `IQuery<TResult>`（`QFramework.cs:342`）：负责数据的**查**，`TResult Do()`；只用于组合查询/转换查询逻辑较重的场景，逻辑简单时直接在 Controller 表现逻辑里查询即可（`Doc.md:2666`，Query 是可选概念）
- 如果游戏要和服务器同步数据：拉取用 Query，增删改用 Command（`Doc.md:2672`）

`AbstractCommand`/`AbstractCommand<TResult>`/`AbstractQuery<T>` 分别在 `QFramework.cs:312-336` 和 `348-360`，都只是把 `Execute()`/`Do()` 转发到 `protected abstract OnExecute()/OnDo()`，本身不持有状态——继承时不要在子类里加可变字段绕开"无状态"规则。

## 5. Command 拦截

覆写 `Architecture<T>.ExecuteCommand`（无返回值重载在 `QFramework.cs:183-187`，有返回值重载在 `177-181`）即可在每次 `SendCommand` 前后插入逻辑：

```csharp
// Doc.md:3137-3186
protected override void ExecuteCommand(ICommand command)
{
    Debug.Log("Before " + command.GetType().Name + " Execute");
    base.ExecuteCommand(command);
    Debug.Log("After " + command.GetType().Name + " Execute");
}
```

用途：Command 日志、Command 中间件、撤销功能、用 Command 做自动化测试。默认实现只是 `command.SetArchitecture(this); command.Execute();`，没有任何拦截逻辑。

## 6. Register* 返回值变更（2026-08-12）——现行用法

`RegisterSystem<TSystem>/RegisterModel<TModel>/RegisterUtility<TUtility>` 现在**返回注册的实例本身**，签名与实现见 `QFramework.cs:43,45,47`（接口声明）与 `132-163`（`Architecture<T>` 实现，`return system;` / `return model;` / `return utility;`）。QFramework.cs 顶部注释明确写着 `Latest Update: 2026.8.12 ... return module instance after register`。

**官方 Doc.md 全部教程示例截至本次抓取仍是旧写法**——包括"用接口设计模块"一节（`Doc.md:2408-2422`）都还在丢弃返回值，例如 `this.RegisterModel<ICounterAppModel>(new CounterAppModel());` 不接返回值。这是文档滞后于源码，不代表当前推荐写法。现行写法应当捕获返回值：

```csharp
public class GameArchitecture : Architecture<GameArchitecture>
{
    protected override void Init()
    {
        // 捕获返回值：注册即拿到实例，省一次 GetUtility/GetModel 往返
        var storage = this.RegisterUtility<IStorage>(new Storage());
        var counterModel = this.RegisterModel<ICounterAppModel>(new CounterAppModel());

        counterModel.Count.SetValueWithoutEvent(storage.LoadInt(nameof(counterModel.Count)));
    }
}
```

`IStorage`/`ICounterAppModel` 沿用 `Doc.md:2336-2422` 的接口化示例类型；这里的差异只是**不丢弃返回值**，直接拿实例继续用。

## 7. Architecture<T> 生命周期

- `Architecture<T>.Interface` 首次访问时触发 `InitArchitecture()`（`QFramework.cs:79-113`）：先 `new T()` 并调用其 `Init()`（子类在这里做 Register），再跑 `OnRegisterPatch` 补丁钩子，然后**先初始化所有未初始化的 Model，再初始化所有未初始化的 System**（`98-109`）
- `Deinit()`（`QFramework.cs:117-124`）顺序相反：**先 Deinit 所有 System，再 Deinit 所有 Model**，最后清空容器并把静态单例置空
- `RegisterSystem/RegisterModel` 如果在架构已经 `mInited` 之后调用（运行时动态注册），会立即调用一次该实例的 `Init()`（`QFramework.cs:137-141,151-155`），不用等下一次全局初始化
- `RegisterUtility` 不参与 Init/Deinit 生命周期（`IUtility` 没有 `ICanInit`），注册即用

容器本身（`IOCContainer`，`private IOCContainer mContainer`，`QFramework.cs:130`）与事件系统（`TypeEventSystem`，`QFramework.cs:197`）的实现细节见 [EVENT_TOOLS.md](./EVENT_TOOLS.md)；不要和 Toolkits 的 `IOCKit` 混淆。
