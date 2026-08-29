---
name: unity-qframework-design
description: Source-anchored design rules for QFramework v1.0.257 covering the four-layer architecture (Controller/System/Model/Utility), Command/Query/Event/BindableProperty core tools, and the CoreKit-family toolkits (UIKit/ResKit/ActionKit/PackageKit), distilled from QFramework.cs source and the official Doc.md tutorial with every rule citing a file/line anchor to guard against stale-memory hallucination.
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Writing or reviewing QFramework architecture code (Controller/System/Model/Utility)
- Registering Systems/Models/Utilities, sending Commands/Queries/Events
- Binding BindableProperty / BindableList / BindableDictionary in views
- Choosing between the core IOCContainer and the Toolkits IOCKit, or between architecture events and EasyEvent
- 编写或审查 QFramework 架构代码、四层职责划分、Command/Query/Event/BindableProperty 使用、核心容器与 Toolkits 容器选型

# QFramework - Design Rules（v1.0.257）

Advisory 模块。全部规则提炼自 QFramework 官方源码与教程：
- **QFramework.cs**（核心架构单文件源码，仓库根目录 `QFramework.cs`，内部注释标注 `Latest Update: 2026.8.12 return module instance after register`）
- **Doc.md**（官方教程全文，8820 行，仓库根目录 `Doc.md`）
- **QFramework API.md**（核心层签名索引；Toolkits 无覆盖，且截至本次抓取仍未同步 2026-08-12 的返回值变更）

