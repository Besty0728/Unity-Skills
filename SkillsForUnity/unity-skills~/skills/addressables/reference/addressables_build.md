### addressables_build
`AddressableAssetSettings.BuildPlayerContent()` for the **current** build target. Long-running and blocking: it runs on the Editor main thread and is not an async job.
**Parameters:** None
Returns `success`, `duration` (seconds), and `message`. On a build failure it returns `success: false` with `duration` and the build system's `error`; on an exception it returns `success: false` with `error` and `details`.
