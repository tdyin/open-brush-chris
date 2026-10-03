// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SpatialTracking;

namespace TiltBrush
{
    public class TestCHRISBimanualInput
    {
        sealed class FakeInput : ICHRISInputState
        {
            public readonly HashSet<string> Held = new HashSet<string>();
            public bool StopPressedThisFrame => false;
            public float WheelNotches { get; set; }
            public Vector2 MouseDelta { get; set; }
            public bool IsPressed(string input) => Held.Contains(input);
        }

        static string Backend => System.Environment.GetEnvironmentVariable("CHRIS_REPO") is string repo && repo.Length > 0
            ? repo : Path.GetFullPath("../chris");
        static string PresetPath => Path.Combine(Backend, "schemas/v0.1.2/openbrush_keyboard-mouse_two-hand.default.json");
        static CHRISInputMapping DirectPreset() => CHRISInputMapping.Load(Path.Combine(Backend,
            "schemas/v0.1.3/openbrush_keyboard-mouse_two-hand.default.json"));

        [Test]
        public void DirectPoseRollKeysRotateOppositelyWithoutMovingEitherHand()
        {
            var mapping = DirectPreset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            Vector3 brush = state.Brush.Position, wand = state.Wand.Position;
            input.Held.Add("key.r"); Step(state, mapping, input, dt: 0.1f);
            Quaternion rolled = state.Brush.Rotation;
            Assert.That((rolled * Vector3.right).y, Is.LessThan(0));
            Assert.That(Vector3.Angle(rolled * Vector3.forward, Vector3.forward), Is.LessThan(0.01f));
            Assert.That(state.Brush.Position, Is.EqualTo(brush));
            Assert.That(state.Wand.Position, Is.EqualTo(wand));
            Assert.That(state.Wand.Rotation, Is.EqualTo(Quaternion.identity));
            input.Held.Add("key.f"); Step(state, mapping, input, dt: 0.1f);
            Assert.That(Quaternion.Angle(state.Brush.Rotation, rolled), Is.LessThan(0.01f),
                "Opposite roll keys held together cancel");
            input.Held.Remove("key.r"); Step(state, mapping, input, dt: 0.1f);
            Assert.That(Quaternion.Angle(state.Brush.Rotation, Quaternion.identity), Is.LessThan(0.01f),
                "F rolls back through the same angle as R");
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f"); Step(state, mapping, input, dt: 0.1f);
            Assert.That((state.Wand.Rotation * Vector3.right).y, Is.GreaterThan(0));
            Assert.That(Quaternion.Angle(state.Brush.Rotation, Quaternion.identity), Is.LessThan(0.01f));
        }

        [Test]
        public void DirectPoseSharedCasesMatchBackendVerdicts()
        {
            string directory = Path.Combine(Backend, "tests/core/fixtures/mapping_v013");
            var cases = JArray.Parse(File.ReadAllText(Path.Combine(directory, "cases.json")));
            Assert.That(cases.Count, Is.GreaterThan(10));
            foreach (var item in cases)
            {
                string file = (string)item["file"], expected = (string)item["expect"];
                string actual;
                try { CHRISInputMapping.Load(Path.Combine(directory, file)); actual = "valid"; }
                catch (CHRISMappingException error) { actual = error.Code; }
                Assert.That(actual, Is.EqualTo(expected), file);
            }
            Assert.That(DirectPreset().SchemaVersion, Is.EqualTo(CHRISInputMapping.DirectPoseVersion));
        }

