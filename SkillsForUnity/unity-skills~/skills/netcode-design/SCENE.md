# Netcode - Scene Management

所有规则来自 `Runtime/SceneManagement/NetworkSceneManager.cs`、`Runtime/Configuration/NetworkConfig.cs:109`。

## 核心开关：`NetworkConfig.EnableSceneManagement`

```csharp
public bool EnableSceneManagement = true;   // 默认 true
```
源码：`NetworkConfig.cs:109`。

- **true（推荐，默认）**：所有场景加载 / 卸载由 `NetworkSceneManager` 统一调度；client 连接时自动同步 server 的场景状态
- **false**：由业务自行保证 server/client 场景一致（仅动态 Spawn 可用，场景内预置 NetworkObject 不再同步）

**大多数项目保持 true**。False 路径（所谓 "PrefabSync" 模式）仅用于特殊场景（例如 client 严格独立于 server scene）。

## 关键 API（NetworkSceneManager）

```csharp
// 加载新场景（Server only）
public SceneEventProgressStatus LoadScene(string sceneName, LoadSceneMode loadSceneMode);  // :1496

// 卸载 additive 场景（Server only）
public SceneEventProgressStatus UnloadScene(Scene scene);                                  // :1252

// 客户端连接时期望的同步模式（Server 启动前设置）
public void SetClientSynchronizationMode(LoadSceneMode mode);                              // :803

// 事件订阅
public event Action<SceneEvent> OnSceneEvent;
public event SceneLoadedDelegateHandler OnLoadComplete;
public event SceneUnloadedDelegateHandler OnUnloadComplete;
public event OnSynchronizeCompleteDelegateHandler OnSynchronizeComplete;

// 自定义拦截
public VerifySceneBeforeLoadingDelegateHandler VerifySceneBeforeLoading;
public VerifySceneBeforeUnloadingDelegateHandler VerifySceneBeforeUnloading;
```

`SceneEventProgressStatus` 返回值常见：
- `Started` — 加载开始
- `SceneNotLoaded` — 场景未在 Build Settings
- `SceneEventInProgress` — 另一场景事件进行中
- `InvalidSceneName` / `SceneFailedVerification` 等

## LoadSceneMode

- `LoadSceneMode.Single` — 清空当前所有已加载场景，加载目标为唯一场景
- `LoadSceneMode.Additive` — 叠加加载；用 `UnloadScene` 卸载特定 additive

## 场景加载生命周期

```
Server 调用 LoadScene("X", Single/Additive)
  ↓
所有 client 收到 SceneEvent(Load)
  ↓
Unity SceneManager.LoadSceneAsync (每端本地)
  ↓
加载完成 → OnLoadComplete(clientId, sceneName, mode)
  ↓
同步场景内的 NetworkObject（按 GlobalObjectIdHash 对齐）
  ↓
OnSynchronizeComplete(clientId)   ← 此时所有预置 NetworkObject 的 OnNetworkSpawn 已触发
```

## Client 连接时的同步

- 默认 `SetClientSynchronizationMode(LoadSceneMode.Single)`：client 连上后将 **server 当前所有已加载场景** 加载到本机（Single 模式下 client 会先卸载自己本地全部非 active scene，再按 server 列表重建）
- 如需 client 保留本地 UI 场景等不被覆盖，改用 Additive 模式并在 `VerifySceneBeforeLoading` 过滤

## 场景内预置 NetworkObject

- Build Settings 里的 scene asset
- scene 里预放的 NetworkObject，每个都有 `GlobalObjectIdHash`
- 同 scene 在 server 和 client 加载后，Netcode 按 hash **对齐** 预置对象，并触发 `OnNetworkSpawn`

> **不需要** 自己手动 Spawn 场景预置对象。直接 `Start` / `StartHost` 即可。

## VerifySceneBeforeLoading 回调

