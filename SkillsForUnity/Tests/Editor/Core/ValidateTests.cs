using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ValidateTests
    {
        // ──────────────────────────────────────────────
        //  Helper: extract "error" from anonymous object
        // ──────────────────────────────────────────────

        private static string GetError(object result)
        {
            if (result == null) return null;
            var prop = result.GetType().GetProperty("error");
            return prop?.GetValue(result)?.ToString();
        }

        // ══════════════════════════════════════════════
        //  Required
        // ══════════════════════════════════════════════

        [Test]
        public void Required_NullValue_ReturnsError()
        {
            var result = Validate.Required(null, "param");

            Assert.IsNotNull(result);
            StringAssert.Contains("param", GetError(result));
            StringAssert.Contains("required", GetError(result));
        }

        [Test]
        public void Required_EmptyString_ReturnsError()
        {
            var result = Validate.Required("", "param");

            Assert.IsNotNull(result);
            StringAssert.Contains("required", GetError(result));
        }

        [Test]
        public void Required_ValidString_ReturnsNull()
        {
            var result = Validate.Required("hello", "param");

            Assert.IsNull(result);
        }

        // ══════════════════════════════════════════════
        //  RequiredJsonArray
        // ══════════════════════════════════════════════

        [Test]
        public void RequiredJsonArray_NullValue_ReturnsError()
        {
            var result = Validate.RequiredJsonArray(null, "items");

            Assert.IsNotNull(result);
            StringAssert.Contains("required", GetError(result));
        }

        [Test]
        public void RequiredJsonArray_EmptyString_ReturnsError()
        {
            var result = Validate.RequiredJsonArray("", "items");

            Assert.IsNotNull(result);
            StringAssert.Contains("required", GetError(result));
        }

        [Test]
        public void RequiredJsonArray_EmptyArray_ReturnsError()
        {
            var result = Validate.RequiredJsonArray("[]", "items");

            Assert.IsNotNull(result);
            StringAssert.Contains("non-empty", GetError(result));
        }

        [Test]
        public void RequiredJsonArray_NullLiteral_ReturnsError()
        {
            var result = Validate.RequiredJsonArray("null", "items");

            Assert.IsNotNull(result);
            StringAssert.Contains("non-empty", GetError(result));
        }

        [Test]
        public void RequiredJsonArray_EmptyArrayWithWhitespace_ReturnsError()
        {
            var result = Validate.RequiredJsonArray("  []  ", "items");

            Assert.IsNotNull(result);
            StringAssert.Contains("non-empty", GetError(result));
        }

        [Test]
        public void RequiredJsonArray_ValidArray_ReturnsNull()
        {
            var result = Validate.RequiredJsonArray("[{\"name\":\"a\"}]", "items");

            Assert.IsNull(result);
        }

        // ══════════════════════════════════════════════
        //  InRange (float)
        // ══════════════════════════════════════════════

        [Test]
        public void InRange_Float_BelowMin_ReturnsError()
        {
            var result = Validate.InRange(-1f, 0f, 10f, "value");

            Assert.IsNotNull(result);
            StringAssert.Contains("must be between", GetError(result));
        }

        [Test]
        public void InRange_Float_AboveMax_ReturnsError()
        {
            var result = Validate.InRange(11f, 0f, 10f, "value");

            Assert.IsNotNull(result);
            StringAssert.Contains("must be between", GetError(result));
        }

        [Test]
        public void InRange_Float_AtMin_ReturnsNull()
        {
            var result = Validate.InRange(0f, 0f, 10f, "value");

            Assert.IsNull(result);
        }

        [Test]
        public void InRange_Float_AtMax_ReturnsNull()
        {
            var result = Validate.InRange(10f, 0f, 10f, "value");

            Assert.IsNull(result);
        }

        [Test]
        public void InRange_Float_InMiddle_ReturnsNull()
        {
            var result = Validate.InRange(5.5f, 0f, 10f, "value");

            Assert.IsNull(result);
        }

        // ══════════════════════════════════════════════
        //  InRange (int)
        // ══════════════════════════════════════════════

        [Test]
        public void InRange_Int_BelowMin_ReturnsError()
        {
            var result = Validate.InRange(-1, 0, 100, "count");

            Assert.IsNotNull(result);
            StringAssert.Contains("must be between", GetError(result));
        }

        [Test]
        public void InRange_Int_AboveMax_ReturnsError()
        {
            var result = Validate.InRange(101, 0, 100, "count");

            Assert.IsNotNull(result);
            StringAssert.Contains("must be between", GetError(result));
        }

        [Test]
        public void InRange_Int_AtMin_ReturnsNull()
        {
            var result = Validate.InRange(0, 0, 100, "count");

            Assert.IsNull(result);
        }

        [Test]
        public void InRange_Int_AtMax_ReturnsNull()
        {
            var result = Validate.InRange(100, 0, 100, "count");

            Assert.IsNull(result);
        }

        [Test]
        public void InRange_Int_InMiddle_ReturnsNull()
        {
            var result = Validate.InRange(50, 0, 100, "count");

            Assert.IsNull(result);
        }

        // ══════════════════════════════════════════════
        //  SafePath
        // ══════════════════════════════════════════════

        [Test]
        public void SafePath_NullPath_ReturnsError()
        {
            var result = Validate.SafePath(null, "path");

            Assert.IsNotNull(result);
            StringAssert.Contains("required", GetError(result));
        }

        [Test]
        public void SafePath_EmptyPath_ReturnsError()
        {
            var result = Validate.SafePath("", "path");

            Assert.IsNotNull(result);
            StringAssert.Contains("required", GetError(result));
        }

        [Test]
        public void SafePath_PathTraversal_ReturnsError()
        {
            var result = Validate.SafePath("Assets/../etc/passwd", "path");

            Assert.IsNotNull(result);
            StringAssert.Contains("traversal", GetError(result));
        }

        [Test]
        public void SafePath_PathTraversalDoubleDot_ReturnsError()
        {
            var result = Validate.SafePath("../secret", "path");

            Assert.IsNotNull(result);
            StringAssert.Contains("traversal", GetError(result));
        }

        [Test]
        public void SafePath_ValidAssetsPath_ReturnsNull()
        {
            var result = Validate.SafePath("Assets/Textures/icon.png", "path");

            Assert.IsNull(result);
        }

        [Test]
        public void SafePath_ValidPackagesPath_ReturnsNull()
        {
            var result = Validate.SafePath("Packages/com.unity.render-pipelines.core/Shaders/Common.hlsl", "path");

            Assert.IsNull(result);
        }

        [Test]
        public void SafePath_InvalidRootPath_ReturnsError()
        {
            var result = Validate.SafePath("Library/cache.dat", "path");

            Assert.IsNotNull(result);
            StringAssert.Contains("must start with Assets/ or Packages/", GetError(result));
        }

        [Test]
        public void SafePath_AbsoluteSystemPath_ReturnsError()
        {
            var result = Validate.SafePath("C:/Windows/System32/cmd.exe", "path");

            Assert.IsNotNull(result);
            StringAssert.Contains("must start with Assets/ or Packages/", GetError(result));
        }

        [Test]
        public void SafePath_DeleteRootAssets_ReturnsError()
        {
            var result = Validate.SafePath("Assets", "path", isDelete: true);

            Assert.IsNotNull(result);
            StringAssert.Contains("Cannot delete root", GetError(result));
        }

        [Test]
        public void SafePath_DeleteRootAssetsTrailingSlash_ReturnsError()
        {
            var result = Validate.SafePath("Assets/", "path", isDelete: true);

            Assert.IsNotNull(result);
            StringAssert.Contains("Cannot delete root", GetError(result));
        }

        [Test]
        public void SafePath_DeleteRootPackages_ReturnsError()
        {
            var result = Validate.SafePath("Packages", "path", isDelete: true);

            Assert.IsNotNull(result);
            StringAssert.Contains("Cannot delete root", GetError(result));
        }

        [Test]
        public void SafePath_DeleteSubfolder_ReturnsNull()
        {
            var result = Validate.SafePath("Assets/OldStuff", "path", isDelete: true);

            Assert.IsNull(result);
        }

        [Test]
        public void SafePath_BackslashNormalized_ValidPath_ReturnsNull()
        {
            var result = Validate.SafePath("Assets\\Scripts\\Main.cs", "path");

            Assert.IsNull(result);
        }

        [Test]
        public void SafePath_DoubleSlashNormalized_ValidPath_ReturnsNull()
        {
            var result = Validate.SafePath("Assets//Scripts//Main.cs", "path");

            Assert.IsNull(result);
        }

        [Test]
        public void SafePath_DotSlashPrefix_ValidPath_ReturnsNull()
        {
            var result = Validate.SafePath("./Assets/Scripts/Main.cs", "path");

            Assert.IsNull(result);
        }
    }
}
