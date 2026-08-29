# BINDABLE_QUERY — BindableProperty / BindableList / BindableDictionary / Query

## 1. BindableProperty 接口与核心实现

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
`QFramework.cs:690-703`。注意 `IReadonlyBindableProperty<T>` 继承 `IEasyEvent`——BindableProperty 本质上是"一个值 + 一个 EasyEvent"，可以像普通 `IEasyEvent` 一样参与 `OrEvent` 组合（见 [EVENT_TOOLS.md](./EVENT_TOOLS.md)）。

`BindableProperty<T>` 实现（`QFramework.cs:705-760`）关键点：

- 构造函数 `BindableProperty(T defaultValue = default)`，默认值可选
- `Value` setter 先做 null 检查，再用静态 `Comparer` 判等，相等则**不触发事件**；不等才 `SetValue` + `mOnValueChanged.Trigger(value)`
- `Comparer` 是 `public static Func<T, T, bool>`，默认 `(a, b) => a.Equals(b)`；可用 `WithComparer(Func<T,T,bool>)` 覆盖（注意这是**静态字段**，对同一个 `T` 的所有实例生效，不是实例级配置）
- `SetValueWithoutEvent(T newValue)` 直接改 `mValue`，**不比较、不触发事件**——用于初始化时从存储加载旧值，不希望触发订阅逻辑（`Doc.md:2147-2148` 的用法）
- `Register` 只订阅之后的变化；`RegisterWithInitValue` 会先用当前值同步调用一次回调，再走 `Register`（`QFramework.cs:745-749`）——适合需要立即渲染初始界面的场景

```csharp
// Register vs RegisterWithInitValue（QFramework.cs:740-749）
mSomeValue.Register(v => Debug.Log(v));            // 只在下次变化时触发
mSomeValue.RegisterWithInitValue(v => Debug.Log(v)); // 立刻用当前值触发一次，之后再随变化触发
```

`ComparerAutoRegister`（`QFramework.cs:762-789`，`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`）在场景加载前把以下类型的 `Comparer` 从默认 `Equals` 换成 `==`：`int/float/double/string/long`、`Vector2/Vector3/Vector4`、`Color`（逐分量比较的 `Color32`）、`Bounds/Rect/Quaternion`、`Vector2Int/Vector3Int/BoundsInt`、`RangeInt`（比较 start+length）、`RectInt`（`.Equals`）。**自定义类/结构体不在此列**，默认走 `object.Equals`（值类型会装箱），高频更新的自定义类型建议自行 `WithComparer`。

`BindableProperty` 是独立工具，**可以完全脱离 QFramework 架构使用**（`Doc.md:2125`），不需要 Model/Architecture 也能用它做数据绑定。

## 2. BindableList<T> ——边缘工具，2024-09-18 新增

`Doc.md:8438-8592`；实现在 `_CoreKit/BindableKit/Scripts/BindableList.cs`（不在 QFramework.cs 单文件里，是 Toolkits 附加的工具，`[Serializable] class BindableList<T> : Collection<T>`）。应社区诉求补充的集合绑定，不是核心概念的一部分。暴露 6 个事件，全部是**惰性实例化的 `EasyEvent`**（首次访问属性才 `new`，`BindableList.cs:87-115`）：`OnCountChanged(int)`、`OnAdd(int index, T item)`、`OnMove(int oldIndex, int newIndex, T item)`、`OnRemove(int index, T item)`、`OnReplace(int index, T oldItem, T newItem)`、`OnClear()`。

```csharp
mNameList.OnAdd.Register((index, newName) => { /* 增量渲染新增项 */ })
    .UnRegisterWhenGameObjectDestroyed(gameObject);
mNameList.OnRemove.Register((index, nameItem) => { /* 移除对应视图 */ })
    .UnRegisterWhenGameObjectDestroyed(gameObject);
```

**两个容易踩的坑（`BindableList.cs:38-84`）**：
- 用索引器赋值 `list[i] = x` 触发的是 `SetItem` 覆写，**只会 `Trigger` `OnReplace`**，不会连带触发 `OnRemove`/`OnAdd`/`OnCountChanged`（`BindableList.cs:78-84`）——数量没变，这是对的，但如果你的订阅逻辑指望"替换=先删后加"就会漏事件
- `Move(oldIndex, newIndex)` 内部调用的是 `base.RemoveItem`/`base.InsertItem`（`Collection<T>` 基类方法，**跳过了本类覆写的 `RemoveItem`/`InsertItem`**），所以移动只会 `Trigger` `OnMove` 一次，**不会**额外触发 `OnRemove`+`OnAdd`+`OnCountChanged`（`BindableList.cs:60-67`）

用途：列表型 UI（背包格子、排行榜行）需要增量更新视图而非整表重绘时才有必要；数据量小或整表重绘代价不高时，直接用普通 `List<T>` + 一个 `BindableProperty<int>` 记数量往往更简单。

## 3. BindableDictionary<TKey,TValue> ——边缘工具，2024-09-19 新增，用途未定论

`Doc.md:8593-8712`；实现在 `_CoreKit/BindableKit/Scripts/BindableDictionary.cs`（同样是 Toolkits 附加工具，内部用 `Dictionary<TKey,TValue> mInner` 包了一层，实现 `IDictionary<TKey,TValue>`）。事件比 `BindableList` 少一个：`OnCountChanged`、`OnAdd(key, value)`、`OnRemove(key, value)`、`OnReplace(key, oldValue, newValue)`、`OnClear()`——**没有 `OnMove`**（字典无序，移动没有意义）。索引器赋值 `dict[key] = value` 会自动判断是新增还是替换（`TryGetValue` 先查一次），命中就 `OnReplace`，没命中就 `OnAdd` + `OnCountChanged`（`BindableDictionary.cs:26-44`）——这一点和 `BindableList` 的索引器行为不同，属于该实现自己更完整的处理。

作者原话（`Doc.md:8595`）：**"虽然笔者目前还不知道 BindableDictionary 能用在什么使用场景下，但是还是应童鞋的要求实现了"**。这是框架作者自己都不确定使用场景的功能，引用/推荐这个类型前先评估项目是否真的需要键值对级别的变更通知，不要把它当成字典类数据的默认绑定方案。

## 4. Query：CQRS 里的 Q

`IQuery<TResult>`（`QFramework.cs:342`，见 [LAYERS.md](./LAYERS.md) 接口清单）配合 `AbstractQuery<T>`（`QFramework.cs:348-360`）使用，`Do()` 转发到 `protected abstract T OnDo()`：

```csharp
// Doc.md:2627-2634，跨 Model 组合查询
public class SchoolAllPersonCountQuery : AbstractQuery<int>
{
    protected override int OnDo() =>
        this.GetModel<StudentModel>().StudentNames.Count +
        this.GetModel<TeacherModel>().TeacherNames.Count;
}

// Controller 里发起
mAllPersonCount = this.SendQuery(new SchoolAllPersonCountQuery());
```

**Query 是可选概念**（`Doc.md:2666`）：查询逻辑不重时直接写在 Controller 表现逻辑里就够了；查询需要跨多个 Model 组合、需要数据转换，或者项目规模变大导致查询逻辑发散时，才值得抽成 Query 对象集中管理。

CQRS 分工：**Command 负责增删改，Query 负责查**（`Doc.md:2669`）。如果游戏需要和服务器同步数据：拉取服务器数据的请求放 Query，增删改服务器数据的请求放 Command（`Doc.md:2672`）——这条经验规则同样适用于本地存储/配置表的读写划分。
