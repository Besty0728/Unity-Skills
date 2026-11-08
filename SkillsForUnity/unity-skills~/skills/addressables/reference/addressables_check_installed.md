### addressables_check_installed
Probe for the package and the settings asset. **The only skill that works without either.** Call it first.
**Parameters:** None
Returns `installed`, `configured`, `packageId`, and a `hint` naming the next action. When installed it adds `version` (from `PackageInfo.FindForAssembly`) and `settingsPath` (null when `configured` is false).
