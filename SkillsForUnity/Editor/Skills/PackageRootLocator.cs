namespace UnitySkills.Internal
{
    /// <summary>
    /// The on-disk root of this package (PackageInfo.resolvedPath), shared by the skill installer and the evaluation
    /// runner launcher. The assembly lookup can come back without a path for some install kinds, hence the asset-path
    /// fallback. Reads PackageInfo, so main thread only.
    /// </summary>
    internal static class PackageRootLocator
    {
        internal const string PackageName = "com.besty.unity-skills";

        internal static bool TryGet(out string root)
        {
            root = null;
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PackageRootLocator).Assembly);
            if (packageInfo != null)
                root = packageInfo.resolvedPath;

            if (string.IsNullOrEmpty(root))
            {
                packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName);
                if (packageInfo != null)
                    root = packageInfo.resolvedPath;
            }

            return !string.IsNullOrEmpty(root);
        }
    }
}

// Producer:Betsy
