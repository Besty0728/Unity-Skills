# Package Skills

Unity Package Manager 操作，支持包的安装、移除和 Cinemachine 自动配置。

## Skills

### `package_list`
列出所有已安装的包。
**Parameters:** None.

**Returns:**
```json
{
  "success": true,
  "count": 15,
  "packages": [{"name": "com.unity.cinemachine", "version": "3.1.3", "displayName": "Cinemachine"}]
}
```

### `package_check`
检查包是否已安装。
**Parameters:**
- `packageId` (string, required): 包 ID，如 `com.unity.cinemachine`

**Returns:**
```json
{"packageId": "com.unity.cinemachine", "installed": true, "version": "3.1.3"}
```

### `package_install`
安装指定包。
**Parameters:**
- `packageId` (string, required): 包 ID
- `version` (string, optional): 版本号

### `package_remove`
移除已安装的包。
**Parameters:**
- `packageId` (string, required): 包 ID

### `package_refresh`
刷新已安装包列表缓存。
**Parameters:** None.

### `package_install_cinemachine`
安装 Cinemachine，自动处理依赖。
**Parameters:**
- `version` (int, optional): 2 或 3，默认 3。CM3 自动安装 Splines 依赖。

### `package_get_cinemachine_status`
获取 Cinemachine 安装状态。
**Parameters:** None.

**Returns:**
```json
{
  "cinemachine": {"installed": true, "version": "3.1.3", "isVersion3": true},
  "splines": {"installed": true, "version": "2.8.0"}
}
```
