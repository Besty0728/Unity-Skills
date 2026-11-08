# BINDABLE_QUERY - BindableProperty / BindableList / BindableDictionary / Query

## 1. BindableProperty interfaces and core implementation

```csharp
public interface IReadonlyBindableProperty<T> : IEasyEvent
{
    T Value { get; }
    IUnRegister RegisterWithInitValue(Action<T> action);
    void UnRegister(Action<T> onValueChanged);
    IUnRegister Register(Action<T> onValueChanged);
}

public interface IBindableProperty<T> : IReadonlyBindableProperty<T>
{
    new T Value { get; set; }
    void SetValueWithoutEvent(T newValue);
}
```
Source: `QFramework.cs:690-703`. Note that `IReadonlyBindableProperty<T>` inherits `IEasyEvent`. A BindableProperty is essentially "a value plus an EasyEvent", so it can take part in `OrEvent` composition like any `IEasyEvent` (see [EVENT_TOOLS.md](./EVENT_TOOLS.md)).

Key points of the `BindableProperty<T>` implementation (`QFramework.cs:705-760`):

- Constructor `BindableProperty(T defaultValue = default)`; the default value is optional
- The `Value` setter null-checks first, then uses the static `Comparer` to test equality. If equal, **no event fires**; only when different does it `SetValue` and `mOnValueChanged.Trigger(value)`
- `Comparer` is a `public static Func<T, T, bool>`, defaulting to `(a, b) => a.Equals(b)`. It can be overridden with `WithComparer(Func<T,T,bool>)`. Note this is a **static field**: it applies to all instances of the same `T`, not per-instance configuration
- `SetValueWithoutEvent(T newValue)` writes `mValue` directly, **without comparing and without firing events**. Use it to load an old value from storage during initialization when subscriber logic should not run (usage at `Doc.md:2147-2148`)
- `Register` subscribes only to later changes. `RegisterWithInitValue` first calls the callback once synchronously with the current value, then proceeds like `Register` (`QFramework.cs:745-749`). Use it when the initial UI must render immediately

```csharp
// Register vs RegisterWithInitValue (QFramework.cs:740-749)
mSomeValue.Register(v => Debug.Log(v));            // fires only on the next change
mSomeValue.RegisterWithInitValue(v => Debug.Log(v)); // fires immediately with the current value, then on every change
```

`ComparerAutoRegister` (`QFramework.cs:762-789`, `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`) swaps the `Comparer` from the default `Equals` to `==` before a scene loads, for these types: `int/float/double/string/long`, `Vector2/Vector3/Vector4`, `Color` (component-wise comparison of `Color32`), `Bounds/Rect/Quaternion`, `Vector2Int/Vector3Int/BoundsInt`, `RangeInt` (compares start+length), and `RectInt` (`.Equals`). **Custom classes and structs are not on this list** and use `object.Equals` by default (value types get boxed). For custom types that update frequently, set `WithComparer` yourself.

`BindableProperty` is a standalone tool and **can be used entirely outside the QFramework architecture** (`Doc.md:2125`). Data binding does not require a Model or Architecture.

## 2. BindableList<T>: peripheral tool, added 2024-09-18

`Doc.md:8438-8592`. Implemented in `_CoreKit/BindableKit/Scripts/BindableList.cs` (not in the QFramework.cs single file; it is a Toolkits add-on, `[Serializable] class BindableList<T> : Collection<T>`). It is a collection binding added in response to community requests, not part of the core concepts. It exposes 6 events, all **lazily instantiated `EasyEvent`s** (created on first property access, `BindableList.cs:87-115`): `OnCountChanged(int)`, `OnAdd(int index, T item)`, `OnMove(int oldIndex, int newIndex, T item)`, `OnRemove(int index, T item)`, `OnReplace(int index, T oldItem, T newItem)`, `OnClear()`.

```csharp
mNameList.OnAdd.Register((index, newName) => { /* incrementally render the new item */ })
    .UnRegisterWhenGameObjectDestroyed(gameObject);
mNameList.OnRemove.Register((index, nameItem) => { /* remove the matching view */ })
    .UnRegisterWhenGameObjectDestroyed(gameObject);
```

**Two easy-to-hit pitfalls (`BindableList.cs:38-84`)**:
- Index assignment `list[i] = x` triggers the `SetItem` override, which **only `Trigger`s `OnReplace`**; it does not also fire `OnRemove`/`OnAdd`/`OnCountChanged` (`BindableList.cs:78-84`). The count is unchanged, which is correct, but if your subscriber logic expects "replace = remove then add", you will miss events.
- `Move(oldIndex, newIndex)` internally calls `base.RemoveItem`/`base.InsertItem` (the `Collection<T>` base methods, **bypassing this class's overrides of `RemoveItem`/`InsertItem`**). So a move fires `OnMove` once, and **does not** additionally fire `OnRemove` + `OnAdd` + `OnCountChanged` (`BindableList.cs:60-67`).

Use: list-style UI (inventory slots, leaderboard rows) needs it only when incremental view updates are preferred over redrawing the whole table. For small data sets or when a full redraw is cheap, a plain `List<T>` plus a `BindableProperty<int>` tracking the count is often simpler.

## 3. BindableDictionary<TKey,TValue>: peripheral tool, added 2024-09-19, use case unsettled

`Doc.md:8593-8712`. Implemented in `_CoreKit/BindableKit/Scripts/BindableDictionary.cs` (also a Toolkits add-on; it wraps an internal `Dictionary<TKey,TValue> mInner` and implements `IDictionary<TKey,TValue>`). It has one fewer event than `BindableList`: `OnCountChanged`, `OnAdd(key, value)`, `OnRemove(key, value)`, `OnReplace(key, oldValue, newValue)`, `OnClear()`. **There is no `OnMove`**, since dictionaries are unordered and moving has no meaning. Index assignment `dict[key] = value` decides automatically between add and replace (it first checks with `TryGetValue`): a hit fires `OnReplace`, a miss fires `OnAdd` + `OnCountChanged` (`BindableDictionary.cs:26-44`). This differs from the `BindableList` indexer; it is that implementation's own, more complete handling.

The author's own words (`Doc.md:8595`): **"Although the author does not yet know what usage scenarios BindableDictionary is suited to, it was implemented at the request of readers."** This is a feature whose author is unsure of its use cases. Before referencing or recommending this type, assess whether the project really needs key-level change notifications. Do not treat it as the default binding solution for dictionary-shaped data.

## 4. Query: the Q in CQRS

`IQuery<TResult>` (`QFramework.cs:342`, see the interface list in [LAYERS.md](./LAYERS.md)) is used with `AbstractQuery<T>` (`QFramework.cs:348-360`). `Do()` forwards to `protected abstract T OnDo()`:

```csharp
// Doc.md:2627-2634, a query combining multiple Models
public class SchoolAllPersonCountQuery : AbstractQuery<int>
{
    protected override int OnDo() =>
        this.GetModel<StudentModel>().StudentNames.Count +
        this.GetModel<TeacherModel>().TeacherNames.Count;
}

// Issued from a Controller
mAllPersonCount = this.SendQuery(new SchoolAllPersonCountQuery());
```

**Query is an optional concept** (`Doc.md:2666`). When the query logic is not heavy, writing it directly in the Controller's presentation logic is enough. Extract it into a Query object to centralize it when the query combines several Models, needs data transformation, or the logic spreads out as the project grows.

For the CQRS split itself (Command for create/update/delete, Query for reads, and the server-sync rule), see section 4 of [LAYERS.md](./LAYERS.md).
