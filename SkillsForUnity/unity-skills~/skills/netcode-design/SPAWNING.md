# Netcode - Spawning & Prefab Registration

All rules come from `Runtime/Core/NetworkObject.cs:1884-2215`, `Runtime/Spawning/NetworkSpawnManager.cs`, `Runtime/Configuration/NetworkPrefab.cs`, and `Runtime/Configuration/NetworkPrefabsList.cs`.

## Happy path

```
① Create a NetworkPrefabsList asset (or add directly to NetworkConfig.Prefabs)
② Add prefabs (each with a NetworkObject component) to the list
③ If a prefab is the player prefab — assign it to NetworkConfig.PlayerPrefab
④ Start Server/Host (StartHost / StartServer)
⑤ Server: Instantiate(prefab) + .GetComponent<NetworkObject>().Spawn()
⑥ Each client receives the spawn message, creates the instance, and fires OnNetworkSpawn
```

## Key APIs

### Spawn variants on NetworkObject

```csharp
public void Spawn(bool destroyWithScene = false);                              // NetworkObject.cs:1884
public void SpawnWithOwnership(ulong clientId, bool destroyWithScene = false); // :1902
public void SpawnAsPlayerObject(ulong clientId, bool destroyWithScene = false); // :1912
public void Despawn(bool destroy = true);                                      // :1921
```

- `Spawn()` — ownership goes to the Server (`OwnerClientId = Server/Host ID = 0`)
- `SpawnWithOwnership(id)` — explicit owner
- `SpawnAsPlayerObject(id)` — spawns and marks the instance as that client's PlayerObject (`IsLocalPlayer` becomes true on that client)
- `Despawn(true)` — default, destroys the GameObject. `false` unregisters from the network but keeps the GameObject alive

### One-shot via NetworkSpawnManager

```csharp
public NetworkObject InstantiateAndSpawn(
    NetworkObject networkPrefab,
    ulong ownerClientId        = NetworkManager.ServerClientId,
    bool  destroyWithScene     = false,
    bool  isPlayerObject       = false,
    bool  forceOverride        = false,
    Vector3    position        = default,
    Quaternion rotation        = default);                             // NetworkSpawnManager.cs:736
```

Safer than manual `Instantiate + Spawn`: handles `NetworkPrefabHandler` overrides, scene association, and initial transform atomically.

### GetPlayerNetworkObject

```csharp
public NetworkObject GetPlayerNetworkObject(ulong clientId);  // NetworkSpawnManager.cs:372
```

Resolves a client's PlayerObject. The Server can query any client; a client can only query its own.

## NetworkPrefab shape

```csharp
[Serializable]
public class NetworkPrefab {
    public NetworkPrefabOverride Override;        // None / Prefab / Hash
    public GameObject Prefab;                     // the default prefab
    public GameObject SourcePrefabToOverride;     // used when Override=Prefab
    public uint       SourceHashToOverride;       // used when Override=Hash
    public GameObject OverridingTargetPrefab;     // prefab to swap in
    public uint SourcePrefabGlobalObjectIdHash { get; }
    public uint TargetPrefabGlobalObjectIdHash  { get; }
    public bool Validate(int index = -1);         // returning false causes Netcode to drop this entry
}
```
Source: `Runtime/Configuration/NetworkPrefab.cs:32-247`.

### NetworkPrefabsList (ScriptableObject, reusable)

```csharp
[CreateAssetMenu(fileName = "NetworkPrefabsList", menuName = "Netcode/Network Prefabs List")]
public class NetworkPrefabsList : ScriptableObject {
    public IReadOnlyList<NetworkPrefab> PrefabList { get; }
    public void Add(NetworkPrefab prefab);
    public void Remove(NetworkPrefab prefab);
    public bool Contains(GameObject prefab);
    public bool Contains(NetworkPrefab prefab);
}
```
Source: `Runtime/Configuration/NetworkPrefabsList.cs:15-95`.

**Runtime edits take effect immediately**: if multiple NetworkManagers reference the same list, `Add/Remove` broadcasts to every reference holder.

## GlobalObjectIdHash

- Every `NetworkObject` gets a `uint GlobalObjectIdHash` when the prefab is created or imported in the editor.
- This is the key Server and Client use to identify "the same prefab".
- **Important**: re-importing the same prefab preserves the hash; **copying** or **newly creating** a prefab produces a different hash. Both peers must ship from the same project, or the prefab files must match exactly.

## ForceSamePrefabs

`NetworkConfig.ForceSamePrefabs = true` (default) enforces identical `Prefabs` lists on Server and Client at connection time. Keep it true in production. Only set it to false when Server and Client intentionally ship different assets (e.g. different LOD) and you take responsibility for matching hashes yourself.

