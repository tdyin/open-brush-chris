// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace TiltBrush
{
    public class TestCHRISInputRemap
    {
        sealed class FakeInput : ICHRISInputState
        {
            public readonly HashSet<string> Held = new HashSet<string>();
            public bool StopPressedThisFrame { get; set; }
            public bool IsPressed(string input) => Held.Contains(input);
            public float WheelNotches { get; set; }
            public Vector2 MouseDelta { get; set; }
        }

        // Every step 3 action: keys draw/undo/view, wheel size.
        static CHRISInputMapping AllKeys() => Mapping("{\"type\":\"key\",\"key\":\"space\"}",
            ",{\"id\":\"undo\",\"source\":{\"type\":\"key\",\"key\":\"z\"},\"action\":\"undo\"}" +
            ",{\"id\":\"size\",\"source\":{\"type\":\"mouse_wheel\"},\"action\":\"brush_size\"}" +
            ",{\"id\":\"view\",\"source\":{\"type\":\"key_vector2\",\"up\":\"w\",\"down\":\"s\",\"left\":\"a\",\"right\":\"d\"},\"action\":\"move_view\"}");

        static CHRISInputMapping Mapping(string drawSource, string extra = "") => CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(
            "{\"version\":\"v0.1.1\",\"app\":\"openbrush\",\"profile\":\"keyboard_mouse\",\"mappings\":[" +
            "{\"id\":\"draw\",\"source\":" + drawSource + ",\"action\":\"draw\"}" + extra + "]}"));
        static CHRISInputMapping Space() => Mapping("{\"type\":\"key\",\"key\":\"space\"}",
            ",{\"id\":\"undo\",\"source\":{\"type\":\"key\",\"key\":\"z\"},\"action\":\"undo\"}");
        static CHRISInputMapping LeftButton() => Mapping("{\"type\":\"mouse_button\",\"button\":\"left\"}");

        [Test]
        public void RealKeyboardDigitControlsSelectBothHands()
        {
            var previous = Keyboard.current;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            keyboard.MakeCurrent();
            try
            {
                var digits = new[] { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4,
                    Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
                for (int i = 0; i < digits.Length; i++)
                {
                    Assert.That(keyboard[digits[i]].name, Is.EqualTo(i.ToString()),
                        "Binding capture must save the actual Input System control name");
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(digits[i]));
                    InputSystem.Update();
                    Assert.That(CHRISDeviceInput.Instance.IsPressed("key." + i), Is.True,
                        "The production adapter must resolve the schema digit " + i);
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                    InputSystem.Update();
                    Assert.That(CHRISDeviceInput.Instance.IsPressed("key." + i), Is.False);
                }

                var asset = Resources.Load<TextAsset>("CHRIS/TwoHandDefault");
                var mapping = CHRISInputMapping.Parse(asset.bytes);
                var hand = new CHRISBimanualInput();
                void StepHand() => hand.Step(mapping, CHRISDeviceInput.Instance, true, false,
                    Vector3.zero, Vector3.forward, true, 1f / 60f);
                StepHand();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.F2));
                InputSystem.Update();
                StepHand();
                Assert.That(hand.Mode, Is.EqualTo(CHRISControlMode.Position));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.Update();
                StepHand();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit2));
                InputSystem.Update();
                StepHand();
                Assert.That(hand.Selected, Is.EqualTo("wand"), "2 selects the left Wand in the default layout");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.Update();
                StepHand();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit1));
                InputSystem.Update();
                StepHand();
                Assert.That(hand.Selected, Is.EqualTo("brush"), "1 selects Brush");
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                if (previous != null) previous.MakeCurrent();
            }
        }

        [Test]
        public void RecoveryCaptureSuppressesMappedInputsAndNativeShortcuts()
        {
            Assert.That(CHRISInputMappingHost.ShouldOwnKeyboardShortcuts(false, true, true), Is.True,
                "No active profile still owns native shortcuts during F1 recovery capture");
            Assert.That(CHRISInputMappingHost.ShouldOwnKeyboardShortcuts(false, false, false), Is.False);
            var (remap, input) = Activated(AllKeys());
            input.Held.Add("key.space"); input.Held.Add("key.z"); input.Held.Add("key.w");
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld, Is.True);
            remap.SuppressForEditor(input);
            Assert.That(remap.DrawHeld || remap.UndoPressed || remap.ViewKeys != Vector2.zero, Is.False,
                "The captured press cannot also draw, undo, or move the view");
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld || remap.UndoPressed || remap.ViewKeys != Vector2.zero, Is.False,
                "Held captured inputs stay latched until release");
        }

        [Test]
        public void LoadedTwoHandProfileLeavesNativeShortcutsAndConvenienceUntilTakeover()
        {
            var bimanual = CHRISInputMapping.Parse(Resources.Load<TextAsset>("CHRIS/TwoHandDefault").bytes);
            Assert.That(CHRISInputMappingHost.MappingOwnsKeyboardShortcuts(bimanual, false), Is.False);
            Assert.That(CHRISInputMappingHost.AllowsMappedConvenience(bimanual, false), Is.False,
                "Mapped undo, wheel and view movement remain dormant during physical control");
            Assert.That(CHRISInputMappingHost.MappingOwnsKeyboardShortcuts(bimanual, true), Is.True);
            Assert.That(CHRISInputMappingHost.AllowsMappedConvenience(bimanual, true), Is.True);
            Assert.That(CHRISInputMappingHost.MappingUsesCombinedTrigger(bimanual), Is.False,
                "An unclaimed two-hand profile must preserve the native controller trigger path");
            Assert.That(CHRISInputMappingHost.MappingOwnsKeyboardShortcuts(AllKeys(), false), Is.True,
                "Legacy mapping still owns its shortcuts");
            Assert.That(CHRISInputMappingHost.AllowsMappedConvenience(AllKeys(), false), Is.True);
            Assert.That(CHRISInputMappingHost.MappingUsesCombinedTrigger(AllKeys()), Is.True);
        }

        [Test]
        public void BimanualWheelWorksInPoseModeAndUICarryIsDiscarded()
        {
            var asset = Resources.Load<TextAsset>("CHRIS/TwoHandDefault");
            var mapping = CHRISInputMapping.Parse(asset.bytes);
            var (remap, input) = Activated(mapping);
            input.WheelNotches = 0.6f;
            remap.Tick(input, true, false);
            remap.SuppressBimanualConvenience(input);
            Assert.That(remap.SizeNotches, Is.Zero);
            input.WheelNotches = 0.6f;
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.Zero, "UI fractional wheel does not carry into pose mode");
            input.WheelNotches = 0.5f;
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(1), "Wheel applies brush size while in a pose mode");
            input.WheelNotches = 0;
            input.Held.Add("key.w");
            remap.Tick(input, true, false);
            remap.SuppressBimanualConvenience(input);
            remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "Held view key needs release after UI mode");
        }

        static (CHRISInputRemap, FakeInput) Activated(CHRISInputMapping mapping)
        {
            var remap = new CHRISInputRemap();
            var input = new FakeInput();
            remap.Offer(mapping);
            remap.Tick(input, true, false);
            Assert.That(remap.Active, Is.SameAs(mapping));
            return (remap, input);
        }

        [Test]
        public void PressStartsAndReleaseEndsDraw()
        {
            var (remap, input) = Activated(Space());
            Assert.That(remap.DrawHeld, Is.False);
            input.Held.Add("key.space");
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld && remap.DrawDown && !remap.DrawUp, Is.True);
            remap.Tick(input, true, true);
            Assert.That(remap.DrawHeld && !remap.DrawDown, Is.True, "Down is one frame only");
            input.Held.Remove("key.space");
            remap.Tick(input, true, true);
            Assert.That(!remap.DrawHeld && remap.DrawUp, Is.True);
            input.Held.Add("key.z");
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld, Is.False, "Only the draw input draws");
        }

        [Test]
        public void FocusLossEndsDrawAndHeldKeyNeedsAFreshPress()
        {
            var (remap, input) = Activated(Space());
            input.Held.Add("key.space");
            remap.Tick(input, true, false);
            remap.Tick(input, false, true);
            Assert.That(!remap.DrawHeld && remap.DrawUp, Is.True, "Focus loss releases the stroke");
            remap.Tick(input, false, false);
            Assert.That(remap.DrawHeld, Is.False);
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld, Is.False, "Refocus with the key still held does not resume");
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld, Is.False);
            input.Held.Remove("key.space");
            remap.Tick(input, true, false);
            input.Held.Add("key.space");
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld && remap.DrawDown, Is.True, "A fresh press draws again");
            Assert.That(remap.Active, Is.Not.Null, "Focus loss does not deactivate the mapping");
        }

        [Test]
        public void StopReleasesAndDeactivates()
        {
            var (remap, input) = Activated(LeftButton());
            input.Held.Add("mouse.left");
            remap.Tick(input, true, false);
            input.StopPressedThisFrame = true;
            remap.Tick(input, true, true);
            Assert.That(!remap.DrawHeld && remap.DrawUp, Is.True);
            Assert.That(remap.Active, Is.Null);
            input.StopPressedThisFrame = false;
            remap.Tick(input, true, false);
            Assert.That(remap.DrawHeld || remap.DrawUp, Is.False, "Nothing draws after Stop");
        }

        [Test]
        public void CombinedTriggerEdgesFollowBothSources()
        {
            var trigger = new CHRISCombinedTrigger();
            // Physical trigger held over a panel (not painting) before and while a mapping activates.
            trigger.Sample(1, true, false);
            trigger.Sample(2, true, false);
            Assert.That(trigger.Held && !trigger.Down, Is.True, "No false press for an already held trigger");
            trigger.Sample(3, true, true);
            Assert.That(trigger.Held && !trigger.Down && !trigger.Up, Is.True, "Mapped draw joins without a new press");
            trigger.Sample(3, false, false);
            Assert.That(trigger.Held, Is.True, "One sample per frame");
            trigger.Sample(4, false, true);
            Assert.That(trigger.Held && !trigger.Up, Is.True, "Releasing one source keeps the stroke");
            trigger.Sample(5, false, false);
            Assert.That(!trigger.Held && trigger.Up, Is.True);
            trigger.Sample(6, false, true);
            Assert.That(trigger.Down, Is.True);
        }

        [Test]
        public void PhysicalTriggerHeldThroughStopRequiresReleaseBeforeDrawingAgain()
        {
            var gate = new CHRISPhysicalTriggerGate();
            var trigger = new CHRISCombinedTrigger();
            trigger.Sample(1, gate.Filter(true, true), false);
            Assert.That(trigger.Down, Is.True);

            gate.RequireRelease();
            trigger.Sample(2, gate.Filter(true, true), false);
            Assert.That(trigger.Up, Is.True, "Stop ends the physical stroke while the trigger is held");
            trigger.Sample(3, gate.Filter(true, true), false);
            Assert.That(trigger.Held, Is.False, "Holding through Stop cannot restart drawing");

            trigger.Sample(4, gate.Filter(false, true), false);
            Assert.That(gate.WaitingForRelease, Is.False);
            trigger.Sample(5, gate.Filter(true, true), false);
            Assert.That(trigger.Down, Is.True, "A fresh physical press works after release");
        }

        [Test]
        public void XrFocusLossReleasesAndRequiresFreshMappedAndPhysicalPresses()
        {
            Assert.That(CHRISInputMappingHost.InputFocused(true, false), Is.False,
                "The headset losing focus is enough even while the desktop stays focused");
            Assert.That(CHRISInputMappingHost.InputFocused(false, true), Is.False);
            Assert.That(CHRISInputMappingHost.InputFocused(true, true), Is.True);

            var (remap, input) = Activated(Space());
            var gate = new CHRISPhysicalTriggerGate();
            input.Held.Add("key.space");
            remap.Tick(input, true, false);
            Assert.That(gate.Filter(true, true), Is.True);
            remap.Tick(input, CHRISInputMappingHost.InputFocused(true, false), true);
            Assert.That(gate.Filter(true, false), Is.False);
            Assert.That(remap.DrawUp, Is.True);
            remap.Tick(input, true, false);
            Assert.That(gate.Filter(true, true), Is.False);
            Assert.That(remap.DrawHeld, Is.False, "A key held through headset focus loss remains latched");
            input.Held.Clear();
            remap.Tick(input, true, false);
            Assert.That(gate.Filter(false, true), Is.False);
            input.Held.Add("key.space");
            remap.Tick(input, true, false);
            Assert.That(gate.Filter(true, true), Is.True);
            Assert.That(remap.DrawDown, Is.True);
        }

        [Test]
        public void NativeOnlyTriggerDoesNotUseChrisReleaseGate()
        {
            Assert.That(CHRISInputMappingHost.ShouldGatePhysicalTrigger(false, false, false), Is.False,
                "With no active mapping, Escape and XR focus retain the native trigger path");
            Assert.That(CHRISInputMappingHost.ShouldGatePhysicalTrigger(true, false, false), Is.True);
            Assert.That(CHRISInputMappingHost.ShouldGatePhysicalTrigger(false, true, false), Is.True,
                "The mapped release edge is still delivered");
            Assert.That(CHRISInputMappingHost.ShouldGatePhysicalTrigger(false, false, true), Is.True,
                "A held physical trigger remains gated after mapping Stop");
        }

        [Test]
        public void HostStateResetsOnEveryPlayModeEntry()
        {
            // The project enters Play mode without a domain reload (EditorSettings m_EnterPlayModeOptions),
            // so host statics would otherwise survive into the next session and skip the startup load.
            var host = typeof(CHRISInputMappingHost);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var reset = host.GetMethod("ResetForPlay", flags);
            var start = host.GetMethod("Start", flags);
            Assert.That(reset.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>().loadType,
                Is.EqualTo(RuntimeInitializeLoadType.SubsystemRegistration));
            Assert.That(start.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>().loadType,
                Is.EqualTo(RuntimeInitializeLoadType.AfterSceneLoad), "The file loads at startup, before any controller read");

            var started = host.GetField("s_Started", flags);
            var frame = host.GetField("s_Frame", flags);
            var shortcutsOwned = host.GetField("s_ShortcutsOwned", flags);
            var previous = CHRISInputMappingHost.Remap;
            try
            {
                previous.Offer(Space());
                previous.Tick(new FakeInput(), true, false);
                Assert.That(previous.Active, Is.Not.Null);
                started.SetValue(null, true);
                frame.SetValue(null, 1234);
                shortcutsOwned.SetValue(null, true);

                reset.Invoke(null, null);

                Assert.That(CHRISInputMappingHost.Remap, Is.Not.SameAs(previous));
                Assert.That(CHRISInputMappingHost.Remap.Active, Is.Null, "No mapping carries into the next session");
                Assert.That(CHRISInputMappingHost.Remap.HasPending, Is.False);
                Assert.That((bool)started.GetValue(null), Is.False, "The next session loads the file again");
                Assert.That((int)frame.GetValue(null), Is.EqualTo(-1));
                Assert.That((bool)shortcutsOwned.GetValue(null), Is.False);
            }
            finally { reset.Invoke(null, null); }
        }

        [Test]
        public void UndoFiresOncePerPress()
        {
            var (remap, input) = Activated(AllKeys());
            input.Held.Add("key.z");
            remap.Tick(input, true, false);
            Assert.That(remap.UndoPressed, Is.True);
            for (int i = 0; i < 5; i++)
            {
                remap.Tick(input, true, false);
                Assert.That(remap.UndoPressed, Is.False, "Holding does not repeat");
            }
            input.Held.Remove("key.z");
            remap.Tick(input, true, false);
            input.Held.Add("key.z");
            remap.Tick(input, true, false);
            Assert.That(remap.UndoPressed, Is.True, "A new press undoes again");
            remap.Tick(input, false, false);
            Assert.That(remap.UndoPressed, Is.False);
            remap.Tick(input, true, false);
            Assert.That(remap.UndoPressed, Is.False, "A press held through focus loss needs a fresh press");
        }

        [Test]
        public void WheelStepsWholeNotchesOutsideStrokes()
        {
            var (remap, input) = Activated(AllKeys());
            input.WheelNotches = 1;
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(1));
            input.WheelNotches = 0.5f;
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(0), "A half notch waits");
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(1), "Halves add up to one notch");
            input.WheelNotches = -2;
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(-2), "Towards the user is smaller");
            input.WheelNotches = 0.75f;
            remap.Tick(input, true, true);
            Assert.That(remap.SizeNotches, Is.EqualTo(0), "Ignored during a stroke");
            input.WheelNotches = 0.5f;
            remap.Tick(input, true, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(0), "Nothing from the stroke carries over");
            input.WheelNotches = 3;
            remap.Tick(input, false, false);
            Assert.That(remap.SizeNotches, Is.EqualTo(0), "Discarded while unfocused");

            var (drawOnly, other) = Activated(Space());
            other.WheelNotches = 3;
            drawOnly.Tick(other, true, false);
            Assert.That(drawOnly.SizeNotches, Is.EqualTo(0), "An unmapped wheel is left to Open Brush");
        }

        [Test]
        public void ViewKeysNormaliseCancelAndStop()
        {
            var (remap, input) = Activated(AllKeys());
            input.Held.UnionWith(new[] { "key.w", "key.d" });
            remap.Tick(input, true, false);
            Assert.That(Vector2.Distance(remap.ViewKeys, new Vector2(1, 1).normalized), Is.LessThan(1e-5f), "Diagonals are normalised");
            input.Held.Add("key.s");
            remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(new Vector2(1, 0)), "Opposite keys cancel");
            remap.Tick(input, true, true);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "Ignored during a stroke");
            remap.Tick(input, false, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "Focus loss halts movement");
            remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "Keys held through focus loss need a fresh press");
            input.Held.Clear();
            remap.Tick(input, true, false);
            input.Held.Add("key.a");
            remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(new Vector2(-1, 0)));
            input.StopPressedThisFrame = true;
            remap.Tick(input, true, false);
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "Stop halts movement");
        }

        [Test]
        public void SharedMouseViewCaseOwnsMouseMovement()
        {
            var mapping = CHRISInputMapping.Load(System.IO.Path.Combine(TestCHRISInputMapping.Fixtures, "valid_mouse_view_middle_undo.json"));
            var (remap, input) = Activated(mapping);
            Assert.That(remap.MouseDeltaMapped, Is.True);
            input.MouseDelta = new Vector2(30, -10);
            remap.Tick(input, true, false);
            Assert.That(remap.ViewMouse, Is.EqualTo(new Vector2(30, -10)));
            remap.Tick(input, true, true);
            Assert.That(remap.ViewMouse, Is.EqualTo(Vector2.zero), "Ignored during a stroke");
            input.Held.Add("mouse.middle");
            remap.Tick(input, true, false);
            Assert.That(remap.UndoPressed, Is.True, "Undo on the middle button");
            input.StopPressedThisFrame = true;
            remap.Tick(input, true, false);
            Assert.That(remap.MouseDeltaMapped, Is.False, "Stop gives mouse movement back to Open Brush");
            Assert.That(Activated(AllKeys()).Item1.MouseDeltaMapped, Is.False);
        }

        [Test]
        public void HeadingFollowsYawAndSurvivesLookingStraightDown()
        {
            var heading = new CHRISViewHeading();
            heading.Update(new Vector3(1, -0.5f, 1));
            Assert.That(Vector3.Distance(heading.Forward, new Vector3(1, 0, 1).normalized), Is.LessThan(1e-5f), "Pitch is ignored");
            Assert.That(Vector3.Distance(heading.Right, new Vector3(1, 0, -1).normalized), Is.LessThan(1e-5f));
            heading.Update(new Vector3(0.05f, -0.99f, 0.05f));
            Assert.That(Vector3.Distance(heading.Forward, new Vector3(1, 0, 1).normalized), Is.LessThan(1e-5f), "Keeps the last valid heading");
            heading.Update(new Vector3(0, 0, -1));
            Assert.That(heading.ToRoom(new Vector2(0, 2)), Is.EqualTo(new Vector3(0, 0, -2)), "Forward follows a turn");
            Assert.That(heading.ToRoom(new Vector2(1, 0)).y, Is.EqualTo(0), "No vertical movement");
        }

        [Test]
        public void BrushKeysMoveDuringStrokesAndStopOnFocusOrStop()
        {
            var mapping = CHRISInputMapping.Load(System.IO.Path.Combine(TestCHRISInputMapping.Fixtures, "valid_keys_brush_wasd_view.json"));
            var brush = mapping.Find(CHRISMappedAction.MoveBrush);
            Assert.That(brush.Source, Is.EqualTo(CHRISMappingSource.KeyVector2));
            var (remap, input) = Activated(mapping);
            Assert.That(remap.MouseDeltaMapped, Is.False, "Keyboard-only layout leaves mouse movement to Open Brush");
            input.Held.UnionWith(new[] { "key." + brush.Up, "key." + brush.Right });
            remap.Tick(input, true, true);
            Assert.That(Vector2.Distance(remap.BrushKeys, new Vector2(1, 1).normalized), Is.LessThan(1e-5f),
                "Brush keys move during a stroke, normalised");
            Assert.That(remap.ViewKeys, Is.EqualTo(Vector2.zero), "View movement is still ignored during a stroke");
            remap.Tick(input, false, true);
            Assert.That(remap.BrushKeys, Is.EqualTo(Vector2.zero), "Focus loss halts the brush");
            remap.Tick(input, true, false);
            Assert.That(remap.BrushKeys, Is.EqualTo(Vector2.zero), "Keys held through focus loss need a fresh press");
            input.Held.Clear();
            remap.Tick(input, true, false);
            input.Held.Add("key." + brush.Left);
            remap.Tick(input, true, false);
            Assert.That(remap.BrushKeys, Is.EqualTo(new Vector2(-1, 0)));
            input.StopPressedThisFrame = true;
            remap.Tick(input, true, false);
            Assert.That(remap.BrushKeys, Is.EqualTo(Vector2.zero), "Stop halts the brush");
        }

        [Test]
        public void BrushPlaneFacesTheHeadAndClampsTheTip()
        {
            var plane = new CHRISBrushPlane();
            var head = new Vector3(1, 17, 2);
            plane.Activate(head, new Vector3(0, 0, 1), Vector3.forward, 5, 6);
            Assert.That(Vector3.Distance(plane.Tip, head + new Vector3(0, 0, 5)), Is.LessThan(1e-5f), "Starts at the origin, distance ahead");
            Assert.That(Vector3.Distance(plane.Right, Vector3.right), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(plane.Up, Vector3.up), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(plane.Rotation * Vector3.forward, Vector3.forward), Is.LessThan(1e-5f), "Points away from the user");
            plane.Move(new Vector2(2, -1));
            Assert.That(Vector3.Distance(plane.Tip, head + new Vector3(2, -1, 5)), Is.LessThan(1e-5f));
            plane.Move(new Vector2(100, -100));
            Assert.That(plane.Offset, Is.EqualTo(new Vector2(6, -6)), "Clamped at the edge without wrapping");

            // Pitch is kept for the normal; the axes stay level (roll dropped).
            var pitchedDown = new Vector3(0, -1, 1).normalized;
            plane.Activate(Vector3.zero, pitchedDown, Vector3.forward, 5, 6);
            Assert.That(Vector3.Distance(plane.Origin, pitchedDown * 5), Is.LessThan(1e-5f));
            Assert.That(plane.Right.y, Is.EqualTo(0).Within(1e-5f), "No roll");
            Assert.That(Vector3.Dot(plane.Up, plane.Normal), Is.EqualTo(0).Within(1e-5f));
            Assert.That(plane.Offset, Is.EqualTo(Vector2.zero), "Reactivation re-anchors at the origin");

            // Looking straight down falls back to the last valid yaw for the axes.
            plane.Activate(Vector3.zero, Vector3.down, Vector3.left, 5, 6);
            Assert.That(Vector3.Distance(plane.Right, Vector3.Cross(Vector3.up, Vector3.left)), Is.LessThan(1e-5f));
        }

        [Test]
        public void UndoOnlyMappingEditKeepsTheMovingBrushTip()
        {
            var host = typeof(CHRISInputMappingHost);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            string fixture = System.IO.File.ReadAllText(System.IO.Path.Combine(TestCHRISInputMapping.Fixtures,
                "valid_keys_brush_wasd_view.json"));
            var original = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(fixture));
            var undoEdit = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(fixture.Replace("\"key\": \"z\"", "\"key\": \"x\"")));
            var brushEdit = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(fixture.Replace("\"right\": \"rightArrow\"", "\"right\": \"j\"")));
            host.GetMethod("ResetForPlay", flags).Invoke(null, null);
            try
            {
                CHRISInputMappingHost.EnsureBrushPlane(original, Vector3.zero, Vector3.forward);
                CHRISInputMappingHost.BrushPlane.Move(new Vector2(2, -1));
                var tip = CHRISInputMappingHost.BrushPlane.Tip;
                CHRISInputMappingHost.EnsureBrushPlane(undoEdit, new Vector3(10, 0, 0), Vector3.right);
                Assert.That(CHRISInputMappingHost.SameBrushMovement(original, undoEdit), Is.True);
                Assert.That(CHRISInputMappingHost.BrushPlane.Tip, Is.EqualTo(tip),
                    "Changing undo must not jump the brush tip while move_brush stays the same");
                CHRISInputMappingHost.EnsureBrushPlane(brushEdit, new Vector3(10, 0, 0), Vector3.right);
                Assert.That(CHRISInputMappingHost.SameBrushMovement(undoEdit, brushEdit), Is.False);
                Assert.That(CHRISInputMappingHost.BrushPlane.Offset, Is.EqualTo(Vector2.zero),
                    "Changing move_brush establishes a new head-facing plane");
            }
            finally { host.GetMethod("ResetForPlay", flags).Invoke(null, null); }
        }

        [Test]
        public void BrushOwnershipMakesBrushPresentAndHandBackRestoresTracking()
        {
            // The brush counts as present exactly while CHRIS owns its pose (IsTrackedObjectValid
            // ORs BrushPoseOwned for the brush). Hand-back restores the driver's previous state.
            var host = typeof(CHRISInputMappingHost);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var obj = new GameObject("CHRIS brush pose test");
            try
            {
                var driver = obj.AddComponent<UnityEngine.SpatialTracking.TrackedPoseDriver>();
                driver.enabled = true;
                Assert.That(CHRISInputMappingHost.BrushPoseOwned, Is.False, "Nothing owned by default");

                host.GetField("s_Driver", flags).SetValue(null, driver);
                host.GetField("s_DriverWasEnabled", flags).SetValue(null, true);
                driver.enabled = false;
                Assert.That(CHRISInputMappingHost.BrushPoseOwned, Is.True, "Owned: the brush counts as present");

                host.GetMethod("ReleaseBrush", flags).Invoke(null, null);
                Assert.That(CHRISInputMappingHost.BrushPoseOwned, Is.False, "Hand-back: physical presence applies again");
                Assert.That(driver.enabled, Is.True, "Hand-back restores the controller's tracking driver");
            }
            finally
            {
                host.GetMethod("ResetForPlay", flags).Invoke(null, null);
                Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void NewMappingWaitsForNeutral()
        {
            var (remap, input) = Activated(Space());
            var next = LeftButton();
            input.Held.Add("key.space");
            remap.Tick(input, true, false);
            remap.Offer(next);
            remap.Tick(input, true, true);
            Assert.That(remap.Active, Is.Not.SameAs(next), "An old mapped input is held");
            Assert.That(remap.DrawHeld, Is.True, "The current mapping keeps drawing until neutral");
            input.Held.Remove("key.space");
            remap.Tick(input, true, true);
            Assert.That(remap.Active, Is.Not.SameAs(next), "A stroke is still in progress");
            input.Held.Add("mouse.left");
            remap.Tick(input, true, false);
            Assert.That(remap.Active, Is.Not.SameAs(next), "A new mapped input is held");
            input.Held.Clear();
            remap.Tick(input, true, false);
            Assert.That(remap.Active, Is.SameAs(next));
            Assert.That(remap.HasPending, Is.False);

            input.StopPressedThisFrame = true;
            remap.Offer(Space());
            remap.Tick(input, true, false);
            Assert.That(remap.Active == null && !remap.HasPending, Is.True, "Stop also drops a waiting mapping");
        }
    }
}
