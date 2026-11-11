# FSMKit, TableKit, PoolKit & GridKit

Sub-doc of [qframework-design](./MODULE.md). Four small, independent data-structure kits: a runtime state machine, an indexed table for join-style queries, two object pools, and 2D grid containers.

## FSMKit: pure in-memory runtime state machine

`FSM<T>` (`Toolkits/_CoreKit/FSMKit/IState.cs:291`) is a generic state machine where `T` is the state enum type. It is not persisted and not saved across scenes. It is a pure in-memory object that follows the lifecycle of its host MonoBehaviour.

### Chained mode: use when there are few states and fast iteration

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
void OnDestroy()   => FSM.Clear(); // the state machine does not clean itself up; the host must call Clear when destroyed
```

### Class mode: use when there are many states or lots of logic per state

```csharp
public class StateA : AbstractState<States, IStateClassExample>
{
    public StateA(FSM<States> fsm, IStateClassExample target) : base(fsm, target) { }
    protected override bool OnCondition() => mFSM.CurrentStateId == States.B;
    public override void OnGUI() { if (GUILayout.Button("To B")) mFSM.ChangeState(States.B); }
}

FSM.AddState(States.A, new StateA(FSM, this));
FSM.AddState(States.B, new StateB(FSM, this));
// The two modes can be mixed on the same FSM instance: some states use AddState, others use the chained FSM.State(...)
FSM.StartState(States.A);
```

Chained mode suits rapid development or the early phase with few states. When the state count or per-state code grows, switch to class mode. Both modes share the same `FSM<T>` instance, so no full rewrite is needed (Doc.md:4366-4372, 4443-4448).

## TableKit: built for joint queries

`List` / `Dictionary` support queries on a single dimension only. Joint queries (for example, "filter by Level and Age at the same time") degrade into linear scans. `Table<T>` (`Toolkits/_CoreKit/TableKit/Script/TableKit.cs`) maintains several `TableIndex<TKey, T>` indexes over the same data, balancing query flexibility and performance (Doc.md:4486-4490, 4566):

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
// Narrow the range with an index first, then refine with LINQ; faster than running Where over the whole table
foreach (var s in school.LevelIndex.Get(2).Where(s => s.Age < 3))
{
    Debug.Log($"{s.Age}:{s.Level}:{s.Name}");
}
```

TableKit is framework infrastructure, not a top-level API for business code. The internal data management of ResKit and UIKit is built on it (Doc.md:4568, corresponding to `ResTable.cs` / `UIPanelTable.cs`). Business code generally does not need to use `Table<T>` directly, unless there is a real multi-dimensional joint-query need.

## PoolKit: two object pools with different semantics

### SimpleObjectPool: for business code, no recycle-limit checks

```csharp
var pool = new SimpleObjectPool<Fish>(() => new Fish(), initCount: 50);
var fish = pool.Allocate();  // CurCount: 50 -> 49
pool.Recycle(fish);          // CurCount: 49 -> 50
```

`SimpleObjectPool<T>.Recycle()` (`SimpleObjectPool.cs:71-78`) unconditionally pushes the object back onto the stack. It does not check whether the object is already in the pool, and it does not check any cap. Recycling the same object twice puts two references to it in the pool. Suitable for lightweight cases where you guarantee yourself that nothing is recycled twice.

### SafeObjectPool: a singleton global pool with a cap and double-recycle protection

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

`SafeObjectPool<T>` is an `ISingleton` (`SafeObjectPool.cs:66-84`), and `T` must implement `IPoolable`. `Recycle()` checks `IsRecycled` to prevent double recycling, and checks `MaxCacheCount`. When the cap is exceeded, the object still gets its `OnRecycled()` callback but is not pooled, and is left to the GC (`SafeObjectPool.cs:155-176`):

```csharp
public override bool Recycle(T t)
{
    if (t == null || t.IsRecycled) return false;         // prevent double recycling
    if (mMaxCount > 0 && mCacheStack.Count >= mMaxCount)
    {
        t.OnRecycled();  // the callback still fires
        return false;    // but it is not pooled; left to the GC
    }
    t.IsRecycled = true;
    t.OnRecycled();
    mCacheStack.Push(t);
    return true;
}
```

Choice: for small objects used internally by one script, use `SimpleObjectPool`. For a shared global pool across scripts that needs a memory cap (bullets, effects and other frequently created and destroyed objects), use `SafeObjectPool`.

## GridKit: 2D grids

### EasyGrid: fixed width and height

```csharp
var grid = new EasyGrid<string>(4, 4);
grid.Fill("Empty");
grid[2, 3] = "Hello";
grid.ForEach((x, y, content) => Debug.Log($"({x},{y}):{content}"));
```

Suits scenarios with fixed dimensions, such as match-3 games, Tetris, board games, and Tilemap tile data (Doc.md:4941-4947). Its design draws on GameMaker Studio's `ds_grid`.

### DynaGrid: negative coordinates and dynamic growth

```csharp
var dynaGrid = new DynaGrid<MyData>();
dynaGrid[1, 1] = new MyData { Key = "Hero" };
dynaGrid[-1, -10] = new MyData { Key = "Enemy" }; // negative coordinates work directly; no bounds need to be declared in advance
dynaGrid.ForEach((x, y, data) => Debug.Log($"{x} {y} {data.Key}"));
```

`DynaGrid<T>` (`Toolkits/_CoreKit/GridKit/DynaGrid.cs`) does not require width and height up front. Writing to any coordinate, including negative ones, grows the underlying storage on demand. It suits cases where the bounds are unknown or the grid must expand in four directions, such as a map growing outward from an origin. By contrast, `EasyGrid` requires width and height fixed at construction (Doc.md:5067-5104).

## See also

- Reactive `BindableList<T>` / `BindableDictionary<TKey,TValue>` are outside the scope of this page; see [BINDABLE_QUERY.md](./BINDABLE_QUERY.md).
