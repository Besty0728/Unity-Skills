using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnitySkills.Internal;
using UnitySkills.Tests.Core;

namespace UnitySkills.Tests
{
    /// <summary>
    /// Runs every EditMode fixture under UnitySkills.Tests.* (Core and OptionalPackages) against an in-memory
    /// per-project settings store with no legacy global values, so no test writes into the real project's
    /// UserSettings/UnitySkills/settings.json and no test depends on the developer's own settings. A fixture that needs a
    /// file-backed store or legacy values opens its own <see cref="ProjectPrefsTestScope"/> inside this one.
    /// </summary>
    [SetUpFixture]
    public sealed class ProjectPrefsTestIsolation
    {
        private ProjectPrefsTestScope _scope;

        [OneTimeSetUp]
        public void OneTimeSetUp() => _scope = ProjectPrefsTestScope.InMemory();

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _scope?.Dispose();
            _scope = null;
        }
    }
}

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Points <see cref="ProjectPrefs"/> at a test store for its lifetime and makes the owners of the per-project
    /// settings drop what they cached from the previous store (and raise their change events, so the router's output
    /// caches and the /health snapshot follow). Dispose in reverse order of creation.
    /// </summary>
    internal sealed class ProjectPrefsTestScope : IDisposable
    {
        private IDisposable _redirect;

        private ProjectPrefsTestScope(IDisposable redirect)
        {
            _redirect = redirect;
            ReloadOwners();
        }

        /// <summary>Values in memory; <paramref name="legacyValues"/> stands in for the pre-3.0 global EditorPrefs.</summary>
        public static ProjectPrefsTestScope InMemory(IDictionary<string, object> legacyValues = null) =>
            new ProjectPrefsTestScope(ProjectPrefs.UseForTests(null, legacyValues));

        /// <summary>Values in <c>&lt;projectRoot&gt;/UserSettings/UnitySkills/settings.json</c>.</summary>
        public static ProjectPrefsTestScope AtRoot(string projectRoot, IDictionary<string, object> legacyValues = null,
            bool legacyFromEditorPrefs = false) =>
            new ProjectPrefsTestScope(ProjectPrefs.UseForTests(projectRoot, legacyValues, legacyFromEditorPrefs));

        public void Dispose()
        {
            if (_redirect == null) return;
            _redirect.Dispose();
            _redirect = null;
            ReloadOwners();
        }

        /// <summary>The settings owners that cache a value read from the store.</summary>
        internal static void ReloadOwners()
        {
            SkillsSurfaceProfile.ReloadForTests();
            SkillRouter.ResetSummaryPreferencesForTests();
            SkillsModeManager.ReloadPreferencesForTests();
        }
    }
}

// Producer:Betsy
