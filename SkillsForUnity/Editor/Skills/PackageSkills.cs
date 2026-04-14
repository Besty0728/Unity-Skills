using UnityEngine;
using UnityEditor;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// 包管理技能 - AI 可调用的 Package Manager 操作
    /// </summary>
    public static class PackageSkills
    {
        [UnitySkill("package_list", "List all installed packages",
            Category = SkillCategory.Package, Operation = SkillOperation.Query,
            Tags = new[] { "package", "list", "upm", "installed" },
            Outputs = new[] { "count", "packages" },
            ReadOnly = true)]
        public static object PackageList()
        {
            var packages = PackageManagerHelper.InstalledPackages;
            if (packages == null)
                return new { error = "Package list not ready. Call package_refresh first." };

            var list = packages.Values.Select(p => new { name = p.name, version = p.version, displayName = p.displayName }).ToList();
            return new { success = true, count = list.Count, packages = list };
        }

        [UnitySkill("package_check", "Check if a package is installed. Returns version if installed.",
            Category = SkillCategory.Package, Operation = SkillOperation.Query,
            Tags = new[] { "package", "check", "version", "installed" },
            Outputs = new[] { "packageId", "installed", "version" },
            RequiresInput = new[] { "packageId" },
            ReadOnly = true)]
        public static object PackageCheck(string packageId)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            var installed = PackageManagerHelper.IsPackageInstalled(packageId);
            var version = PackageManagerHelper.GetInstalledVersion(packageId);
            return new { packageId, installed, version };
        }

        [UnitySkill("package_install", "Install a package. version is optional.",
            Category = SkillCategory.Package, Operation = SkillOperation.Execute,
            Tags = new[] { "package", "install", "upm", "add", "job" },
            Outputs = new[] { "status", "message", "jobId" },
            RequiresInput = new[] { "packageId" })]
        public static object PackageInstall(string packageId, string version = null)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            bool handledSynchronously = false;
            bool immediateSuccess = true;
            string immediateMessage = null;
            BatchJobRecord job = null;

            PackageManagerHelper.InstallPackage(packageId, version, (success, msg) =>
            {
                if (job == null)
                {
                    handledSynchronously = true;
                    immediateSuccess = success;
                    immediateMessage = msg;
                    return;
                }

                if (!success)
                    AsyncJobService.FailJob(job.jobId, $"Package install failed: {msg}", "failed_package");
            });

            if (handledSynchronously && !immediateSuccess)
                return new { success = false, error = immediateMessage ?? "Package install failed." };

            job = AsyncJobService.StartPackageJob("install", packageId, version);

            return new {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                message = $"Installing {packageId}" + (version != null ? $"@{version}" : "") + "... Use job_status/job_wait for progress.",
                serverAvailability = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    $"正在安装包 {packageId}。包导入和程序集刷新期间，REST 服务可能短暂不可用。",
                    alwaysInclude: true,
                    retryAfterSeconds: 8)
            };
        }

        [UnitySkill("package_remove", "Remove an installed package.",
            Category = SkillCategory.Package, Operation = SkillOperation.Execute | SkillOperation.Delete,
            Tags = new[] { "package", "remove", "uninstall", "upm", "job" },
            Outputs = new[] { "message", "jobId" },
            RequiresInput = new[] { "packageId" })]
        public static object PackageRemove(string packageId)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            if (!PackageManagerHelper.IsPackageInstalled(packageId))
                return new { error = $"Package {packageId} is not installed" };

            bool handledSynchronously = false;
            bool immediateSuccess = true;
            string immediateMessage = null;
            BatchJobRecord job = null;

            PackageManagerHelper.RemovePackage(packageId, (success, msg) =>
            {
                if (job == null)
                {
                    handledSynchronously = true;
                    immediateSuccess = success;
                    immediateMessage = msg;
                    return;
                }

                if (!success)
                    AsyncJobService.FailJob(job.jobId, $"Package removal failed: {msg}", "failed_package");
            });

            if (handledSynchronously && !immediateSuccess)
                return new { success = false, error = immediateMessage ?? "Package remove failed." };

            job = AsyncJobService.StartPackageJob("remove", packageId);

            return new {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                message = $"Removing {packageId}... Use job_status/job_wait for progress.",
                serverAvailability = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    $"正在移除包 {packageId}。包导入和程序集刷新期间，REST 服务可能短暂不可用。",
                    alwaysInclude: true,
                    retryAfterSeconds: 8)
            };
        }

        [UnitySkill("package_refresh", "Refresh the installed package list cache.",
            Category = SkillCategory.Package, Operation = SkillOperation.Execute,
            Tags = new[] { "package", "refresh", "cache", "upm", "job" },
            Outputs = new[] { "message", "jobId" })]
        public static object PackageRefresh()
        {
            if (PackageManagerHelper.IsRefreshing)
            {
                var existingJob = AsyncJobService.StartPackageJob("refresh", "(package_list)");
                return new
                {
                    success = true,
                    status = "accepted",
                    jobId = existingJob.jobId,
                    message = "Already refreshing package list..."
                };
            }

            BatchJobRecord job = null;
            PackageManagerHelper.RefreshPackageList(success =>
            {
                if (job == null)
                    return;

                if (!success)
                    AsyncJobService.FailJob(job.jobId, "Package list refresh failed.", "failed_package_refresh");
            });

            job = AsyncJobService.StartPackageJob("refresh", "(package_list)");
            return new
            {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                message = "Refreshing package list..."
            };
        }

        [UnitySkill("package_install_cinemachine", "Install Cinemachine. version: 2 or 3 (default 3). CM3 auto-installs Splines dependency.",
            Category = SkillCategory.Package, Operation = SkillOperation.Execute,
            Tags = new[] { "package", "install", "cinemachine", "camera", "job" },
            Outputs = new[] { "message", "jobId" })]
        public static object PackageInstallCinemachine(int version = 3)
        {
            var useV3 = version >= 3;
            var targetVersion = useV3 ? PackageManagerHelper.Cinemachine3Version : PackageManagerHelper.Cinemachine2Version;

            // 检查是否已安装
            var status = PackageManagerHelper.GetCinemachineStatus();
            if (status.installed)
            {
                if ((useV3 && status.isVersion3) || (!useV3 && !status.isVersion3))
                    return new { success = true, message = $"Cinemachine {status.version} is already installed." };
            }

            bool handledSynchronously = false;
            bool immediateSuccess = true;
            string immediateMessage = null;
            BatchJobRecord job = null;

            PackageManagerHelper.InstallCinemachine(useV3, (success, msg) =>
            {
                if (job == null)
                {
                    handledSynchronously = true;
                    immediateSuccess = success;
                    immediateMessage = msg;
                    return;
                }

                if (!success)
                    AsyncJobService.FailJob(job.jobId, $"Cinemachine install failed: {msg}", "failed_package");
            });

            if (handledSynchronously && !immediateSuccess)
                return new { success = false, error = immediateMessage ?? "Cinemachine install failed." };

            job = AsyncJobService.StartPackageJob("install", PackageManagerHelper.CinemachinePackageId, targetVersion);

            var depMsg = useV3 ? " (with Splines dependency)" : "";
            return new {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                message = $"Installing Cinemachine {targetVersion}{depMsg}... Use job_status/job_wait for progress.",
                serverAvailability = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    $"正在安装 Cinemachine {targetVersion}{depMsg}。包导入和程序集刷新期间，REST 服务可能短暂不可用。",
                    alwaysInclude: true,
                    retryAfterSeconds: 8)
            };
        }

        [UnitySkill("package_install_splines", "Install Unity Splines package. Auto-detects correct version for Unity 6 vs Unity 2022.",
            Category = SkillCategory.Package, Operation = SkillOperation.Execute,
            Tags = new[] { "package", "install", "splines", "path", "job" },
            Outputs = new[] { "message", "jobId" })]
        public static object PackageInstallSplines()
        {
            var currentVersion = PackageManagerHelper.GetInstalledVersion(PackageManagerHelper.SplinesPackageId);
            var targetVersion = PackageManagerHelper.GetRecommendedSplinesVersion();

            if (currentVersion == targetVersion)
                return new { success = true, message = $"Splines {currentVersion} is already installed." };

            bool handledSynchronously = false;
            bool immediateSuccess = true;
            string immediateMessage = null;
            BatchJobRecord job = null;

            PackageManagerHelper.InstallSplines((success, msg) =>
            {
                if (job == null)
                {
                    handledSynchronously = true;
                    immediateSuccess = success;
                    immediateMessage = msg;
                    return;
                }

                if (!success)
                    AsyncJobService.FailJob(job.jobId, $"Splines install failed: {msg}", "failed_package");
            });

            if (handledSynchronously && !immediateSuccess)
                return new { success = false, error = immediateMessage ?? "Splines install failed." };

            job = AsyncJobService.StartPackageJob("install", PackageManagerHelper.SplinesPackageId, targetVersion);

            return new {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                message = $"Installing Splines {targetVersion}" + (currentVersion != null ? $" (upgrading from {currentVersion})" : "") + "... Use job_status/job_wait for progress.",
                serverAvailability = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    $"正在安装 Splines {targetVersion}。包导入和程序集刷新期间，REST 服务可能短暂不可用。",
                    alwaysInclude: true,
                    retryAfterSeconds: 8)
            };
        }

        [UnitySkill("package_get_cinemachine_status", "Get Cinemachine installation status.",
            Category = SkillCategory.Package, Operation = SkillOperation.Query,
            Tags = new[] { "package", "cinemachine", "status", "version" },
            Outputs = new[] { "cinemachine", "splines" },
            ReadOnly = true)]
        public static object PackageGetCinemachineStatus()
        {
            var status = PackageManagerHelper.GetCinemachineStatus();
            var splinesInstalled = PackageManagerHelper.IsPackageInstalled(PackageManagerHelper.SplinesPackageId);
            var splinesVersion = PackageManagerHelper.GetInstalledVersion(PackageManagerHelper.SplinesPackageId);

            return new {
                cinemachine = new {
                    installed = status.installed,
                    version = status.version,
                    isVersion3 = status.isVersion3
                },
                splines = new {
                    installed = splinesInstalled,
                    version = splinesVersion
                }
            };
        }

        [UnitySkill("package_search", "Search installed packages by name or displayName (does not search the Unity Registry)",
            Category = SkillCategory.Package, Operation = SkillOperation.Query,
            Tags = new[] { "package", "search", "find", "upm" },
            Outputs = new[] { "query", "count", "packages" },
            RequiresInput = new[] { "query" },
            ReadOnly = true)]
        public static object PackageSearch(string query)
        {
            if (Validate.Required(query, "query") is object err) return err;

            var packages = PackageManagerHelper.InstalledPackages;
            if (packages == null)
                return new { error = "Package list not ready. Call package_refresh first." };

            var matches = packages.Values
                .Where(p => p.name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (p.displayName != null && p.displayName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(p => new { name = p.name, version = p.version, displayName = p.displayName })
                .ToList();

            return new { success = true, query, count = matches.Count, packages = matches };
        }

        [UnitySkill("package_get_dependencies", "Get dependency list for an installed package",
            Category = SkillCategory.Package, Operation = SkillOperation.Query,
            Tags = new[] { "package", "dependencies", "upm", "info" },
            Outputs = new[] { "packageId", "version", "dependencyCount", "dependencies" },
            RequiresInput = new[] { "packageId" },
            ReadOnly = true)]
        public static object PackageGetDependencies(string packageId)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            var packages = PackageManagerHelper.InstalledPackages;
            if (packages == null)
                return new { error = "Package list not ready. Call package_refresh first." };

            if (!packages.TryGetValue(packageId, out var pkg))
                return new { error = $"Package not found: {packageId}" };

            var deps = pkg.dependencies?.Select(d => new { name = d.name, version = d.version }).ToList();
            return new { success = true, packageId, version = pkg.version, dependencyCount = deps?.Count ?? 0, dependencies = deps };
        }

        [UnitySkill("package_get_versions", "Get all available versions for a package",
            Category = SkillCategory.Package, Operation = SkillOperation.Query,
            Tags = new[] { "package", "versions", "upm", "upgrade" },
            Outputs = new[] { "packageId", "currentVersion", "compatibleVersion", "latestVersion", "allVersions" },
            RequiresInput = new[] { "packageId" },
            ReadOnly = true)]
        public static object PackageGetVersions(string packageId)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            var packages = PackageManagerHelper.InstalledPackages;
            if (packages == null)
                return new { error = "Package list not ready. Call package_refresh first." };

            if (!packages.TryGetValue(packageId, out var pkg))
                return new { error = $"Package not found: {packageId}" };

            var versions = pkg.versions?.all?.ToList();
            return new { success = true, packageId, currentVersion = pkg.version,
                compatibleVersion = pkg.versions?.compatible, latestVersion = pkg.versions?.latest,
                allVersions = versions };
        }
    }
}
