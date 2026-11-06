using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Forward references in previews: an item of gameobject_create_batch may be parented to an earlier item of the same
    /// call, and inside <see cref="SkillPlanningService.BeginPendingObjectsScope"/> (the /skills/batch dry run) a step may
    /// name an object an earlier step would create, or a component an earlier step would add. Nothing exists during a
    /// preview, so without this both used to be rejected as "not found" and the caller fell back to one call per object.
    /// </summary>
    [TestFixture]
    public class PendingObjectsPlanningTests
    {
        private SurfaceProfileKind _savedProfile;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            // A name no scene object can share, so every lookup here is about predictions only.
            _root = "PendingRoot_" + Guid.NewGuid().ToString("N");
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        // ---------- inside one create_batch call ----------

        [Test]
        public void CreateBatch_ItemParentedToAnEarlierItem_IsValid()
        {
            var items = new JArray
            {
                new JObject { ["name"] = _root },
                new JObject { ["name"] = "Child", ["parentName"] = _root },
                new JObject { ["name"] = "Leaf", ["parentPath"] = _root + "/Child" },
            };

            var dry = DryRun("gameobject_create_batch", new JObject { ["items"] = items });

            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
            var predicted = ((JArray)dry["changes"]["create"]).Select(c => (string)c["predictedPath"]).ToArray();
            Assert.That(predicted, Is.EqualTo(new[] { _root, _root + "/Child", _root + "/Child/Leaf" }));
            Assert.That(StringArray(dry["validation"]?["warnings"]).Any(w => w.Contains("earlier step")), Is.False,
                "A parent created by this same call is its own intent, not something to warn about.");
        }

        [Test]
        public void CreateBatch_ParentNamedByALaterItem_IsInvalid()
        {
            var items = new JArray
            {
                new JObject { ["name"] = "Child", ["parentName"] = _root },
                new JObject { ["name"] = _root },
            };

            var dry = DryRun("gameobject_create_batch", new JObject { ["items"] = items });

            Assert.That(dry["valid"]?.Value<bool>(), Is.False, "Items run in order; a parent must come first.");
        }

        [Test]
        public void CreateBatch_FailedItem_IsNobodysParent()
        {
            var items = new JArray
            {
                new JObject { ["name"] = _root, ["primitiveType"] = "NoSuchPrimitive" },
                new JObject { ["name"] = "Child", ["parentName"] = _root },
            };

            var dry = DryRun("gameobject_create_batch", new JObject { ["items"] = items });

            var failedItems = ((JArray)dry["validation"]?["semanticErrors"] ?? new JArray())
                .Select(error => (string)error["field"])
                .Distinct()
                .OrderBy(field => field, StringComparer.Ordinal)
                .ToArray();
            Assert.That(failedItems, Is.EqualTo(new[] { "items[0]", "items[1]" }),
                "The failed item creates nothing, so the item naming it as parent fails too: " + dry.ToString(Formatting.None));
        }

        [Test]
        public void CreateBatch_ItemSpace_IsValidated()
        {
            var items = new JArray
            {
                new JObject { ["name"] = _root, ["space"] = "World", ["x"] = 1 },
                new JObject { ["name"] = "Child", ["space"] = "sideways" },
            };

            var dry = DryRun("gameobject_create_batch", new JObject { ["items"] = items });

            Assert.That(dry["valid"]?.Value<bool>(), Is.False);
            var created = (JArray)dry["changes"]["create"];
            Assert.That(created.Count, Is.EqualTo(1));
            Assert.That((string)created[0]["space"], Is.EqualTo("world"));
            Assert.That(created[0]["position"]?["x"]?.Value<float>(), Is.EqualTo(1f));
        }

        [Test]
        public void Create_StatesTheSpaceOfItsCoordinates()
        {
            var local = DryRun("gameobject_create", new JObject { ["name"] = _root, ["x"] = 2 });
            Assert.That((string)local["changes"]["create"][0]["space"], Is.EqualTo("local"), "Omitted space is the historic local.");

            var world = DryRun("gameobject_create", new JObject { ["name"] = _root, ["space"] = "world", ["rotY"] = 90 });
            Assert.That(world["valid"]?.Value<bool>(), Is.True, world.ToString(Formatting.None));
            Assert.That((string)world["changes"]["create"][0]["space"], Is.EqualTo("world"));
            Assert.That(world["changes"]["create"][0]["rotation"]?["y"]?.Value<float>(), Is.EqualTo(90f));

            var bad = DryRun("gameobject_create", new JObject { ["name"] = _root, ["space"] = "sideways" });
            Assert.That(bad["valid"]?.Value<bool>(), Is.False);
            Assert.That(((JArray)bad["validation"]["semanticErrors"]).Any(e => (string)e["field"] == "space"), Is.True,
                bad.ToString(Formatting.None));
        }

        // ---------- across the steps of one dry run ----------

        [Test]
        public void Scope_LaterStepsCanNameWhatAnEarlierStepCreates()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var create = DryRun("gameobject_create", new JObject { ["name"] = _root });
                Assert.That(create["valid"]?.Value<bool>(), Is.True, create.ToString(Formatting.None));

                var addComponent = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
                Assert.That(addComponent["valid"]?.Value<bool>(), Is.True, addComponent.ToString(Formatting.None));
                Assert.That(StringArray(addComponent["validation"]?["warnings"]).Any(w => w.Contains(_root) && w.Contains("earlier step")),
                    Is.True, "The acceptance must say what it rests on.");

                var child = DryRun("gameobject_create", new JObject { ["name"] = "Child", ["parentName"] = _root });
                Assert.That(child["valid"]?.Value<bool>(), Is.True, child.ToString(Formatting.None));
                Assert.That((string)child["changes"]["create"][0]["predictedPath"], Is.EqualTo(_root + "/Child"));

                var byPath = DryRun("gameobject_rename", new JObject { ["path"] = _root + "/Child", ["newName"] = "Renamed" });
                Assert.That(byPath["valid"]?.Value<bool>(), Is.True, byPath.ToString(Formatting.None));
            }
        }

        [Test]
        public void Scope_StillRunsTheChecksThatNeedNoLiveObject()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("gameobject_create", new JObject { ["name"] = _root });

                var badProperty = DryRun("component_set_property", new JObject
                {
                    ["name"] = _root,
                    ["componentType"] = "BoxCollider",
                    ["propertyName"] = "noSuchMember",
                    ["value"] = "1"
                });
                Assert.That(badProperty["valid"]?.Value<bool>(), Is.False,
                    "The property is checked on the component type, which needs no live object: " + badProperty.ToString(Formatting.None));

                var badType = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "NoSuchComponentType" });
                Assert.That(badType["valid"]?.Value<bool>(), Is.False);
            }
        }

        [Test]
        public void Scope_InvalidStepRegistersNothing()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var create = DryRun("gameobject_create", new JObject { ["name"] = _root, ["primitiveType"] = "NoSuchPrimitive" });
                Assert.That(create["valid"]?.Value<bool>(), Is.False);

                var addComponent = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
                Assert.That(addComponent["valid"]?.Value<bool>(), Is.False, "Execution would reject the create, so nothing exists to name.");
            }
        }

        [Test]
        public void Scope_PlanRegistersToo()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var plan = JObject.Parse(SkillRouter.Plan("gameobject_create", new JObject { ["name"] = _root }.ToString(Formatting.None)));
                Assert.That(plan["valid"]?.Value<bool>(), Is.True, plan.ToString(Formatting.None));

                var addComponent = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
                Assert.That(addComponent["valid"]?.Value<bool>(), Is.True, addComponent.ToString(Formatting.None));
            }
        }

        [Test]
        public void NestedScope_ReusesTheOuterSet()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("gameobject_create", new JObject { ["name"] = _root });
                using (SkillPlanningService.BeginPendingObjectsScope())
                {
                    Assert.That(DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" })["valid"]?.Value<bool>(),
                        Is.True, "An inner scope sees what the outer one registered.");
                }

                Assert.That(DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" })["valid"]?.Value<bool>(),
                    Is.True, "Disposing the inner scope must not end the outer one.");
            }
        }

        [Test]
        public void OutsideAScope_BehaviourIsUnchanged()
        {
            DryRun("gameobject_create", new JObject { ["name"] = _root });
            var before = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
            Assert.That(before["valid"]?.Value<bool>(), Is.False, "Without a scope a preview registers nothing.");

            using (SkillPlanningService.BeginPendingObjectsScope())
                DryRun("gameobject_create", new JObject { ["name"] = _root });

            var after = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
            Assert.That(after["valid"]?.Value<bool>(), Is.False, "Predictions end with their scope.");
        }

        [Test]
        public void Scope_ALiveObjectWinsOverAPrediction()
        {
            var live = new GameObject(_root);
            try
            {
                GameObjectFinder.InvalidateCache();
                using (SkillPlanningService.BeginPendingObjectsScope())
                {
                    DryRun("gameobject_create", new JObject { ["name"] = _root });
                    var addComponent = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });

                    Assert.That(addComponent["valid"]?.Value<bool>(), Is.True, addComponent.ToString(Formatting.None));
                    Assert.That(StringArray(addComponent["validation"]?["warnings"]).Any(w => w.Contains("earlier step")), Is.False,
                        "An exact live match is what execution finds first, so the preview checks it rather than the prediction.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(live);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void Scope_ANameWithASlashMatchesAPendingPath()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("gameobject_create", new JObject { ["name"] = _root });
                DryRun("gameobject_create", new JObject { ["name"] = "Child", ["parentName"] = _root });

                var byPathAsName = DryRun("component_add", new JObject { ["name"] = _root + "/Child", ["componentType"] = "BoxCollider" });
                Assert.That(byPathAsName["valid"]?.Value<bool>(), Is.True,
                    "Execution reads a '/'-name that names no object as a path: " + byPathAsName.ToString(Formatting.None));
                Assert.That(StringArray(byPathAsName["validation"]?["warnings"]).Any(w => w.Contains(_root + "/Child") && w.Contains("earlier step")),
                    Is.True, byPathAsName.ToString(Formatting.None));

                var missing = DryRun("component_add", new JObject { ["name"] = _root + "/Missing", ["componentType"] = "BoxCollider" });
                Assert.That(missing["valid"]?.Value<bool>(), Is.False, "A path nothing predicts is still not found.");
            }
        }

        // ---------- creates whose preview predicts no object ----------

        [Test]
        public void Scope_AnUnplannedCreateIsNameableByLaterSteps()
        {
            var light = _root + "_Key";
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("gameobject_create", new JObject { ["name"] = _root });
                var create = DryRun("light_create", new JObject { ["name"] = light });
                Assert.That(create["valid"]?.Value<bool>(), Is.True, create.ToString(Formatting.None));

                var setParent = DryRun("gameobject_set_parent", new JObject { ["childName"] = light, ["parentName"] = _root });
                Assert.That(setParent["valid"]?.Value<bool>(), Is.True,
                    "light_create's preview has no create entry, yet execution makes the light: " + setParent.ToString(Formatting.None));
                Assert.That(StringArray(setParent["validation"]?["warnings"]).Any(w => w.Contains($"child '{light}'") && w.Contains("earlier step")),
                    Is.True, setParent.ToString(Formatting.None));
            }

            DryRun("light_create", new JObject { ["name"] = light });
            var outside = DryRun("gameobject_set_parent", new JObject { ["childName"] = light });
            Assert.That(outside["valid"]?.Value<bool>(), Is.False, "Without a scope a preview registers nothing.");
        }

        [Test]
        public void Scope_AnUnplannedCreateUnderAParent_IsNameableByItsPredictedPath()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("ui_create_canvas", new JObject { ["name"] = _root });
                var panel = DryRun("ui_create_panel", new JObject { ["name"] = "Panel", ["parent"] = _root });
                Assert.That(panel["valid"]?.Value<bool>(), Is.True, panel.ToString(Formatting.None));

                var byPath = DryRun("gameobject_rename", new JObject { ["path"] = _root + "/Panel", ["newName"] = "Renamed" });
                Assert.That(byPath["valid"]?.Value<bool>(), Is.True, byPath.ToString(Formatting.None));
                Assert.That(StringArray(byPath["validation"]?["warnings"]).Any(w => w.Contains(_root + "/Panel") && w.Contains("earlier step")),
                    Is.True, byPath.ToString(Formatting.None));
            }
        }

        [Test]
        public void Scope_AParentlessUiCreate_IsNameableByNameOnly()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("ui_create_panel", new JObject { ["name"] = _root });

                var byName = DryRun("gameobject_rename", new JObject { ["name"] = _root, ["newName"] = "Renamed" });
                Assert.That(byName["valid"]?.Value<bool>(), Is.True, byName.ToString(Formatting.None));

                var byPath = DryRun("gameobject_rename", new JObject { ["path"] = _root, ["newName"] = "Renamed" });
                Assert.That(byPath["valid"]?.Value<bool>(), Is.False,
                    "A UI element without a parent goes under a Canvas, so no path is predicted for it.");
            }
        }

        [Test]
        public void Scope_AChildOfANameOnlyParent_IsNameableByNameOnly()
        {
            var button = _root + "_Button";
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("ui_create_panel", new JObject { ["name"] = _root });
                var create = DryRun("ui_create_button", new JObject { ["name"] = button, ["parent"] = _root });
                Assert.That(create["valid"]?.Value<bool>(), Is.True, create.ToString(Formatting.None));

                var byPath = DryRun("gameobject_rename", new JObject { ["path"] = _root + "/" + button, ["newName"] = "Renamed" });
                Assert.That(byPath["valid"]?.Value<bool>(), Is.False,
                    "The panel goes under a Canvas, so 'Panel/Button' is a path that will not exist: " + byPath.ToString(Formatting.None));

                var byName = DryRun("gameobject_rename", new JObject { ["name"] = button, ["newName"] = "Renamed" });
                Assert.That(byName["valid"]?.Value<bool>(), Is.True, byName.ToString(Formatting.None));
                Assert.That(StringArray(byName["validation"]?["warnings"]).Any(w => w.Contains(button) && w.Contains("earlier step")),
                    Is.True, byName.ToString(Formatting.None));
            }
        }

        [Test]
        public void Scope_APredictedCreateUnderANameOnlyParent_IsNameableByNameOnly()
        {
            var child = _root + "_Child";
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                DryRun("ui_create_panel", new JObject { ["name"] = _root });
                var create = DryRun("gameobject_create", new JObject { ["name"] = child, ["parentName"] = _root });
                Assert.That(create["valid"]?.Value<bool>(), Is.True, create.ToString(Formatting.None));

                var byPath = DryRun("gameobject_rename", new JObject { ["path"] = _root + "/" + child, ["newName"] = "Renamed" });
                Assert.That(byPath["valid"]?.Value<bool>(), Is.False,
                    "gameobject_create predicts '<panel>/<child>', but the panel goes under a Canvas: " + byPath.ToString(Formatting.None));

                var byName = DryRun("gameobject_rename", new JObject { ["name"] = child, ["newName"] = "Renamed" });
                Assert.That(byName["valid"]?.Value<bool>(), Is.True, byName.ToString(Formatting.None));
                Assert.That(StringArray(byName["validation"]?["warnings"]).Any(w => w.Contains(child) && w.Contains("earlier step")),
                    Is.True, byName.ToString(Formatting.None));
            }
        }

        [Test]
        public void Scope_ComponentAddOnAMissingObject_DoesNotMakeItPending()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var add = DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
                Assert.That(add["valid"]?.Value<bool>(), Is.False, add.ToString(Formatting.None));

                var rename = DryRun("gameobject_rename", new JObject { ["name"] = _root, ["newName"] = "Renamed" });
                Assert.That(rename["valid"]?.Value<bool>(), Is.False, "component_add's name locates its target; it creates no object.");
            }
        }

        [Test]
        public void Scope_AnAssetCreate_RegistersNoGameObject()
        {
            using (SkillPlanningService.BeginPendingObjectsScope())
            {
                var material = DryRun("material_create", new JObject { ["name"] = _root });
                Assert.That(material["valid"]?.Value<bool>(), Is.True, material.ToString(Formatting.None));

                var rename = DryRun("gameobject_rename", new JObject { ["name"] = _root, ["newName"] = "Renamed" });
                Assert.That(rename["valid"]?.Value<bool>(), Is.False, "material_create names an asset, not a scene object.");
            }
        }

        // ---------- components an earlier step adds ----------

        [Test]
        public void Scope_SetPropertyOnAComponentAnEarlierStepAdds_IsValidWithAWarning()
        {
            var live = new GameObject(_root);
            try
            {
                GameObjectFinder.InvalidateCache();
                var entityId = UnityObjectIdUtility.GetEntityId(live);
                using (SkillPlanningService.BeginPendingObjectsScope())
                {
                    var add = DryRun("component_add", new JObject { ["entityId"] = entityId, ["componentType"] = "BoxCollider" });
                    Assert.That(add["valid"]?.Value<bool>(), Is.True, add.ToString(Formatting.None));

                    var set = DryRun("component_set_property", new JObject
                    {
                        ["entityId"] = entityId,
                        ["componentType"] = "BoxCollider",
                        ["propertyName"] = "isTrigger",
                        ["value"] = "true"
                    });
                    Assert.That(set["valid"]?.Value<bool>(), Is.True, set.ToString(Formatting.None));
                    Assert.That(StringArray(set["validation"]?["warnings"]).Any(w => w.Contains("BoxCollider") && w.Contains("added by an earlier step")),
                        Is.True, "The acceptance must say what it rests on: " + set.ToString(Formatting.None));

                    var byBaseType = DryRun("component_set_property", new JObject
                    {
                        ["name"] = _root,
                        ["componentType"] = "Collider",
                        ["propertyName"] = "isTrigger",
                        ["value"] = "true"
                    });
                    Assert.That(byBaseType["valid"]?.Value<bool>(), Is.True,
                        "GetComponent(Collider) finds the BoxCollider the earlier step adds: " + byBaseType.ToString(Formatting.None));

                    var badMember = DryRun("component_set_property", new JObject
                    {
                        ["name"] = _root,
                        ["componentType"] = "BoxCollider",
                        ["propertyName"] = "noSuchMember",
                        ["value"] = "1"
                    });
                    Assert.That(badMember["valid"]?.Value<bool>(), Is.False, "The member is still checked on the type.");
                }

                Assert.That(live.GetComponent<BoxCollider>(), Is.Null, "A dry run adds nothing.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(live);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void Scope_SetPropertyOnAComponentAnEarlierAddRequires_IsValidWithAWarning()
        {
            var live = new GameObject(_root);
            try
            {
                GameObjectFinder.InvalidateCache();
                using (SkillPlanningService.BeginPendingObjectsScope())
                {
                    DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "HingeJoint" });

                    var set = DryRun("component_set_property", new JObject
                    {
                        ["name"] = _root,
                        ["componentType"] = "Rigidbody",
                        ["propertyName"] = "mass",
                        ["value"] = "2"
                    });
                    Assert.That(set["valid"]?.Value<bool>(), Is.True,
                        "Adding a HingeJoint brings the Rigidbody it requires: " + set.ToString(Formatting.None));
                    Assert.That(StringArray(set["validation"]?["warnings"]).Any(w => w.Contains("Rigidbody") && w.Contains("added by an earlier step")),
                        Is.True, set.ToString(Formatting.None));

                    var remove = DryRun("component_remove", new JObject { ["name"] = _root, ["componentType"] = "Rigidbody" });
                    Assert.That(remove["valid"]?.Value<bool>(), Is.False, "The HingeJoint that brings it also blocks its removal.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(live);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void OutsideAScope_SetPropertyOnAMissingComponent_StillFails()
        {
            var live = new GameObject(_root);
            try
            {
                GameObjectFinder.InvalidateCache();
                DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });

                var set = DryRun("component_set_property", new JObject
                {
                    ["name"] = _root,
                    ["componentType"] = "BoxCollider",
                    ["propertyName"] = "isTrigger",
                    ["value"] = "true"
                });
                Assert.That(set["valid"]?.Value<bool>(), Is.False, "Without a scope a preview registers nothing.");
                Assert.That(((JArray)set["validation"]["semanticErrors"]).Any(e => (string)e["field"] == "component"), Is.True,
                    set.ToString(Formatting.None));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(live);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void Scope_AComponentAddedToOneObject_DoesNotCoverAnother()
        {
            var first = new GameObject(_root + "_A");
            var second = new GameObject(_root + "_B");
            try
            {
                GameObjectFinder.InvalidateCache();
                using (SkillPlanningService.BeginPendingObjectsScope())
                {
                    DryRun("component_add", new JObject { ["name"] = first.name, ["componentType"] = "BoxCollider" });

                    var set = DryRun("component_set_property", new JObject
                    {
                        ["name"] = second.name,
                        ["componentType"] = "BoxCollider",
                        ["propertyName"] = "isTrigger",
                        ["value"] = "true"
                    });
                    Assert.That(set["valid"]?.Value<bool>(), Is.False, "The pending component belongs to the other object.");
                    Assert.That(((JArray)set["validation"]["semanticErrors"]).Any(e => (string)e["field"] == "component"), Is.True,
                        set.ToString(Formatting.None));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void Scope_RemoveAComponentAnEarlierStepAdds_IsValidOnceOnly()
        {
            var live = new GameObject(_root);
            try
            {
                GameObjectFinder.InvalidateCache();
                using (SkillPlanningService.BeginPendingObjectsScope())
                {
                    DryRun("component_add", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });

                    var remove = DryRun("component_remove", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" });
                    Assert.That(remove["valid"]?.Value<bool>(), Is.True, remove.ToString(Formatting.None));
                    Assert.That(StringArray(remove["validation"]?["warnings"]).Any(w => w.Contains("added by an earlier step")), Is.True,
                        remove.ToString(Formatting.None));

                    var outOfRange = DryRun("component_remove", new JObject { ["name"] = _root, ["componentType"] = "BoxCollider", ["componentIndex"] = 1 });
                    Assert.That(((JArray)outOfRange["validation"]["semanticErrors"]).Any(e => (string)e["field"] == "componentIndex"), Is.True,
                        "One earlier add makes one component: " + outOfRange.ToString(Formatting.None));

                    var items = new JArray
                    {
                        new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" },
                        new JObject { ["name"] = _root, ["componentType"] = "BoxCollider" },
                    };
                    var batch = DryRun("component_remove_batch", new JObject { ["items"] = items });
                    var failedItems = ((JArray)batch["validation"]?["semanticErrors"] ?? new JArray())
                        .Select(error => (string)error["field"])
                        .Distinct()
                        .ToArray();
                    Assert.That(failedItems, Is.EqualTo(new[] { "items[1]" }),
                        "The first item removes the added component, so the second finds none: " + batch.ToString(Formatting.None));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(live);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- helpers ----------

        private static JObject DryRun(string skill, JObject body)
        {
            Assume.That(SkillRouter.HasSkill(skill), Is.True, $"{skill} is not registered.");
            var response = JObject.Parse(SkillRouter.DryRun(skill, body.ToString(Formatting.None)));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("dryRun"), response.ToString(Formatting.None));
            return response;
        }

        private static string[] StringArray(JToken token) =>
            (token as JArray)?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();
    }
}

// Producer:Betsy