        [Test]
        public void DirectPoseMovesAndRotatesOnlyTheSelectedHandTogether()
        {
            var mapping = DirectPreset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            Vector3 original = state.Brush.Position, other = state.Wand.Position;
            input.Held.Add("key.w"); input.MouseDelta = Vector2.right * 10;
            Step(state, mapping, input);
            Assert.That(state.Brush.Position, Is.EqualTo(original), "Startup leaves physical control intact");
            Assert.That(state.Brush.Rotation, Is.EqualTo(Quaternion.identity));
            input.Held.Clear(); input.MouseDelta = Vector2.zero;
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.MouseDelta = new Vector2(10, 10);
            input.Held.Add("key.w"); input.Held.Add("key.d"); input.Held.Add("key.e");
            Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Position));
            Assert.That(state.Brush.Position.x, Is.GreaterThan(original.x));
            Assert.That(state.Brush.Position.y, Is.GreaterThan(original.y));
            Assert.That(state.Brush.Position.z, Is.GreaterThan(original.z));
            Vector3 facing = state.Brush.Rotation * Vector3.forward;
            Assert.That(facing.x, Is.GreaterThan(0), "D turns right");
            Assert.That(facing.y, Is.GreaterThan(0), "W tilts up");
            Assert.That(state.Wand.Position, Is.EqualTo(other));
            Assert.That(state.Wand.Rotation, Is.EqualTo(Quaternion.identity));
            Quaternion brushRotation = state.Brush.Rotation;
            input.Held.Clear(); input.MouseDelta = Vector2.zero; Step(state, mapping, input);
            input.Held.Add("key.2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.s"); input.Held.Add("key.a"); Step(state, mapping, input);
            facing = state.Wand.Rotation * Vector3.forward;
            Assert.That(facing.x, Is.LessThan(0), "A turns left");
            Assert.That(facing.y, Is.LessThan(0), "S tilts down");
            Assert.That(state.Brush.Rotation, Is.EqualTo(brushRotation));
        }

        [Test]
        public void DirectPoseArrowsMoveViewWithoutBrushStickOrWasdMovement()
        {
            var mapping = DirectPreset();
            var input = new FakeInput();
            var remap = new CHRISInputRemap();
            remap.Offer(mapping); remap.Tick(input, true, false);
            input.Held.Add("key.w"); remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero));
            input.Held.Clear(); input.Held.Add("key.upArrow"); remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.up));
            input.Held.Clear(); input.Held.Add("key.downArrow"); remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.down));
            input.Held.Clear(); input.Held.Add("key.leftArrow"); remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.left));
            input.Held.Clear(); input.Held.Add("key.rightArrow"); remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.right));
            remap.Tick(input, true, true);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "View motion is blocked during drawing");
            Assert.That(mapping.Find(CHRISMappedAction.StickAxis, "brush"), Is.Null);
            Assert.That(CHRISInputMappingHost.AllowsMappedConvenience(mapping, false), Is.False,
                "Menu and physical-control states must not move the viewpoint");
            Assert.That(CHRISInputMappingHost.MappingOwnsKeyboardShortcuts(mapping, false), Is.False);
            Assert.That(CHRISInputMappingHost.AllowsMappedConvenience(mapping, true), Is.True);
            Assert.That(CHRISInputMappingHost.MappingUsesCombinedTrigger(mapping), Is.False);
        }

        [Test]
        public void DirectPoseFocusAndHandBackRequireFreshControl()
        {
            var mapping = DirectPreset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.w"); Step(state, mapping, input);
            Quaternion rotated = state.Brush.Rotation;
            Step(state, mapping, input, focused: false);
            Step(state, mapping, input);
            Assert.That(state.Brush.Rotation, Is.EqualTo(rotated));
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Remove("key.f2"); Step(state, mapping, input);
            Assert.That(state.Brush.Rotation, Is.EqualTo(rotated), "Held W must be released before it resumes");
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.w"); Step(state, mapping, input);
            Assert.That(Quaternion.Angle(rotated, state.Brush.Rotation), Is.GreaterThan(0.1f));
            rotated = state.Brush.Rotation;
            input.Held.Add("key.f6"); Step(state, mapping, input);
            Assert.That(state.HandBackPending, Is.True);
            Assert.That(state.Brush.Rotation, Is.EqualTo(rotated));
            state.Stop();
            Assert.That(state.Active, Is.False);
            Assert.That(state.Brush.IsNeutral && state.Wand.IsNeutral, Is.True);
        }

        [Test]
        public void DirectPoseReloadFromLegacyRotationUsesCombinedPoseMode()
        {
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            var legacy = Preset();
            Step(state, legacy, input);
            input.Held.Add("key.f3"); Step(state, legacy, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Rotation));
            input.Held.Clear(); Step(state, legacy, input);
            var mapping = DirectPreset();
            Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Position));
            input.Held.Add("key.f3"); Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Position), "F3 is unassigned in the new preset");
            Assert.That(CHRISNativePopup.ModeLabel(mapping, state, false, true, true),
                Is.EqualTo("Pose · Brush (Right)"));
        }

        static CHRISInputMapping Preset()
        {
            var asset = Resources.Load<TextAsset>("CHRIS/TwoHandDefault");
            Assert.That(asset, Is.Not.Null, "Bundled preset must load in the player");
            return CHRISInputMapping.Parse(asset.bytes);
        }

        static void Step(CHRISBimanualInput state, CHRISInputMapping mapping, FakeInput input,
            bool focused = true, bool busy = false, Vector3? head = null, Vector3? forward = null,
            bool brushRight = true, float dt = 1f / 60f) =>
            state.Step(mapping, input, focused, busy, head ?? Vector3.zero, forward ?? Vector3.forward, brushRight, dt);

        [Test]
        public void BundledPresetMatchesBackendBytesAndSharedCases()
        {
            var asset = Resources.Load<TextAsset>("CHRIS/TwoHandDefault");
            Assert.That(asset, Is.Not.Null);
            Assert.That(File.Exists(PresetPath), Is.True, "Backend source preset missing: " + PresetPath);
            CollectionAssert.AreEqual(File.ReadAllBytes(PresetPath), asset.bytes, "Player preset must be byte-identical");
            Assert.That(CHRISInputMapping.Parse(asset.bytes).SchemaVersion, Is.EqualTo(CHRISInputMapping.BimanualVersion));

            string directory = Path.Combine(Backend, "tests/core/fixtures/mapping_v012");
            var cases = JArray.Parse(File.ReadAllText(Path.Combine(directory, "cases.json")));
            Assert.That(cases.Count, Is.GreaterThan(30));
            foreach (var item in cases)
            {
                string file = (string)item["file"], expected = (string)item["expect"];
                string actual;
                try { CHRISInputMapping.Load(Path.Combine(directory, file)); actual = "valid"; }
                catch (CHRISMappingException error) { actual = error.Code; }
                Assert.That(actual, Is.EqualTo(expected), file);
            }
        }

        [Test]
        public void HeadOriginPointerReachesNativePanelsWithoutChangingPhysicalReach()
        {
            Assert.That(CHRISBimanualHost.PanelRayReach(4f, false), Is.EqualTo(4f));
            Assert.That(CHRISBimanualHost.PanelRayReach(4f, true), Is.EqualTo(20f));
            Assert.That(CHRISBimanualHost.PanelRayReach(25f, true), Is.EqualTo(25f));
            Assert.That(CHRISBimanualHost.PanelRayReach(4f, true) * 1.5f, Is.EqualTo(30f),
                "The already-active native panel also uses the extended head-pointer reach");
        }

        [Test]
        public void IndependentHandsReleaseOnModeChangeAndKeepPoseAcrossBindingEdit()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.UI));
            input.Held.Add("key.f2"); Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Position));
            input.Held.Clear(); Step(state, mapping, input);

            input.Held.Add("key.space"); Step(state, mapping, input);
            Assert.That(state.Brush.Down(VrInput.Trigger), Is.True);
            Assert.That(state.Wand.Get(VrInput.Trigger), Is.False);
            input.Held.Add("key.h"); Step(state, mapping, input);
            Assert.That(state.Wand.GripHeld, Is.True);
            input.Held.Remove("key.h"); Step(state, mapping, input);
            Assert.That(state.Wand.GripHeld, Is.True, "Grip is an independent toggle");

            input.Held.Clear(); input.MouseDelta = new Vector2(30, 10); Step(state, mapping, input);
            Vector3 moved = state.Brush.Position;
            input.MouseDelta = Vector2.zero;
            var editedBindings = CHRISInputMapping.Parse(Resources.Load<TextAsset>("CHRIS/TwoHandDefault").bytes);
            Step(state, editedBindings, input);
            Assert.That(state.Brush.Position, Is.EqualTo(moved), "Binding edits must not recenter either hand");
            Assert.That(state.Wand.GripHeld, Is.False, "Binding edits release latched grips");

            input.Held.Add("key.f3"); Step(state, editedBindings, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Rotation));
            Assert.That(state.Wand.Up(VrInput.Grip), Is.False, "Release occurred on the binding-edit frame");
        }

        [Test]
        public void RecenterWaitsForCurrentInputsAndHandBackReleases()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            Vector3 original = state.Brush.Position;
            input.Held.Add("key.f5"); input.Held.Add("key.space");
            Step(state, mapping, input, head: new Vector3(30, 0, 0));
            Assert.That(state.RecenterPending, Is.True);
            Assert.That(state.Brush.Position, Is.EqualTo(original), "A newly held trigger defers recenter");
            input.Held.Clear(); Step(state, mapping, input, head: new Vector3(30, 0, 0));
            Assert.That(state.Brush.Position, Is.EqualTo(original), "Wait for the release edge");
            Step(state, mapping, input, head: new Vector3(30, 0, 0));
            Assert.That(state.Brush.Position.x, Is.GreaterThan(original.x + 20));

            input.Held.Add("key.space"); Step(state, mapping, input);
            input.Held.Add("key.f6"); Step(state, mapping, input);
            Assert.That(state.HandBackPending, Is.True);
            Assert.That(state.Brush.Up(VrInput.Trigger), Is.True);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f1"); Step(state, mapping, input);
            Assert.That(state.HandBackPending, Is.False, "F1 recovers the UI after hand-back");
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.UI));
        }

        [Test]
        public void ResumeAfterHandBackLatchesControlsUntilPhysicalGrabDrains()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f6"); Step(state, mapping, input);
            Assert.That(state.HandBackPending, Is.True);

            input.Held.Clear(); Step(state, mapping, input, busy: true);
            input.Held.Add("key.f2"); Step(state, mapping, input, busy: true);
            state.SuspendForEditor(input); // Host defers pose ownership while a native grab is busy.
            Assert.That(state.HandBackPending, Is.False);
            Assert.That(state.NeedsConvenienceRelease, Is.True);

            input.Held.Clear(); input.Held.Add("key.space");
            Step(state, mapping, input, busy: true);
            state.SuspendForEditor(input);
            Assert.That(state.Brush.Get(VrInput.Trigger), Is.False);
            Step(state, mapping, input);
            Assert.That(state.Brush.Get(VrInput.Trigger), Is.False, "Held trigger cannot fire on takeover");
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.space"); Step(state, mapping, input);
            Assert.That(state.Brush.Down(VrInput.Trigger), Is.True, "A fresh press works after takeover");
        }

        [Test]
        public void MouseMotionAndWheelDeferRecenterUntilNeutral()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f5"); input.MouseDelta = new Vector2(10, 0);
            Step(state, mapping, input, head: new Vector3(30, 0, 0));
            Assert.That(state.RecenterPending, Is.True);
            Assert.That(state.Brush.Position.x, Is.LessThan(10), "Movement does not teleport to the new head frame");
            input.MouseDelta = Vector2.zero;
            input.WheelNotches = 1;
            Step(state, mapping, input, head: new Vector3(30, 0, 0));
            Assert.That(state.RecenterPending, Is.True, "Wheel motion also defers recenter");
            input.Held.Clear(); input.WheelNotches = 0;
            Step(state, mapping, input, head: new Vector3(30, 0, 0));
            Assert.That(state.RecenterPending, Is.False);
        }

        [Test]
        public void FrameIsFixedWhileHeadTurnsAndOnlySelectedHandMoves()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            Assert.That(state.Brush.Position.x, Is.GreaterThan(0));
            Assert.That(state.Wand.Position.x, Is.LessThan(0));
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            Vector3 brushBefore = state.Brush.Position, wandBefore = state.Wand.Position;
            input.MouseDelta = new Vector2(20, 0);
            Step(state, mapping, input, head: new Vector3(50, 0, 0), forward: Vector3.right);
            Assert.That(state.Brush.Position, Is.EqualTo(brushBefore), "Selecting Wand preserves Brush pose");
            Assert.That(state.Wand.Position.x, Is.GreaterThan(wandBefore.x));
            Vector3 moved = state.Wand.Position;
            input.MouseDelta = Vector2.zero;
            Step(state, mapping, input, head: new Vector3(-50, 0, 0), forward: Vector3.left);
            Assert.That(state.Wand.Position, Is.EqualTo(moved), "Head motion alone never drifts the fixed frame");
            state.Stop();
            Step(state, mapping, input, brushRight: false);
            Assert.That(state.Brush.Position.x, Is.LessThan(0), "Reactivation follows handedness");
            Assert.That(state.Wand.Position.x, Is.GreaterThan(0));
        }

        [Test]
        public void BothGripTogglesAndFocusLossProduceOneReleaseEdge()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.g"); input.Held.Add("key.h"); Step(state, mapping, input);
            Assert.That(state.Brush.GripHeld && state.Wand.GripHeld, Is.True);
            Assert.That(state.Brush.Down(VrInput.Grip) && state.Wand.Down(VrInput.Grip), Is.True);
            Step(state, mapping, input);
            Assert.That(state.Brush.Down(VrInput.Grip) || state.Wand.Down(VrInput.Grip), Is.False);
            input.Held.Add("key.f3"); Step(state, mapping, input);
            Assert.That(state.Brush.Up(VrInput.Grip) && state.Wand.Up(VrInput.Grip), Is.True);
            Step(state, mapping, input);
            Assert.That(state.Brush.Up(VrInput.Grip) || state.Wand.Up(VrInput.Grip), Is.False);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.space"); Step(state, mapping, input);
            Assert.That(state.Brush.Down(VrInput.Trigger), Is.True);
            Step(state, mapping, input, focused: false);
            Assert.That(state.Brush.Up(VrInput.Trigger), Is.True);
            Step(state, mapping, input, focused: true);
            Assert.That(state.Brush.Get(VrInput.Trigger), Is.False, "Held-through-focus trigger stays latched");
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f3"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.space"); Step(state, mapping, input);
            Assert.That(state.Brush.Down(VrInput.Trigger), Is.True);
        }

        [Test]
        public void AnyEdgesIncludeStickAndWheelMotionIsFrameRateIndependent()
        {
            var hand = new CHRISVirtualHand();
            hand.Sample(false, false, false, false, false, Vector2.right);
            Assert.That(hand.Down(VrInput.Any), Is.True);
            hand.Sample(false, false, false, false, false, Vector2.right);
            Assert.That(hand.Down(VrInput.Any), Is.False);
            hand.Release();
            Assert.That(hand.Up(VrInput.Any), Is.True);

            var document = JObject.Parse(System.Text.Encoding.UTF8.GetString(
                Resources.Load<TextAsset>("CHRIS/TwoHandDefault").bytes));
            var rows = (JArray)document["mappings"];
            foreach (JObject row in rows)
            {
                if ((string)row["action"] == "brush_size") { row.Remove(); break; }
            }
            foreach (JObject row in rows)
                if ((string)row["action"] == "pose_depth" || (string)row["action"] == "rotate_roll")
                    row["source"] = new JObject { ["type"] = "mouse_wheel" };
            var mapping = CHRISInputMapping.Parse(System.Text.Encoding.UTF8.GetBytes(document.ToString()));
            var input = new FakeInput();
            var fast = new CHRISBimanualInput();
            var slow = new CHRISBimanualInput();
            Step(fast, mapping, input); Step(slow, mapping, input);
            input.Held.Add("key.f2"); Step(fast, mapping, input); Step(slow, mapping, input);
            input.Held.Clear(); Step(fast, mapping, input); Step(slow, mapping, input);
            input.WheelNotches = 1;
            Step(fast, mapping, input, dt: 1f / 30f);
            Step(slow, mapping, input, dt: 1f / 120f);
            Assert.That(fast.Brush.Position, Is.EqualTo(slow.Brush.Position));
            input.WheelNotches = 0;
            input.Held.Add("key.f3"); Step(fast, mapping, input); Step(slow, mapping, input);
            input.Held.Clear(); Step(fast, mapping, input); Step(slow, mapping, input);
            input.WheelNotches = 1;
            Step(fast, mapping, input, dt: 1f / 30f);
            Step(slow, mapping, input, dt: 1f / 120f);
            Assert.That(Quaternion.Angle(fast.Brush.Rotation, slow.Brush.Rotation), Is.LessThan(0.001f));
        }

        [Test]
        public void HandednessSwapRestoresBothOriginalPoseDrivers()
        {
            var left = new GameObject("CHRIS left driver test");
            var right = new GameObject("CHRIS right driver test");
            try
            {
                var a = left.AddComponent<TrackedPoseDriver>();
                var b = right.AddComponent<TrackedPoseDriver>();
                a.enabled = true;
                b.enabled = false;
                CHRISBimanualHost.RestoreDriverOwnership();
                CHRISBimanualHost.ReconcileDriverOwnership(a, b);
                Assert.That(a.enabled || b.enabled, Is.False);
                CHRISBimanualHost.ReconcileDriverOwnership(b, a);
                Assert.That(a.enabled || b.enabled, Is.False);
                CHRISBimanualHost.RestoreDriverOwnership();
                Assert.That(a.enabled, Is.True);
                Assert.That(b.enabled, Is.False);
            }
            finally
            {
                CHRISBimanualHost.RestoreDriverOwnership();
                UnityEngine.Object.DestroyImmediate(left);
                UnityEngine.Object.DestroyImmediate(right);
            }
        }

        [Test]
        public void UIModeClicksAndKeysDoNotPressVirtualTriggers()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.UI));
            input.Held.UnionWith(new[] { "mouse.left", "key.enter", "key.space" });
            Step(state, mapping, input);
            Step(state, mapping, input);
            Assert.That(state.Brush.Get(VrInput.Trigger) || state.Brush.Down(VrInput.Trigger), Is.False,
                "UI-mode mouse-left/Enter/Space must not draw");
            Assert.That(state.Wand.Get(VrInput.Trigger) || state.Wand.Down(VrInput.Trigger), Is.False);
        }

        [Test]
        public void VirtualHandIsClampedAroundItsOwnRecenterOrigin()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            input.Held.Clear(); Step(state, mapping, input);
            Vector3 brushOrigin = state.Brush.Origin, wandStart = state.Wand.Position;
            Assert.That(Vector3.Distance(state.Brush.Position, brushOrigin), Is.LessThan(1e-4f), "Origin is the recenter position");
            for (int i = 0; i < 200; i++) { input.MouseDelta = new Vector2(5000, 0); Step(state, mapping, input); }
            input.MouseDelta = Vector2.zero;
            Assert.That(Vector3.Distance(state.Brush.Position, brushOrigin),
                Is.LessThanOrEqualTo(CHRISBimanualInput.MaxHandOffsetUnits + 1e-3f), "Clamped to the generous radius");
            Assert.That(Vector3.Distance(state.Brush.Position, brushOrigin),
                Is.GreaterThan(CHRISBimanualInput.MaxHandOffsetUnits - 1e-3f), "Moves right up to the bound");
            Assert.That(state.Wand.Position, Is.EqualTo(wandStart), "Only the selected hand moves; the wand keeps its own origin");
            var clamped = CHRISBimanualInput.ClampToOrigin(new Vector3(100, 0, 0), new Vector3(1, 2, 3), 20);
            Assert.That(Vector3.Distance(clamped, new Vector3(1, 2, 3)), Is.EqualTo(20).Within(1e-4f));
        }

        [Test]
        public void SavedProfileStartsPhysicalUntilExplicitUIOrPoseTakeover()
        {
            var mapping = Preset();
            var input = new FakeInput();
            var state = new CHRISBimanualInput();
            Step(state, mapping, input); // Saved profile activation does not choose a control owner.
            Assert.That(state.Active, Is.True);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.UI));
            Assert.That(CHRISBimanualHost.WantsKeyboardUI(state.Active, state.Mode, false, state.HandBackPending), Is.False);
            Assert.That(CHRISBimanualHost.UsesVirtualHands(false, false), Is.False,
                "Both physical controllers retain their buttons, trigger and pointer at startup");

            var keyboardUI = new CHRISKeyboardUIOwnership();
            input.Held.Add("key.f1"); Step(state, mapping, input);
            keyboardUI.Request();
            keyboardUI.UpdateActive(true, true, false, true, false);
            Assert.That(keyboardUI.Granted, Is.False,
                "F1 waits for a physical stroke or grab to finish");
            keyboardUI.UpdateActive(true, true, false, false, false);
            Assert.That(keyboardUI.Granted, Is.True);
            Assert.That(CHRISBimanualHost.WantsKeyboardUI(state.Active, state.Mode,
                keyboardUI.Granted, state.HandBackPending), Is.True,
                "The same predicate routes virtual hands, the UI pointer and Activate after F1");

            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f6"); Step(state, mapping, input);
            keyboardUI.UpdateActive(true, true, state.HandBackPending, false, false);
            Assert.That(state.HandBackPending, Is.True);
            Assert.That(keyboardUI.Granted, Is.False);
            Assert.That(CHRISBimanualHost.WantsKeyboardUI(state.Active, state.Mode,
                keyboardUI.Granted, state.HandBackPending), Is.False,
                "F6 returns both the pointer and trigger even if Controls stays open");

            input.Held.Clear(); Step(state, mapping, input);
            input.Held.Add("key.f2"); Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Position));
            Assert.That(CHRISBimanualHost.UsesVirtualHands(false, false), Is.False,
                "Pose input waits for safe driver ownership");
            Assert.That(CHRISBimanualHost.UsesVirtualHands(false, true), Is.True);

            state.Stop();
            Step(state, mapping, new FakeInput());
            Assert.That(CHRISBimanualHost.WantsKeyboardUI(state.Active, state.Mode, false, state.HandBackPending), Is.False,
                "Restart with the same saved profile again starts physical");
            input.Held.Clear(); input.Held.Add("key.f3"); Step(state, mapping, input);
            Assert.That(state.Mode, Is.EqualTo(CHRISControlMode.Rotation));
        }

        [Test]
        public void StopLeavesPhysicalPointerUntilNewF1Recovery()
        {
            var keyboardUI = new CHRISKeyboardUIOwnership();
            keyboardUI.Request();
            keyboardUI.UpdateActive(true, true, false, false, false);
            Assert.That(keyboardUI.Granted, Is.True);
            keyboardUI.Release(); // Escape or StopMapping releases the explicit UI owner.
            keyboardUI.UpdateRecovery(true, false, false); // No popup is needed.
            Assert.That(keyboardUI.Granted, Is.False);
            Assert.That(CHRISBimanualHost.UsesVirtualHands(false, false), Is.False);

            keyboardUI.Request(); // First F1 without a profile.
            keyboardUI.UpdateRecovery(true, true, false);
            Assert.That(keyboardUI.Granted, Is.False, "Physical grab drains before recovery UI takes the ray");
            keyboardUI.Release(); // F6 cancels a request even before it is granted.
            keyboardUI.UpdateRecovery(true, false, false);
            Assert.That(keyboardUI.Granted, Is.False);
            keyboardUI.Request();
            keyboardUI.UpdateRecovery(true, false, false);
            Assert.That(keyboardUI.Granted, Is.True);
            keyboardUI.UpdateRecovery(false, false, false);
            Assert.That(keyboardUI.Granted, Is.False, "Focus loss returns to physical routing");
        }
    }
}