```csharp
public delegate bool VerifySceneBeforeLoadingDelegateHandler(
    int sceneIndex, string sceneName, LoadSceneMode loadSceneMode);

NetworkManager.SceneManager.VerifySceneBeforeLoading = (idx, name, mode) => {
    if (name == "ClientOnlyUI") return false;  // 阻止加载
    return true;
};
```
Client 可拒绝 server 下发的某个场景（该 client 跳过加载；server 继续）。用于 "client 永远不加载 UI/Editor 专用场景" 等。

## ❌ 常见错误 vs ✅ 正确模式

### 1. 用 Unity 原生 `SceneManager.LoadScene` 切场景

```csharp
// ❌ WRONG — client 不会跟随
UnityEngine.SceneManagement.SceneManager.LoadScene("GameScene");

// ✅ CORRECT — Server 调用 NetworkSceneManager
if (IsServer) {
    NetworkManager.SceneManager.LoadScene("GameScene", LoadSceneMode.Single);
}
```

### 2. Client 调 LoadScene

```csharp
// ❌ WRONG — 返回 NotServer 错误
if (Input.GetKeyDown(KeyCode.Return)) {
    NetworkManager.SceneManager.LoadScene("Lobby", LoadSceneMode.Single);
}

// ✅ 通过 Rpc 请求 Server 切场景
[Rpc(SendTo.Server)] void RequestSceneServerRpc(FixedString32Bytes name) {
    NetworkManager.SceneManager.LoadScene(name.ToString(), LoadSceneMode.Single);
}
```

### 3. 忘记把场景加入 Build Settings

`LoadScene("X")` 返回 `SceneNotLoaded`。把 scene 拖进 File > Build Settings。

### 4. 在 `Start()` 订阅 SceneManager 事件

```csharp
// ⚠ NetworkManager.SceneManager 在 StartHost/Server 之前可能为 null
void Start() {
    NetworkManager.SceneManager.OnLoadComplete += OnSceneLoaded;  // NRE 风险
}

// ✅ 订阅 NetworkManager.OnServerStarted 或 在 OnNetworkSpawn 里处理
void Start() {
    NetworkManager.OnServerStarted += () => {
        NetworkManager.SceneManager.OnLoadComplete += OnSceneLoaded;
    };
}
```

### 5. 场景切换时 `DontDestroyWithOwner` 对象意外消失

场景 Single 切换会销毁"destroyWithScene=true"的 Spawn 对象。持久对象应：
- `Spawn(destroyWithScene: false)`
- 并在新场景里可能重复注册其 Parent（被 DDoL 的 NetworkObject 可用 `TrySetParent`）

### 6. 在同一帧连续调两次 LoadScene

```csharp
// ❌ 第二次返回 SceneEventInProgress
NetworkManager.SceneManager.LoadScene("A", LoadSceneMode.Single);
NetworkManager.SceneManager.LoadScene("B", LoadSceneMode.Single);  // 被拒

// ✅ 等待 OnLoadComplete 再发下一个
NetworkManager.SceneManager.OnLoadComplete += OnLoaded;
NetworkManager.SceneManager.LoadScene("A", LoadSceneMode.Single);

void OnLoaded(ulong clientId, string name, LoadSceneMode mode) {
    if (name == "A") {
        NetworkManager.SceneManager.LoadScene("B", LoadSceneMode.Additive);
    }
}
```

## 场景切换模板

```csharp
public class SceneController : NetworkBehaviour
{
    public override void OnNetworkSpawn() {
        if (IsServer) {
            NetworkManager.SceneManager.OnLoadComplete += OnLoadComplete;
        }
    }
    public override void OnNetworkDespawn() {
        if (IsServer) {
            NetworkManager.SceneManager.OnLoadComplete -= OnLoadComplete;
        }
    }

    [Rpc(SendTo.Server)]
    void GoToLevelServerRpc(int levelId) {
        var status = NetworkManager.SceneManager.LoadScene(
            $"Level_{levelId}", LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started) {
            Debug.LogWarning($"LoadScene failed: {status}");
        }
    }

    void OnLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode) {
        // server 端所有 client 都完成后（clientId == NetworkManager.ServerClientId 是 server 自己那条）
    }
}
```
