### qframework_scan_architecture
Scan loaded assemblies, excluding QFramework's own and system assemblies, for non-abstract types implementing `IArchitecture`, `ISystem`, `IModel`, `ICommand` (`<T>`), `IQuery<T>` or `IController`. Read-only type metadata; does not enter Play Mode.

**Parameters:** None

**Returns:** `architectures`, `systems`, `models`, `commands`, `queries`, `controllers`, `totalCount`
