// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace TiltBrush
{
    public class TestCHRISInputRemap
    {
        sealed class FakeInput : ICHRISInputState
        {
            public readonly HashSet<string> Held = new HashSet<string>();
            public bool StopPressedThisFrame { get; set; }
            public bool IsPressed(string input) => Held.Contains(input);
        }

        static CHRISInputMapping Mapping(string drawSource, string extra = "") => CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(
            "{\"version\":\"v0.1.1\",\"app\":\"openbrush\",\"profile\":\"keyboard_mouse\",\"mappings\":[" +
            "{\"id\":\"draw\",\"source\":" + drawSource + ",\"action\":\"draw\"}" + extra + "]}"));
        static CHRISInputMapping Space() => Mapping("{\"type\":\"key\",\"key\":\"space\"}",
            ",{\"id\":\"undo\",\"source\":{\"type\":\"key\",\"key\":\"z\"},\"action\":\"undo\"}");
        static CHRISInputMapping LeftButton() => Mapping("{\"type\":\"mouse_button\",\"button\":\"left\"}");

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
