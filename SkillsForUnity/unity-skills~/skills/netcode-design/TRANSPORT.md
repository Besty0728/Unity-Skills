# Netcode - Transport (UnityTransport)

所有规则来自 `Runtime/Transports/UTP/UnityTransport.cs`、`Runtime/Transports/NetworkTransport.cs`。

## 默认 Transport：UnityTransport

```csharp
namespace Unity.Netcode.Transports.UTP;
public class UnityTransport : NetworkTransport { ... }
```

挂在 NetworkManager 同一 GameObject 上，NetworkManager 通过 `NetworkConfig.NetworkTransport` 引用。

## ConnectionAddressData 字段

源码位置：`UnityTransport.cs:222-236`。

```csharp
[Serializable]
public struct ConnectionAddressData {
    public string Address;            // 客户端要连的目标 IP (IPv4)
    public ushort Port;               // 端口
    public string ServerListenAddress; // 服务端监听的 IP（通常 "0.0.0.0" 允许外部）
}
```

**字段语义陷阱**：
- `Address` 在 **client** 视角：要去连接的目标 IP
- `Address` 在 **server/host** 视角：通常与 ServerListenAddress 一起理解；server 模式下 `Address` 可等于本机对外 IP
- `ServerListenAddress` 只有 server/host 用；`""` 或 `null` 会回退到 `Address`，但这常见误用 — 推荐显式填 `"0.0.0.0"` 或 `"127.0.0.1"`

## 三种连接模式

### 1. 直连（LAN / 公网 IP）

```csharp
var t = NetworkManager.Singleton.GetComponent<UnityTransport>();
t.SetConnectionData("192.168.1.10", 7777, listenAddress: "0.0.0.0");
NetworkManager.Singleton.StartHost();   // 或 StartServer / StartClient
```

签名：`SetConnectionData(string ipv4Address, ushort port, string listenAddress = null)`（`UnityTransport.cs:856`）。

### 2. Unity Relay（跨 NAT，通过云中继）

```csharp
// 1. 从 Unity Services 分配 Allocation（略）
// 2. 填入 transport
t.SetRelayServerData(new RelayServerData(...));   // :785
// 或低层
t.SetRelayServerData(
    ipv4Address:            "relay.region.unity.com",
    port:                   443,
    allocationIdBytes:      allocBytes,
    keyBytes:               keyBytes,
    connectionDataBytes:    connDataBytes,
    hostConnectionDataBytes: null,                   // 仅 client 需要
    isSecure:               true);                   // DTLS
// :776
```

`SetHostRelayData(...)` / `SetClientRelayData(...)` 是方便方法。

> **不要** 同时调 `SetConnectionData` 和 `SetRelayServerData`。Relay 模式由 Netcode 自动处理连接信息。

### 3. SinglePlayer Transport（无网络，纯本地）

```
Runtime/Transports/SinglePlayer/SinglePlayerTransport.cs
```
用于 offline 模式仍需 NetworkBehaviour 架构的情况；一般不用。

## 调试模拟器

```csharp
public void SetDebugSimulatorParameters(int packetDelay, int packetJitter, int dropRate);
// :919
```

- `packetDelay` — 毫秒，单向延迟
- `packetJitter` — 毫秒，延迟抖动
- `dropRate` — 百分比 0-100，丢包率

仅开发调试；发布前清零或删除调用。

## Secrets（DTLS / TLS）

```csharp
public void SetServerSecrets(string serverCertificate, string serverPrivateKey); // :1767
public void SetClientSecrets(string serverCommonName, string caCertificate = null); // :1787
```

自签证书场景用；通常 Relay 已内置 secure。

## NetworkDelivery 枚举

`Runtime/Transports/NetworkDelivery.cs`：

- `Unreliable` — 不保可靠、不保序；最低延迟
- `UnreliableSequenced` — 不保可靠，但保序
- `Reliable` — 可靠，非保序
- `ReliableSequenced` — 可靠 + 保序（默认 RPC）
- `ReliableFragmentedSequenced` — 同上 + 自动分片（大 payload）

`RpcDelivery.Reliable` 对应 `ReliableFragmentedSequenced`（大多 RPC 默认走这条）；`RpcDelivery.Unreliable` 对应 `Unreliable`。

## NetworkTopologyTypes

`Runtime/Transports/NetworkTransport.cs:273`：

```csharp
public enum NetworkTopologyTypes {
    ClientServer,          // 默认；Server 权威
    DistributedAuthority,  // 每个 NetworkObject 有自己的 authority
}
```

在 `NetworkConfig.NetworkTopology` 设置；Transport 本身不强约束，拓扑由 NetworkConfig 决定。

## ❌ 常见错误 vs ✅ 正确模式

### 1. Client 把 Address 设为 "0.0.0.0"

```csharp
// ❌ WRONG — 0.0.0.0 是监听通配，不能作为目标
t.SetConnectionData("0.0.0.0", 7777);
NetworkManager.Singleton.StartClient();

// ✅ CORRECT — Client 填 server 的真实 IP
t.SetConnectionData("192.168.1.10", 7777);
NetworkManager.Singleton.StartClient();
```

### 2. Server 漏填 ServerListenAddress，外部无法连

```csharp
// ❌ LAN 上其他设备连不上；Address 作为监听地址默认就是 "127.0.0.1"
t.SetConnectionData("127.0.0.1", 7777);
NetworkManager.Singleton.StartServer();

// ✅ 显式监听全部网卡
t.SetConnectionData("127.0.0.1", 7777, listenAddress: "0.0.0.0");
NetworkManager.Singleton.StartServer();
```

### 3. 同一 Transport 既调 SetConnectionData 又调 SetRelayServerData

```csharp
// ❌ 后面的覆盖前面，但常见写出"两种都配置以防万一"的谬误
t.SetConnectionData("10.0.0.1", 7777);
t.SetRelayServerData(relayData);

// ✅ 二选一：用 Relay 就不要 SetConnectionData
t.SetRelayServerData(relayData);
```

### 4. 运行时修改 ConnectionData 却不重启

```csharp
// ❌ 改完字段没 Shutdown + StartHost/Client 是无效的
t.ConnectionData.Port = 9999;  // 运行中无效
```
连接参数在 StartHost/Server/Client 时锁定。修改需：`Shutdown()` → 改 data → `StartXxx()`。

### 5. 生产环境忘了关 DebugSimulator

```csharp
// ❌ 发布构建仍带 100ms 延迟 + 10% 丢包
t.SetDebugSimulatorParameters(100, 20, 10);

// ✅ 用 #if 围栏或构建前清理
#if DEVELOPMENT_BUILD || UNITY_EDITOR
t.SetDebugSimulatorParameters(100, 20, 10);
#endif
```

### 6. 端口冲突没捕获

`StartHost/Server` 返回 `bool`，失败时返回 false（内部日志会说"端口被占用"）。需要：

```csharp
if (!NetworkManager.Singleton.StartHost()) {
    Debug.LogError("StartHost failed — check port / firewall");
    return;
}
```

## 最小直连模板

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
