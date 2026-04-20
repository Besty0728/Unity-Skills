# Netcode - Scene Management

All rules come from `Runtime/SceneManagement/NetworkSceneManager.cs` and `Runtime/Configuration/NetworkConfig.cs:109`.

## Master switch: `NetworkConfig.EnableSceneManagement`

```csharp
public bool EnableSceneManagement = true;   // default true
```
Source: `NetworkConfig.cs:109`.

- **true (recommended, default)**: every scene load/unload is scheduled by `NetworkSceneManager`; clients automatically sync to the server's scene state on connect
- **false**: your code is responsible for keeping Server and Client scene sets aligned. Only runtime-spawned objects are supported; in-scene NetworkObjects no longer sync

**Keep it true in most projects.** The `false` path (so-called "PrefabSync" mode) is only for unusual cases where the client's scene is strictly independent from the server's.

## Key APIs (NetworkSceneManager)

```csharp
// Load a new scene (Server only)
public SceneEventProgressStatus LoadScene(string sceneName, LoadSceneMode loadSceneMode);  // :1496

// Unload an additive scene (Server only)
public SceneEventProgressStatus UnloadScene(Scene scene);                                  // :1252

// Client synchronization mode (set this before starting the Server)
public void SetClientSynchronizationMode(LoadSceneMode mode);                              // :803

// Event subscriptions
public event Action<SceneEvent> OnSceneEvent;
public event SceneLoadedDelegateHandler OnLoadComplete;
public event SceneUnloadedDelegateHandler OnUnloadComplete;
public event OnSynchronizeCompleteDelegateHandler OnSynchronizeComplete;

// Custom filters
public VerifySceneBeforeLoadingDelegateHandler VerifySceneBeforeLoading;
public VerifySceneBeforeUnloadingDelegateHandler VerifySceneBeforeUnloading;
```

`SceneEventProgressStatus` values you will see most often:
- `Started` — load has begun
- `SceneNotLoaded` — the scene is not in Build Settings
- `SceneEventInProgress` — another scene event is already running
- `InvalidSceneName` / `SceneFailedVerification` — and similar

## LoadSceneMode

- `LoadSceneMode.Single` — unload every currently loaded scene and load the target as the sole scene
- `LoadSceneMode.Additive` — stack on top; call `UnloadScene` to remove a specific additive scene

## Load lifecycle

```
Server calls LoadScene("X", Single/Additive)
  ↓
All clients receive SceneEvent(Load)
  ↓
Unity SceneManager.LoadSceneAsync (on each peer)
  ↓
Load completes → OnLoadComplete(clientId, sceneName, mode)
  ↓
In-scene NetworkObjects are aligned by GlobalObjectIdHash
  ↓
OnSynchronizeComplete(clientId)   ← at this point every pre-placed NetworkObject has fired OnNetworkSpawn
```

## Client sync on connect

- Default `SetClientSynchronizationMode(LoadSceneMode.Single)`: on connect, the client loads exactly the scene set the server currently has loaded (Single mode first unloads the client's non-active scenes, then rebuilds from the server's list)
- If the client should keep a local UI scene that the server never touches, switch to Additive mode and filter in `VerifySceneBeforeLoading`

## In-scene NetworkObjects

- A scene asset listed in Build Settings
- Every pre-placed NetworkObject in that scene has a `GlobalObjectIdHash`
- When both server and client load that scene, Netcode aligns the pre-placed objects by hash and fires `OnNetworkSpawn`

> You do **not** manually Spawn in-scene objects. `Start` / `StartHost` is enough.

## VerifySceneBeforeLoading callback

```csharp
public delegate bool VerifySceneBeforeLoadingDelegateHandler(
    int sceneIndex, string sceneName, LoadSceneMode loadSceneMode);

NetworkManager.SceneManager.VerifySceneBeforeLoading = (idx, name, mode) => {
    if (name == "ClientOnlyUI") return false;  // block this load
    return true;
};
```
A client can reject a scene load requested by the server (that client skips the load; the server continues). Useful for "the client never loads this editor/UI-only scene".

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Using Unity's `SceneManager.LoadScene` to switch scenes

```csharp
// ❌ WRONG — clients do not follow
UnityEngine.SceneManagement.SceneManager.LoadScene("GameScene");

// ✅ CORRECT — server calls NetworkSceneManager
if (IsServer) {
    NetworkManager.SceneManager.LoadScene("GameScene", LoadSceneMode.Single);
}
```

### 2. Client calling LoadScene directly

```csharp
// ❌ WRONG — returns a NotServer error
if (Input.GetKeyDown(KeyCode.Return)) {
    NetworkManager.SceneManager.LoadScene("Lobby", LoadSceneMode.Single);
}

// ✅ Ask the server to switch via RPC
[Rpc(SendTo.Server)] void RequestSceneServerRpc(FixedString32Bytes name) {
    NetworkManager.SceneManager.LoadScene(name.ToString(), LoadSceneMode.Single);
}
```

### 3. Forgetting to add the scene to Build Settings

`LoadScene("X")` returns `SceneNotLoaded`. Drag the scene into File > Build Settings.

### 4. Subscribing to SceneManager events in `Start()`

```csharp
// ⚠ NetworkManager.SceneManager can be null before StartHost/Server
void Start() {
    NetworkManager.SceneManager.OnLoadComplete += OnSceneLoaded;  // NRE risk
}

// ✅ Subscribe from NetworkManager.OnServerStarted or inside OnNetworkSpawn
void Start() {
    NetworkManager.OnServerStarted += () => {
        NetworkManager.SceneManager.OnLoadComplete += OnSceneLoaded;
    };
}
```

### 5. Persistent objects lost during scene transitions

`Single`-mode scene switches destroy every spawn-with-scene object. For persistent objects:
- `Spawn(destroyWithScene: false)`
- Re-parent if needed in the new scene (a DDoL NetworkObject can be used as the target of `TrySetParent`)

### 6. Two LoadScene calls in the same frame

```csharp
// ❌ The second call returns SceneEventInProgress
NetworkManager.SceneManager.LoadScene("A", LoadSceneMode.Single);
NetworkManager.SceneManager.LoadScene("B", LoadSceneMode.Single);  // rejected

// ✅ Wait for OnLoadComplete before issuing the next one
NetworkManager.SceneManager.OnLoadComplete += OnLoaded;
NetworkManager.SceneManager.LoadScene("A", LoadSceneMode.Single);

void OnLoaded(ulong clientId, string name, LoadSceneMode mode) {
    if (name == "A") {
        NetworkManager.SceneManager.LoadScene("B", LoadSceneMode.Additive);
    }
}
```

## Scene-switch template

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
        // The entry with clientId == NetworkManager.ServerClientId is the server's own completion.
    }
}
```