每条规则都标注具体文件/行号（行号对应上游 [liangxiegame/QFramework](https://github.com/liangxiegame/QFramework) 主仓库 v1.0.257 快照；Toolkits 源码位于 `QFramework.Unity2018+/Assets/QFramework/`），遇到分歧以 QFramework.cs 源码为准，Doc.md/API.md 仅供交叉印证——教程本身可能滞后于源码。正文中文，API 名/代码保持英文。

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

## When to Load This Module

生成或审查以下任意一项之前先加载：

- `Architecture<T>` 子类的 `Init()` 启动逻辑、`RegisterSystem/RegisterModel/RegisterUtility` 调用
- `IController / ISystem / IModel / IUtility` 接口实现，或给这些接口增删 `ICan*` 能力接口
- `SendCommand / SendQuery / SendEvent / RegisterEvent` 调用，`ICommand / ICommand<TResult> / IQuery<TResult>` 实现
- `BindableProperty<T> / BindableList<T> / BindableDictionary<TKey,TValue>` 的声明、Register/RegisterWithInitValue/Comparer 使用
- `TypeEventSystem` / `EasyEvent` 系列的注册与手动注销
- 覆写 `Architecture<T>.ExecuteCommand` 做 Command 拦截（日志/中间件/撤销/自动化测试）
- 判断某个数据该放 Model 还是留在 MonoBehaviour 局部
- 在 QFramework.cs 自带的 `IOCContainer` 与 Toolkits `CoreKit.IOCKit`（`IQFrameworkContainer` + `[Inject]`）之间选型
- Toolkits 安装形态确认（unitypackage 导入 `Assets/`，无 UPM）、版本号核对

## Critical Rule Summary

| # | Rule | Source anchor |
|---|------|---------------|
| 1 | `RegisterSystem<T>/RegisterModel<T>/RegisterUtility<T>` **返回注册的实例本身**（不再是 void），自 2026-08-12 起生效；`IArchitecture` 接口已声明 `TSystem/TModel/TUtility` 返回类型。官方 Doc.md 全部教程示例（含"用接口设计模块"一节 2408-2422 行）仍是丢弃返回值的旧写法，**是过时示例，不代表当前推荐用法** | `QFramework.cs:43,45,47,132-163` |
| 2 | 四层接口：`IController`(226) `ISystem`(235) `IModel`(264) `IUtility`(291)；`ICommand`(299) / `ICommand<TResult>`(305)；`IQuery<TResult>` 只有泛型形式，**没有非泛型 `IQuery`**(342) | `QFramework.cs:226,235,264,291,299,305,342` |
| 3 | 分层通用规则：IController 改 System/Model 状态必须走 Command；System/Model 通知上层必须用 Event 或 BindableProperty；上层可直接获取下层，**下层不能获取上层**；Command/Query 不能有状态。规则是"理想状态"，作者原话允许按需放宽（如给 IController 追加 `ICanSendEvent`） | `Doc.md:2682-2820`（放宽原话 2791，改接口示例 2793-2805） |
| 4 | 判断数据是否该放 Model：时间上共享（跨启动持久化）、物理上共享（跨界面/跨场景常驻内存）、配置表三类才放 Model；反例——**敌人生命不放 Model**，交给敌人脚本自己管理 | `Doc.md:4051-4077`（反例 4064-4066） |
| 5 | `BindableProperty<T>.Value` setter 靠 `Comparer` 判等后再触发事件；`ComparerAutoRegister`（`[RuntimeInitializeOnLoadMethod]`）已为 int/float/string/Vector2-4/Color/Quaternion 等类型把默认 `Equals` 换成 `==`。`Register` 只订阅后续变化，`RegisterWithInitValue` 会先用当前值回调一次 | `QFramework.cs:711,762-789,740-749` |
| 6 | `BindableList<T>` / `BindableDictionary<TKey,TValue>` 是 2024-09-18/19 才加的 Toolkits 附加工具（`_CoreKit/BindableKit/`，不在 QFramework.cs 单文件里），边缘工具而非核心概念；作者原话承认**不知道 BindableDictionary 能用在什么场景**——按需评估，不要当成默认选型。`BindableList` 索引器赋值只触发 `OnReplace`，`Move` 只触发 `OnMove`（都不会连带 `OnAdd`/`OnRemove`） | `Doc.md:8438-8592,8593-8712`（原话 8595）；`BindableList.cs:38-115`,`BindableDictionary.cs:17-44` |
| 7 | 事件工具三选一：`TypeEventSystem`（跨对象、支持接口继承事件、适合通用系统）／`EasyEvent`（脱离架构、更轻量、无字段名）／架构内建 `SendEvent`（基于 TypeEventSystem 实例）。手动注销必须用**具名方法**（匿名委托无法 `-=`）；`UnRegisterWhenCurrentSceneUnloaded()` 无需传 GameObject，换场景即自动清空；事件类型建议用 `struct` 减少 GC | `Doc.md:3305-3461`（小结 3440-3461），`QFramework.cs:610-611,624-641` |
| 8 | Command 拦截：覆写 `Architecture<T>.ExecuteCommand(ICommand)`（前后各插一段逻辑）可做日志/中间件/撤销/自动化测试，官方源码默认实现只是直接 `command.Execute()` | `Doc.md:3137-3186`，`QFramework.cs:183-187` |
| 9 | **两套容器不要混淆**：核心 `IOCContainer`（`Dictionary<Type,object>`，仅单例注册/获取，`Architecture<T>` 内部私有持有一份，无反射）；Toolkits `CoreKit.IOCKit`（`IQFrameworkContainer`/`QFrameworkContainer`）是完全独立的反射式依赖注入实现——支持类型映射、命名注册、关系映射、按 `[Inject]` 特性做字段/属性注入、按参数最多的公开构造函数自动解析依赖——不在 QFramework.cs 源文件里 | 核心：`QFramework.cs:130,647-684`；Toolkits：`IOCKit.cs:19-31,33-136,141,239-263,315-358`（`_CoreKit/IOCKit/IOCKit.cs`） |
| 10 | Toolkits 安装形态：`.unitypackage` 导入 `Assets/`，**没有 UPM 包**；已装版本看 PackageKit 编辑器面板或安装目录下的 `PackageVersion.json`，GitHub/Gitee Release tag 会滞后于 PackageKit 实时源 | `Doc.md:4178-4182`；`PackageData.cs:286`（`PackageVersion.json` 写入路径，Toolkits 源码） |

## Sub-doc Routing

| Sub-doc | When to read |
|---------|--------------|
| [LAYERS.md](./LAYERS.md) | 四层接口与职责、通用规则与放宽方式、共享数据判据、Command 拦截、`Architecture<T>` 生命周期（`InitArchitecture`/`Deinit`） |
| [BINDABLE_QUERY.md](./BINDABLE_QUERY.md) | `BindableProperty`（含 Comparer/RegisterWithInitValue）、`BindableList`/`BindableDictionary`、`Command` vs `Query` 的 CQRS 分工 |
| [EVENT_TOOLS.md](./EVENT_TOOLS.md) | `TypeEventSystem`、`EasyEvent` 家族、`IOCContainer`（核心）vs `IOCKit`（Toolkits）、注销模式（`UnRegisterWhenGameObjectDestroyed`/`UnRegisterWhenCurrentSceneUnloaded`/`IUnRegisterList`） |
| [CODEGEN_UIKIT.md](./CODEGEN_UIKIT.md) | CodeGenKit 两阶段代码生成（Designer 覆盖规则、命名空间迁移、ScriptsFolder 不 fallback、ViewController 嵌套、OtherBinds）与 UIKit 界面工作流（面板开关/生命周期/Apply 选错 UIRoot 的硬坑/UIKitSettingData 配置） |
| [RESKIT.md](./RESKIT.md) | ResKit 资源方案：AssetBundle 标记粒度、场景独占 AB、模拟/非模拟模式、ResLoader 引用计数语义、构建输出目录、AB 跨包 Prefab 依赖的 Unity 官方 bug |
| [ACTIONKIT.md](./ACTIONKIT.md) | ActionKit 链式动作序列（Delay/Sequence/Parallel/Repeat/Condition、全局生命周期、TimeScale 与场景切换）、SingletonKit 六种单例选型、AudioKit 三通道与**纯编辑态访问 Settings 会 NPE 的陷阱**、ScreenTransition |
| [DATA_KITS.md](./DATA_KITS.md) | FSMKit（链式 vs 类模式）、TableKit（联合查询与内部基础设施定位）、PoolKit（两种池语义差异）、GridKit/DynaGrid |

## Routing to Other Modules

- REST 端点/操作层面的 QFramework 技能调用 → 加载并行编写的 [qframework](../qframework/SKILL.md) REST 模块（本模块只覆盖设计规则，不覆盖 REST 参数）
- 与 UniTask/协程的桥接（ActionKit 序列 vs async）→ [unitask-design](../unitask-design/SKILL.md) / [async](../async/SKILL.md)
- 整体架构分层评审、状态管理选型 → [architecture](../architecture/SKILL.md) / [patterns](../patterns/SKILL.md)
- 事件/BindableProperty 高频触发场景的性能评审 → [performance](../performance/SKILL.md)
- ResKit 底层如走 Addressables 的资源生命周期 → [addressables-design](../addressables-design/SKILL.md)
- Asmdef 布局（QFramework.cs 与 Toolkits 分别装配）→ [asmdef](../asmdef/SKILL.md)

## Version Scope

锚定 **v1.0.257**（QFramework.cs 内部注释最后更新 2026-08-12，本模块 2026-08 抓取）。

- QFramework.cs 是单文件核心架构，版本号不随 Toolkits 独立发布，跟随 GitHub/Gitee 主仓库 tag。
- Toolkits（CoreKit/UIKit/ResKit/ActionKit/AudioKit/PackageKit…）版本以 PackageKit 面板/`PackageVersion.json` 为准，可能领先于本模块引用的 unitypackage 快照。
- 关键分水岭：`RegisterSystem/RegisterModel/RegisterUtility` 返回值语义在 2026-08-12 从 void 改为返回实例——凡是引用早于此版本的第三方教程或历史代码，示例可能与当前源码签名不符，以 QFramework.cs 为准。

当有疑问时，去读源码引用的行号，而不是凭记忆或凭教程旧例。

## Known Gaps

- **LocaleKit 官方教程零覆盖**：Doc.md 全篇只在工具清单里一句话提到 LocaleKit（"本地化&多语言工具集"），没有任何用法示例或 API 说明。从源码看它当前主要是编辑器内 CN/EN 显示开关（`LocaleKitEditor.IsCN`，`EditorPrefs` 键 `EDITOR_CN`）加一份语言定义 ScriptableObject（`LanguageDefineConfig`），**并不是面向游戏运行时文本的完整多语言方案**，与其自我描述有落差。需要运行时本地化时不要假设 LocaleKit 能直接胜任，先读源码确认。
- **FluentAPI 方法清单未逐条收录**：`_CoreKit/FluentAPI/` 覆盖 GameObject/Transform/Camera/Color/Graphic/Vector/RectTransform 及 C# 基础类型的大量链式扩展，数量过大未在本模块展开；用到时按目标类型去对应源文件查。
