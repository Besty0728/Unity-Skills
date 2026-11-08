### qframework_generate_architecture_code
Generate architecture-layer code to disk through `CreatePreview` and `Generate`. Refuses to overwrite an existing file at the target path.

Same parameters as `qframework_preview_architecture_code` (`codeType`, `inputName`, `namespaceName` and `outputRoot` required; `generateInterface` optional, default false).

**Returns:** `success`, `error`, `assetPath`, `codeType`, `className`. Failures also add `hint`: the same upstream-validation note as the preview when `CreatePreview` rejects the input, or a fixed note that QFramework's file-exists error means it refused to overwrite existing code.

Refused under Approval and Auto (`MayTriggerReload`): Bypass or allowlist only.
