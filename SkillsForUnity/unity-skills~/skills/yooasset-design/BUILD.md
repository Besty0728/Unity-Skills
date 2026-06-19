---
name: unity-yooasset-build
description: "YooAsset build pipeline, Collector, and BuildParameters (v2.3.18, YooAsset.Editor) — AssetBundleBuilder entry point, collect rules, and Editor-only build APIs. YooAsset 构建管线、Collector 与 BuildParameters(v2.3.18,YooAsset.Editor:AssetBundleBuilder 入口、收集规则、仅编辑器构建 API)。"
type: reference
---

# YooAsset - Build Pipeline, Collector, BuildParameters

All rules come from `Editor/AssetBundleBuilder/AssetBundleBuilder.cs`, `Editor/AssetBundleBuilder/BuildParameters.cs`, `Editor/AssetBundleBuilder/BuildPipeline/ScriptableBuildPipeline/ScriptableBuildParameters.cs`, `Editor/AssetBundleCollector/CollectCommand.cs`, and `Editor/AssetBundleCollector/CollectRules/IFilterRule.cs`.

Everything in this document lives in the namespace `YooAsset.Editor` and is only available in the Editor assembly.

## The one build entry point

```csharp
// Editor/AssetBundleBuilder/AssetBundleBuilder.cs:11
public class AssetBundleBuilder
{
    public BuildResult Run(
        BuildParameters    buildParameters,
        List<IBuildTask>   buildPipeline,
        bool               enableLog);     // :18
}
```

Everything else — bundle layout, compression, collector configuration — flows through the `buildParameters` argument and the `buildPipeline` task list. A typical caller:

```csharp
var builder = new AssetBundleBuilder();
var result  = builder.Run(parameters, taskList, enableLog: true);
if (!result.Success) Debug.LogError(result.ErrorInfo);
```

Source for the control flow: `AssetBundleBuilder.cs:18-58`. `Run` clears the build context, instantiates a `BuildParametersContext`, initializes `BuildLogger`, and dispatches via `BuildRunner.Run`.

## `BuildParameters` (abstract base class)

```csharp
// Editor/AssetBundleBuilder/BuildParameters.cs:12
public abstract class BuildParameters
{
    public string BuildOutputRoot;                                 // :17
    public string BuildinFileRoot;                                 // :22
    public string BuildPipeline;                                   // :27
    public int    BuildBundleType;                                 // :32   cast from EBuildBundleType
    public BuildTarget BuildTarget;                                // :37
    public string PackageName;                                     // :42
    public string PackageVersion;                                  // :47
    public string PackageNote;                                     // :52   defaults to DateTime.Now if empty

    public bool   ClearBuildCacheFiles = false;                    // :57
    public bool   UseAssetDependencyDB = false;                    // :63   big speedup for large projects
    public bool   EnableSharePackRule  = false;                    // :68
    public bool   SingleReferencedPackAlone = true;                // :74
    public bool   VerifyBuildingResult = false;                    // :79

    public EFileNameStyle         FileNameStyle         = EFileNameStyle.HashName;   // :84
    public EBuildinFileCopyOption BuildinFileCopyOption = EBuildinFileCopyOption.None; // :89
    public string                 BuildinFileCopyParams;                             // :94

    public IEncryptionServices       EncryptionServices;           // :99
    public IManifestProcessServices  ManifestProcessServices;      // :104
    public IManifestRestoreServices  ManifestRestoreServices;      // :109

    public virtual void   CheckBuildParameters();                  // :119
    public virtual string GetPipelineOutputDirectory();            // :177
    public virtual string GetPackageOutputDirectory();             // :189
    public virtual string GetPackageRootDirectory();               // :201
    public virtual string GetBuildinRootDirectory();               // :213
}
```

`CheckBuildParameters` throws on missing `BuildOutputRoot` / `BuildinFileRoot` / `BuildPipeline` / `BuildBundleType` / `PackageName` / `PackageVersion` / while Unity's own `BuildPipeline.isBuildingPlayer` is true. Populate every one of those fields before `Run`.

## `ScriptableBuildParameters` — the common subclass

```csharp
// Editor/AssetBundleBuilder/BuildPipeline/ScriptableBuildPipeline/ScriptableBuildParameters.cs:10
public class ScriptableBuildParameters : BuildParameters
{
    public ECompressOption CompressOption = ECompressOption.Uncompressed;   // :15
    public bool StripUnityVersion = false;                                   // :20  (2.3.15+)
    public bool DisableWriteTypeTree = false;                                // :25
    public bool IgnoreTypeTreeChanges = true;                                // :30  docstring: "invalid parameter"
    public bool ReplaceAssetPathWithAddress = false;                         // :36  (2.3.17+) saves manifest memory
    public bool TrackSpriteAtlasDependencies = false;                        // :41  (2.3.15+)
    public bool WriteLinkXML = true;                                         // :47
    public string CacheServerHost;                                           // :52
    public int    CacheServerPort;                                           // :57
    public string BuiltinShadersBundleName;                                  // :63
    public string MonoScriptsBundleName;                                     // :68

    public BundleBuildParameters GetBundleBuildParameters();                 // :74  translates to SBP's own type
}
```

