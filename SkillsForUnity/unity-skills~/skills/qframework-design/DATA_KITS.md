# FSMKit, TableKit, PoolKit & GridKit

Sub-doc of [qframework-design](./SKILL.md). Four small, independent data-structure kits: a runtime state machine, an indexed table for join-style queries, two object pools, and 2D grid containers.

## FSMKit：纯运行时内存状态机

`FSM<T>`（`Toolkits/_CoreKit/FSMKit/IState.cs:291`）是一个泛型状态机，`T` 是状态枚举类型，不持久化、不跨场景保存，纯内存对象，跟着宿主 MonoBehaviour 的生命周期走。

### 链式模式：状态少、迭代快时用

```csharp
public FSM<States> FSM = new FSM<States>();

void Start()
{
    FSM.State(States.A)
        .OnCondition(() => FSM.CurrentStateId == States.B)
        .OnEnter(() => Debug.Log("Enter A"))
        .OnUpdate(() => { /* ... */ })
        .OnGUI(() => { if (GUILayout.Button("To B")) FSM.ChangeState(States.B); })
        .OnExit(() => Debug.Log("Exit A"));

    FSM.StartState(States.A);
}

void Update()      => FSM.Update();
void FixedUpdate() => FSM.FixedUpdate();
void OnGUI()       => FSM.OnGUI();
void OnDestroy()   => FSM.Clear(); // 状态机不会自己清理，宿主销毁时必须显式 Clear
```

### 类模式：状态多、逻辑量大时用

```csharp
public class StateA : AbstractState<States, IStateClassExample>
{
    public StateA(FSM<States> fsm, IStateClassExample target) : base(fsm, target) { }
    protected override bool OnCondition() => mFSM.CurrentStateId == States.B;
    public override void OnGUI() { if (GUILayout.Button("To B")) mFSM.ChangeState(States.B); }
}

FSM.AddState(States.A, new StateA(FSM, this));
FSM.AddState(States.B, new StateB(FSM, this));
// 两种模式可以在同一个 FSM 实例里混用：某些状态用 AddState，某些状态用 FSM.State(...) 链式声明
FSM.StartState(States.A);
```

链式适合快速开发或状态少的阶段；状态数量或每个状态的代码量变大后切到类模式，两者共享同一个 `FSM<T>` 实例，不需要整体重写（Doc.md:4366-4372, 4443-4448）。

## TableKit：为联合查询而生

`List`/`Dictionary` 只支持单一维度的查询，做联合查询（比如"同时按 Level 和 Age 过滤"）性能会退化成线性扫描。`Table<T>`（`Toolkits/_CoreKit/TableKit/Script/TableKit.cs`）通过多个 `TableIndex<TKey, T>` 维护同一份数据的多套索引，兼顾查询灵活性和性能（Doc.md:4486-4490, 4566）：

```csharp
public class School : Table<Student>
{
    public TableIndex<int, Student> AgeIndex = new TableIndex<int, Student>(s => s.Age);
    public TableIndex<int, Student> LevelIndex = new TableIndex<int, Student>(s => s.Level);

    protected override void OnAdd(Student item) { AgeIndex.Add(item); LevelIndex.Add(item); }
    protected override void OnRemove(Student item) { AgeIndex.Remove(item); LevelIndex.Remove(item); }
    protected override void OnClear() { AgeIndex.Clear(); LevelIndex.Clear(); }
}

var school = new School();
school.Add(new Student { Age = 1, Level = 2, Name = "liangxie" });
// 先用索引缩小范围，再用 LINQ 精筛，比对整表做 Where 快
foreach (var s in school.LevelIndex.Get(2).Where(s => s.Age < 3))
{
    Debug.Log($"{s.Age}:{s.Level}:{s.Name}");
}
```

