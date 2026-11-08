### qframework_get_status
Report the install kind (Toolkits, core-only or none), detected assemblies, package version, editor locale and ResKit SimulationMode. Works with or without QFramework installed; call it first.

**Parameters:** None

**Returns:** `installed`, `installKind` (`"toolkits"`, `"coreOnly"` or `"none"`), `assemblies`, `version`, `editorLocaleIsCN`, `simulationMode`

`version` is read from `Assets/QFramework/Framework/PackageVersion.json`, which exists only in a standard Toolkits layout; relocated or single-file installs report `null`.