The `GetBundleBuildParameters()` method produces a `UnityEditor.Build.Pipeline.BundleBuildParameters` so the Scriptable Build Pipeline package can consume it. Key mappings at `:80-88`:

- `CompressOption.Uncompressed` → `BundleCompression.Uncompressed`
- `CompressOption.LZMA` → `BundleCompression.LZMA`
- `CompressOption.LZ4` → `BundleCompression.LZ4`
- `StripUnityVersion = true` → `ContentBuildFlags.StripUnityVersion`
- `DisableWriteTypeTree = true` → `ContentBuildFlags.DisableWriteTypeTree`

### Practical tuning guide

| Goal | Field |
|------|-------|
| Smallest install size | `CompressOption = ECompressOption.LZMA` |
| Fastest runtime load | `CompressOption = ECompressOption.LZ4` |
| Save runtime manifest memory | `ReplaceAssetPathWithAddress = true` (2.3.17+) |
| Keep sprite-atlas refs intact after refactor | `TrackSpriteAtlasDependencies = true` |
| Reduce bundle size / load cost | `DisableWriteTypeTree = true` (only when you fully control the runtime type set) |
| Faster rebuild on large projects | `UseAssetDependencyDB = true` on the base `BuildParameters` |

## `RawFileBuildParameters` — for raw-file bundles

Located at `Editor/AssetBundleBuilder/BuildPipeline/RawFileBuildPipeline/RawFileBuildParameters.cs`. Ships the same base fields plus, from 2.3.18:

```csharp
public bool IncludePathInHash = false;   // CHANGELOG.md:27-35 — hash accounts for file path when true
```

Use raw-file bundles for data files Unity does not natively import (arbitrary JSON, custom config blobs, binary payloads).

## `AssetBundleCollector` — what goes into a bundle

`AssetBundleCollector` is configured via an Editor window (`Tools > YooAsset > AssetBundle Collector`) which writes a ScriptableObject. At build time the collector walks your project, applies your group + pack rules, and emits the list of bundle-to-asset mappings.

You mostly interact with two pieces:

### `CollectCommand`

```csharp
// Editor/AssetBundleCollector/CollectCommand.cs:24
public class CollectCommand
{
    public string PackageName { get; }                // :29
    public IIgnoreRule IgnoreRule { get; }            // :34
    public bool SimulateBuild { set; }                // :40  sets 3 flags at once
    public int  CollectFlags  { get; set; }           // :53
    public bool UniqueBundleName { get; set; }        // :58
    public bool UseAssetDependencyDB { get; set; }    // :63
    public bool EnableAddressable { get; set; }       // :68
    public bool SupportExtensionless { get; set; }    // :73  (default true in 2.3.15+; turn OFF to save runtime memory when fuzzy-loading isn't needed)
    public bool LocationToLower { get; set; }         // :78
    public bool IncludeAssetGUID { get; set; }        // :83
    public bool AutoCollectShaders { get; set; }      // :88
    public AssetDependencyCache AssetDependency { get; }   // :91
    public void SetFlag(ECollectFlags flag, bool isOn);    // :110
    public bool IsFlagSet(ECollectFlags flag);             // :121
}
```

`ECollectFlags`:

```csharp
// CollectCommand.cs:4-22
[Flags]
public enum ECollectFlags
{
    None = 0,
    IgnoreGetDependencies = 1 << 0,     // skip dependency resolution
    IgnoreStaticCollector = 1 << 1,     // skip static collectors
    IgnoreDependCollector = 1 << 2,     // skip depend collectors
}
```

### `IFilterRule` — the 2.3.16 breaking change

```csharp
// Editor/AssetBundleCollector/CollectRules/IFilterRule.cs:23
public interface IFilterRule
{
    string FindAssetType { get; }                     // :29  NEW in 2.3.16 — required
    bool   IsCollectAsset(FilterRuleData data);       // :35
}

public struct FilterRuleData                          // :4
{
    public string AssetPath;
    public string CollectPath;
    public string GroupName;
    public string UserData;
}
```

`FindAssetType` is a Unity asset-type filter (matching the second argument of `AssetDatabase.FindAssets`, e.g. `"t:Prefab"`, `"t:Sprite"`, `"t:Texture2D t:AudioClip"`). It narrows the search scope — critical when your collector directory has many thousands of files.

Source of the migration requirement: `CHANGELOG.md:179-192`.

## `IEncryptionServices` (build-side)

The build-side sibling of `IDecryptionServices`. Populate `BuildParameters.EncryptionServices` to encrypt bundles at build time. Bundles produced with an encryption service require the matching `IDecryptionServices` at runtime (see FILESYSTEM.md).

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Forgetting `FindAssetType` on an `IFilterRule` implementation

