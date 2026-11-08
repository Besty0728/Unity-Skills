# Netcode quick start

Detail behind [SKILL.md](../SKILL.md): one session from setup to shutdown. Each step is a `POST /skill/<name>?expectProject=<project>` call; steps that need no PlayMode can share one `POST /skills/batch`.

1. Inspect the current state:

```bash
curl -s "http://localhost:<port>/skill/netcode_check_setup?expectProject=<project>" -d '{}'
```

2. Create the NetworkManager with UnityTransport, then configure its NetworkConfig:

```bash
curl -s "http://localhost:<port>/skill/netcode_create_manager?expectProject=<project>" -d '{"name":"NetworkManager"}'
curl -s "http://localhost:<port>/skill/netcode_configure_manager?expectProject=<project>" -d '{"tickRate":30,"connectionApproval":false,"enableSceneManagement":true,"networkTopology":"ClientServer"}'
```

3. Transport: on a client `address` is the server IP; on a server `serverListenAddress` is the bind address (usually `0.0.0.0`).

```bash
curl -s "http://localhost:<port>/skill/netcode_set_transport_address?expectProject=<project>" -d '{"address":"127.0.0.1","port":7777,"serverListenAddress":"0.0.0.0"}'
```

4. Player prefab. Attaching a NetworkObject to a prefab asset path depends on object lookup support; the safer route is to instantiate the prefab in the scene, attach there, then register it:

```bash
curl -s "http://localhost:<port>/skill/netcode_create_prefabs_list?expectProject=<project>" -d '{"path":"Assets/NetworkPrefabs.asset"}'
curl -s "http://localhost:<port>/skill/netcode_add_to_prefabs_list?expectProject=<project>" -d '{"listPath":"Assets/NetworkPrefabs.asset","prefabPath":"Assets/Prefabs/Player.prefab"}'
curl -s "http://localhost:<port>/skill/netcode_set_player_prefab?expectProject=<project>" -d '{"prefabPath":"Assets/Prefabs/Player.prefab"}'
```

5. Generate a NetworkBehaviour template (`netcode_add_network_behaviour_script` is refused under Approval and Auto; see [SKILL.md](../SKILL.md)):

```bash
curl -s "http://localhost:<port>/skill/netcode_add_network_behaviour_script?expectProject=<project>" -d '{"className":"PlayerController","path":"Assets/Scripts/PlayerController.cs","includeRpc":true,"includeNetworkVariable":true}'
```

6. Drive the session in PlayMode: enter Play Mode with `editor_play`, then `netcode_start_host`, `netcode_get_status`, `netcode_shutdown`, and `editor_stop`.