## NetworkPrefabOverride

- `None` — plain registration; Server and Client use the same prefab
- `Prefab` — Server requests prefab A, Client swaps in prefab B (both exist in the same project with different components)
- `Hash` — Client does not have the Server-side prefab, only the hash; it maps to a local prefab instead

The vast majority of projects only use `None`.

## Nesting rules

```csharp
public bool TrySetParent(NetworkObject parent, bool worldPositionStays = true);  // NetworkObject.cs:2196
public bool TrySetParent(GameObject   parent, bool worldPositionStays = true);   // :2155
public bool TrySetParent(Transform    parent, bool worldPositionStays = true);   // :2135
```

**Rules**:
- Once spawned, a NetworkObject **must** use `TrySetParent` to change its parent (Server call). Never assign `transform.parent = ...` directly.
- Do **not** nest a NetworkObject B as a static child of another NetworkObject **inside a prefab**. Re-parenting at runtime (after both are spawned) via `TrySetParent` is fine.
- `AutoObjectParentSync = true` (default) automatically replicates parent changes; set to false if you want to manage it through RPCs.

## Scene association

- `Spawn(destroyWithScene: true)` — binds the instance to the current active scene; it is auto-despawned when the scene unloads
- `Spawn(destroyWithScene: false)` — independent of the scene; requires explicit `Despawn` or `NetworkManager.Shutdown()`
- Full scene-switch rules: see [SCENE.md](./SCENE.md)

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Spawning on the client

```csharp
// ❌ WRONG — InvalidOperationException / message rejected
if (IsClient) {
    Instantiate(prefab).GetComponent<NetworkObject>().Spawn();
}

// ✅ Client sends a ServerRpc; Server spawns
[Rpc(SendTo.Server)]
void RequestSpawnServerRpc(Vector3 pos) {
    var go = Instantiate(prefab, pos, Quaternion.identity);
    go.GetComponent<NetworkObject>().Spawn();
}
```

### 2. Forgetting to register the prefab

```
// ❌ At runtime: Server spawns successfully, but clients log
// [Netcode] Failed to create object locally. [globalObjectIdHash=XXX] not found in prefab list.
```
Check: the prefab is in `NetworkConfig.Prefabs` or a `NetworkPrefabsList`; `PlayerPrefab` is also registered (some versions need the explicit entry).

### 3. Using a NetworkObject as a nested child in a prefab

```
Prefab A (NetworkObject)
 └─ Child (NetworkObject)   ← forbidden; Spawn will fail or warn
```

✅ Correct: split `Child` into its own prefab and re-parent at runtime with `TrySetParent(A)`.

### 4. Calling post-processing helpers that do not exist (don't confuse NGO with ProBuilder)

Spawn is final. You do NOT need to call `ToMesh()`, `Refresh()`, or any similar "commit" helper — those belong to ProBuilder, not NGO. Don't let unrelated Unity modules confuse you.

### 5. Despawning an object that isn't spawned

```csharp
// ❌ WRONG — guard is inverted
if (no.IsSpawned == false) no.Despawn();

// ✅ CORRECT
if (no.IsSpawned) no.Despawn();
```

### 6. `Destroy(go)` on a spawned NetworkObject

```csharp
// ❌ WRONG — other clients are not notified; state becomes inconsistent
Destroy(go);

// ✅ CORRECT
networkObject.Despawn(destroy: true);   // broadcasts despawn and destroys the GameObject
```

### 7. `NetworkPrefab.Override = Prefab` without setting `SourcePrefabToOverride`

`NetworkPrefab.Validate()` returns false and Netcode discards the entry. Fill every required field or use `Override = None`.

## Spawn / Despawn template

```csharp
using Unity.Netcode;
using UnityEngine;

public class EnemySpawner : NetworkBehaviour
{
    public NetworkObject enemyPrefab;  // assign in Inspector; must be registered

    public void SpawnEnemy(Vector3 pos) {
        if (!IsServer) return;  // strict authority
        var enemy = NetworkManager.SpawnManager.InstantiateAndSpawn(
            enemyPrefab,
            ownerClientId: NetworkManager.ServerClientId,
            destroyWithScene: true,
            position: pos,
            rotation: Quaternion.identity);
        // enemy is now spawned; clients will fire OnNetworkSpawn shortly
    }

    public void KillEnemy(NetworkObject enemy) {
        if (!IsServer) return;
        if (enemy.IsSpawned) enemy.Despawn(destroy: true);
    }
}
```
