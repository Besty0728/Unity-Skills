### qframework_preview_architecture_code
Preview the code that `ArchitectureCodeGenerator.CreatePreview` would generate for a name. Writes no file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| codeType | string | Yes | - | `ArchitectureCodeType` name: Architecture, System, Model, Command, Utility or Query |
| inputName | string | Yes | - | Base name of the generated type |
| namespaceName | string | Yes | - | Namespace of the generated code |
| outputRoot | string | Yes | - | Output root passed to `CreatePreview` |
| generateInterface | bool | No | false | Also generate the matching interface; meaningful only where `SupportsInterfaceGeneration` is true |

**Returns:** `isValid`, `error`, `codeType`, `className`, `namespaceName`, `assetPath`, `code`. When `isValid` is false, `hint` is also returned: `error` is QFramework's own upstream validation text and may be Chinese (for example "请输入名字"); `hint` is a fixed English note on which of `codeType`, `inputName`, `namespaceName` or `outputRoot` to check.