TableKit 属于框架基础设施而非面向业务的顶层 API：ResKit 和 UIKit 内部的数据管理都是靠它支撑的（Doc.md:4568，对应 `ResTable.cs` / `UIPanelTable.cs`）。自己业务层一般不需要直接用 `Table<T>`，除非确实有多维度联合查询的需求。

## PoolKit：两种对象池，语义不同

### SimpleObjectPool：面向业务，无回收上限检测

```csharp
var pool = new SimpleObjectPool<Fish>(() => new Fish(), initCount: 50);
var fish = pool.Allocate();  // CurCount: 50 -> 49
pool.Recycle(fish);          // CurCount: 49 -> 50
```

`SimpleObjectPool<T>.Recycle()`（`SimpleObjectPool.cs:71-78`）无条件把对象压回栈，不检查是否已经在池里、不检查上限——重复 `Recycle` 同一个对象两次会让它在池里出现两份引用。适合"我自己保证不会重复回收"的轻量场景。

### SafeObjectPool：单例全局池，带上限和重复回收保护

```csharp
class Bullet : IPoolable, IPoolType
{
    public bool IsRecycled { get; set; }
    public void OnRecycled() { }
    public static Bullet Allocate() => SafeObjectPool<Bullet>.Instance.Allocate();
    public void Recycle2Cache() => SafeObjectPool<Bullet>.Instance.Recycle(this);
}

SafeObjectPool<Bullet>.Instance.Init(maxCount: 50, initCount: 25);
```

`SafeObjectPool<T>` 是 `ISingleton`（`SafeObjectPool.cs:66-84`），`T` 必须实现 `IPoolable`。`Recycle()` 会检查 `IsRecycled` 防止重复回收，也会检查 `MaxCacheCount`——超过上限时对象仍会触发 `OnRecycled()` 回调但不会真正入池（`SafeObjectPool.cs:155-176`）：

```csharp
public override bool Recycle(T t)
{
    if (t == null || t.IsRecycled) return false;         // 防重复回收
    if (mMaxCount > 0 && mCacheStack.Count >= mMaxCount)
    {
        t.OnRecycled();  // 回调仍然触发
        return false;    // 但不入池，交给 GC
    }
    t.IsRecycled = true;
    t.OnRecycled();
    mCacheStack.Push(t);
    return true;
}
```

选型：临时脚本内自用的小对象池用 `SimpleObjectPool`；跨脚本共享、需要控制内存上限的全局池（子弹、特效等高频创建销毁对象）用 `SafeObjectPool`。

## GridKit：二维格子

### EasyGrid：固定宽高

```csharp
var grid = new EasyGrid<string>(4, 4);
grid.Fill("Empty");
grid[2, 3] = "Hello";
grid.ForEach((x, y, content) => Debug.Log($"({x},{y}):{content}"));
```

适合消除类游戏、俄罗斯方块、棋类游戏、Tilemap 地块数据这类维度固定的场景（Doc.md:4941-4947），设计上参考了 GameMaker Studio 的 `ds_grid`。

### DynaGrid：支持负坐标、动态扩展

```csharp
var dynaGrid = new DynaGrid<MyData>();
dynaGrid[1, 1] = new MyData { Key = "Hero" };
dynaGrid[-1, -10] = new MyData { Key = "Enemy" }; // 负坐标直接可用，不需要预先声明边界
dynaGrid.ForEach((x, y, data) => Debug.Log($"{x} {y} {data.Key}"));
```

`DynaGrid<T>`（`Toolkits/_CoreKit/GridKit/DynaGrid.cs`）不需要预先指定宽高，写入任意坐标（包括负坐标）会按需扩展底层存储，适合边界不确定或需要向四个方向动态扩张的场景（比如以某个原点为中心向外生长的地图），`EasyGrid` 则要求宽高在构造时就固定（Doc.md:5067-5104）。

## 参见

- 响应式的 `BindableList<T>` / `BindableDictionary<TKey,TValue>` 不在本篇范围，见 [BINDABLE_QUERY.md](./BINDABLE_QUERY.md)。
