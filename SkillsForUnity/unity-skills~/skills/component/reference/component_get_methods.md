### component_get_methods
Discover method metadata without invoking user code. Instance discovery describes a Component type; static discovery also accepts ordinary C# types.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentType` | string | Yes | - | Full CLR type name; simple names must be unambiguous |
| `isStatic` | bool | No | false | Discover static methods instead of instance methods |
| `includePrivate` | bool | No | true | Include inherited private/protected methods |
| `methodName` | string | No | null | Optional exact method-name filter |
| `assembly` | string | No | null | Assembly name to disambiguate the type |

**Returns**: `{componentType, methods: [{name, signature, declaringType, isStatic, visibility, returnType, genericArity, parameters: [{name, type, optional, isOut, isRef, isParams}]}], limit, truncated}`. At most 500 methods are returned. Use parameter `type` values to build `parameterTypes` for invocation; ref/out types end in `&`.
