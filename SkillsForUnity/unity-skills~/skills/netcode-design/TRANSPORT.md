# Netcode - Transport (UnityTransport)

All rules come from `Runtime/Transports/UTP/UnityTransport.cs` and `Runtime/Transports/NetworkTransport.cs`.

## Default transport: UnityTransport

```csharp
namespace Unity.Netcode.Transports.UTP;
public class UnityTransport : NetworkTransport { ... }
```

Attached to the same GameObject as the NetworkManager. The NetworkManager references it through `NetworkConfig.NetworkTransport`.

## ConnectionAddressData fields

Source: `UnityTransport.cs:222-236`.

```csharp
[Serializable]
public struct ConnectionAddressData {
    public string Address;             // IPv4 the client will dial
    public ushort Port;                // port number
    public string ServerListenAddress; // bind address for server/host (commonly "0.0.0.0" for external reach)
}
```

**Field semantics — common trap**:
- On a **client**, `Address` is the target IP to dial.
- On a **server/host**, `Address` is used with `ServerListenAddress`; in server mode `Address` can be the machine's outward-facing IP.
- `ServerListenAddress` is only used on server/host. When `""` or `null`, it falls back to `Address` — a frequent source of bugs. Always set it explicitly to `"0.0.0.0"` (any NIC) or `"127.0.0.1"` (loopback only).

## Three connection modes

### 1. Direct connect (LAN / public IP)

```csharp
var t = NetworkManager.Singleton.GetComponent<UnityTransport>();
t.SetConnectionData("192.168.1.10", 7777, listenAddress: "0.0.0.0");
NetworkManager.Singleton.StartHost();   // or StartServer / StartClient
```

Signature: `SetConnectionData(string ipv4Address, ushort port, string listenAddress = null)` (`UnityTransport.cs:856`).

### 2. Unity Relay (NAT traversal via a cloud relay)

```csharp
// 1. Allocate on Unity Services (omitted)
// 2. Feed the transport
t.SetRelayServerData(new RelayServerData(...));   // :785
// Or the lower-level call
t.SetRelayServerData(
    ipv4Address:            "relay.region.unity.com",
    port:                   443,
    allocationIdBytes:      allocBytes,
    keyBytes:               keyBytes,
    connectionDataBytes:    connDataBytes,
    hostConnectionDataBytes: null,                   // clients only
    isSecure:               true);                   // DTLS
// :776
```

`SetHostRelayData(...)` / `SetClientRelayData(...)` are convenience wrappers.

> **Do not** call `SetConnectionData` and `SetRelayServerData` on the same transport. In Relay mode, Netcode manages the connection data itself.

### 3. SinglePlayer transport (no network, local only)

```
Runtime/Transports/SinglePlayer/SinglePlayerTransport.cs
```
Useful when you want the NetworkBehaviour architecture offline. Rarely used.

## Debug simulator

```csharp
public void SetDebugSimulatorParameters(int packetDelay, int packetJitter, int dropRate);
// :919
```

- `packetDelay` — milliseconds, one-way latency
- `packetJitter` — milliseconds, latency jitter
- `dropRate` — percentage 0-100, packet drop rate

Development only. Clear or guard this call before shipping.

## Secrets (DTLS / TLS)

```csharp
public void SetServerSecrets(string serverCertificate, string serverPrivateKey); // :1767
public void SetClientSecrets(string serverCommonName, string caCertificate = null); // :1787
```

Used for self-signed certificates. Relay is already secure, so this is only for direct-connect scenarios with custom TLS.

## NetworkDelivery enum

`Runtime/Transports/NetworkDelivery.cs`:

- `Unreliable` — not guaranteed, not ordered; lowest latency
- `UnreliableSequenced` — not guaranteed, but ordered
- `Reliable` — guaranteed, not ordered
- `ReliableSequenced` — guaranteed and ordered (default for RPC)
- `ReliableFragmentedSequenced` — reliable + ordered + automatic fragmentation for large payloads

`RpcDelivery.Reliable` maps to `ReliableFragmentedSequenced` (what most RPCs use by default); `RpcDelivery.Unreliable` maps to `Unreliable`.

## NetworkTopologyTypes

`Runtime/Transports/NetworkTransport.cs:273`:

```csharp
public enum NetworkTopologyTypes {
    ClientServer,          // default; server-authoritative
    DistributedAuthority,  // each NetworkObject has its own authority
}
```

Set on `NetworkConfig.NetworkTopology`. The transport itself does not enforce topology — `NetworkConfig` does.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Client setting Address to "0.0.0.0"

```csharp
// ❌ WRONG — 0.0.0.0 is a listen wildcard, not a dial target
t.SetConnectionData("0.0.0.0", 7777);
NetworkManager.Singleton.StartClient();

// ✅ CORRECT — use the server's reachable IP
t.SetConnectionData("192.168.1.10", 7777);
NetworkManager.Singleton.StartClient();
```

### 2. Server missing ServerListenAddress, so LAN peers cannot reach it

```csharp
// ❌ LAN peers cannot connect; Address is used as listen address and defaults to "127.0.0.1"
t.SetConnectionData("127.0.0.1", 7777);
NetworkManager.Singleton.StartServer();

// ✅ Bind every NIC explicitly
t.SetConnectionData("127.0.0.1", 7777, listenAddress: "0.0.0.0");
NetworkManager.Singleton.StartServer();
```

### 3. Mixing SetConnectionData and SetRelayServerData

```csharp
// ❌ The second call overwrites the first, but the "configure both just in case" pattern is still wrong
t.SetConnectionData("10.0.0.1", 7777);
t.SetRelayServerData(relayData);

// ✅ Pick one. When using Relay, do not call SetConnectionData
t.SetRelayServerData(relayData);
```

### 4. Modifying ConnectionData at runtime without a restart

```csharp
// ❌ Changing this mid-session has no effect
t.ConnectionData.Port = 9999;  // ignored while connected
```

Connection parameters are latched when `StartHost/Server/Client` is called. To change them: `Shutdown()` → edit → `StartXxx()`.

### 5. Forgetting to disable DebugSimulator in release builds

```csharp
// ❌ Ships with 100 ms latency + 10% drop rate
t.SetDebugSimulatorParameters(100, 20, 10);

// ✅ Guard with a platform define or strip before build
#if DEVELOPMENT_BUILD || UNITY_EDITOR
t.SetDebugSimulatorParameters(100, 20, 10);
#endif
```

### 6. Ignoring port-conflict failures

`StartHost/Server` return a `bool`. On failure (e.g. "port already in use"), the return is false and an error is logged. Always check:

```csharp
if (!NetworkManager.Singleton.StartHost()) {
    Debug.LogError("StartHost failed — check port / firewall");
    return;
}
```

## Minimal direct-connect template

```csharp
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

public class NetBootstrap : MonoBehaviour
{
    public ushort port = 7777;
    public string serverIp = "127.0.0.1";

    public void StartHost() {
        GetTransport().SetConnectionData(serverIp, port, "0.0.0.0");
        if (!NetworkManager.Singleton.StartHost()) Debug.LogError("Host failed");
    }

    public void StartServer() {
        GetTransport().SetConnectionData(serverIp, port, "0.0.0.0");
        if (!NetworkManager.Singleton.StartServer()) Debug.LogError("Server failed");
    }

    public void StartClient() {
        GetTransport().SetConnectionData(serverIp, port);
        if (!NetworkManager.Singleton.StartClient()) Debug.LogError("Client failed");
    }

    UnityTransport GetTransport() =>
        NetworkManager.Singleton.GetComponent<UnityTransport>();
}
```