```csharp
// ❌ Pre-2.3.16 code — compilation breaks in 2.3.18
public class MyFilterRule : IFilterRule
{
    public bool IsCollectAsset(FilterRuleData data) => /* ... */;
    // missing FindAssetType
}

// ✅ CORRECT
public class MyFilterRule : IFilterRule
{
    public string FindAssetType => "t:Prefab";
    public bool IsCollectAsset(FilterRuleData data) => /* ... */;
}
```

Source: `CHANGELOG.md:179-192`, `IFilterRule.cs:29`.

### 2. Leaving `PackageVersion` empty

```csharp
// ❌ WRONG — CheckBuildParameters throws ErrorCode.PackageVersionIsNullOrEmpty
var p = new ScriptableBuildParameters { PackageName = "Default" };
builder.Run(p, pipeline, true);

// ✅ CORRECT
p.PackageVersion = "1.0.0";  // or DateTime.UtcNow.ToString("yyyyMMddHHmm")
```

Source: `BuildParameters.cs:159-163`.

### 3. `PackageNote` assumed required

```csharp
// ✅ Optional — if empty, YooAsset fills in DateTime.Now (BuildParameters.cs:166-169)
```

### 4. Building while Unity is building the player

```csharp
// ❌ WRONG — YooAsset throws ErrorCode.ThePipelineIsBuiding
if (UnityEditor.BuildPipeline.isBuildingPlayer) builder.Run(...);

// ✅ CORRECT — gate on isBuildingPlayer == false
```

Source: `BuildParameters.cs:121-126`.

### 5. Using `IgnoreTypeTreeChanges`

```csharp
// ⚠ docstring at ScriptableBuildParameters.cs:28-30 explicitly says this is an invalid parameter ("无效参数")
// Treat it as a no-op. Do NOT rely on its behavior.
```

### 6. Enabling `ReplaceAssetPathWithAddress` and then using path-style locations

```csharp
// ❌ WRONG — the manifest no longer stores asset paths; LoadAssetAsync("Assets/.../player.prefab") fails
p.ReplaceAssetPathWithAddress = true;
// later
package.LoadAssetAsync<GameObject>("Assets/Prefabs/player.prefab");

// ✅ CORRECT — when the flag is on, call by addressable address
package.LoadAssetAsync<GameObject>("player");
```

Source: `ScriptableBuildParameters.cs:32-36`.

### 7. Leaving `SupportExtensionless = true` when you never do fuzzy loads

```csharp
// ⚠ SupportExtensionless = true generates extra location entries in the manifest to allow "player" → "Assets/Prefabs/player.prefab".
//   If your code exclusively loads with exact addresses/paths, set it to false to shrink manifest memory.
// Source: CollectCommand.cs:70-73, CHANGELOG.md:261-276.
```

### 8. Omitting `UseAssetDependencyDB` on very large projects

```csharp
// ⚠ On a 10-thousand-asset project, the default (false) makes the collect step several minutes.
p.UseAssetDependencyDB = true;   // enables the speedup
```

Source: `BuildParameters.cs:59-63`.

## Canonical build template

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Pipeline.Interfaces;
using YooAsset.Editor;

public static class BuildYooAsset
{
    public static BuildResult RunStandardBuild(string packageName, string version, BuildTarget target)
    {
        var p = new ScriptableBuildParameters
        {
            BuildOutputRoot  = $"{System.Environment.CurrentDirectory}/Bundles",
            BuildinFileRoot  = $"{System.Environment.CurrentDirectory}/Assets/StreamingAssets",
            BuildPipeline    = nameof(EBuildPipeline.ScriptableBuildPipeline),
            BuildBundleType  = (int)EBuildBundleType.AssetBundle,
            BuildTarget      = target,
            PackageName      = packageName,
            PackageVersion   = version,

            // Tuning
            ClearBuildCacheFiles         = false,
            UseAssetDependencyDB         = true,
            VerifyBuildingResult         = true,
            CompressOption               = ECompressOption.LZ4,
            ReplaceAssetPathWithAddress  = true,
            TrackSpriteAtlasDependencies = true,
            FileNameStyle                = EFileNameStyle.HashName,
            BuildinFileCopyOption        = EBuildinFileCopyOption.None,
        };

        // Pipeline task list — use the stock `ScriptableBuildPipelineFactory` (or equivalent in your project)
        List<IBuildTask> taskList = new ScriptableBuildPipelineFactory().Create();

        var builder = new AssetBundleBuilder();
        return builder.Run(p, taskList, enableLog: true);
    }
}
#endif
```

> The exact factory class name (`ScriptableBuildPipelineFactory`) depends on your setup — the built-in windows assemble it via `Editor/AssetBundleBuilder/AssetBundleBuilderWindow.cs`. If you're scripting the build from scratch, inspect the samples under `Samples~/Extension Sample/Editor/CustomBuildPipeline/` for a concrete reference.
