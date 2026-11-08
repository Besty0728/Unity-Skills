### asset_refresh
Refresh the AssetDatabase after external changes, e.g. files you wrote or deleted with your own file tools.

No parameters.

**Returns**: `{success, message, compileTriggered}`. When the refresh starts a script compilation (a `.cs`, `.asmdef`, `.asmref`, `.rsp` or `.dll` changed), `compileTriggered` is `true` and the result adds `status:"accepted"`, `jobId`, `waitUrl` (`/jobs/<jobId>?wait=90&expectInstance=<instanceId>`, already pinned to this Editor), `serverAvailability` and, when Unity reported the files, `scriptChanges` (up to 20 paths; `scriptChangesTruncated` counts the rest). `compileTriggered:false` means there is nothing to wait for.

Wrote a `.cs` yourself? Call `asset_refresh`, then GET its `waitUrl` as is, retrying the same URL through the domain reload: `curl -s --retry 20 --retry-connrefused --retry-all-errors --retry-delay 2 "http://localhost:<port><waitUrl>"`. `status:"completed"` with `compilation.compiled:true`, `success:true`, and `domainLoaded:true` proves the new types are ready to use; `"failed"` lists the compile errors in `resultData.compilation.errors`. The diagnostics are project-wide (`compilation.scope:"project"`); `compilation.compiled:false` means no compilation followed the refresh.
