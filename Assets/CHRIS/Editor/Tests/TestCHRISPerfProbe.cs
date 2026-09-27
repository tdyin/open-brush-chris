// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Linq;
using NUnit.Framework;

namespace TiltBrush
{
    public class TestCHRISPerfProbe
    {
        [Test]
        public void SummaryUsesMedianNearestRankP95AndMax()
        {
            var odd = CHRISPerfProbe.Summarize(new double[] { 5, 1, 3 });
            Assert.That(odd.Count, Is.EqualTo(3));
            Assert.That(odd.Median, Is.EqualTo(3));
            Assert.That(odd.Max, Is.EqualTo(5));
            var even = CHRISPerfProbe.Summarize(new double[] { 4, 1, 2, 3 });
            Assert.That(even.Median, Is.EqualTo(2.5));
            var hundred = CHRISPerfProbe.Summarize(Enumerable.Range(1, 100).Select(i => (double)i));
            Assert.That(hundred.P95, Is.EqualTo(95), "Nearest-rank 95th percentile");
            Assert.That(CHRISPerfProbe.Summarize(new double[0]).Count, Is.Zero);
        }

        [Test]
        public void LatencySamplesOnlyNewEventsPerAction()
        {
            var sampler = new CHRISPerfProbe.LatencySampler();
            Assert.That(sampler.Offer("undo", 10.000, 10.013), Is.True);
            Assert.That(sampler.Offer("undo", 10.000, 10.027), Is.False, "A held key's press counts once");
            Assert.That(sampler.Offer("move_view", 10.000, 10.013), Is.True, "Actions are tracked separately");
            Assert.That(sampler.Offer("undo", 11.000, 11.014), Is.True, "A new press counts again");
            Assert.That(sampler.Offer("draw", 0, 5), Is.False, "No event yet");
            Assert.That(sampler.Offer("draw", 6, 5), Is.False, "Event from the future is rejected");
            var undo = sampler.Summaries().Single(kv => kv.Key == "undo").Value;
            Assert.That(undo.Count, Is.EqualTo(2));
            Assert.That(undo.Median, Is.EqualTo(13.5).Within(1e-6));
            var raw = sampler.RawCsv().ToList();
            Assert.That(raw[0], Is.EqualTo("action,latency_ms"), "Raw samples are kept for independent recalculation");
            Assert.That(raw.Count(line => line.StartsWith("undo,")), Is.EqualTo(2));
            sampler.Clear();
            Assert.That(sampler.Summaries(), Is.Empty);
        }

        [Test]
        public void RawCapturesInTheSameSecondNeverOverwriteEachOther()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chris-perf-raw-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                string first = CHRISPerfProbe.WriteRaw(dir, "latency", "", new[] { "action,latency_ms", "draw,1" });
                string second = CHRISPerfProbe.WriteRaw(dir, "latency", "", new[] { "action,latency_ms", "draw,2" });
                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null);
                Assert.That(second, Is.Not.EqualTo(first), "Each capture gets its own file");
                Assert.That(System.IO.Directory.GetFiles(dir).Length, Is.EqualTo(2), "Both captures kept");
                Assert.That(System.IO.File.ReadAllLines(first)[1], Is.EqualTo("draw,1"), "First capture intact");
                Assert.That(System.IO.File.ReadAllLines(second)[1], Is.EqualTo("draw,2"));
                string labelled = CHRISPerfProbe.WriteRaw(dir, "frametime", "A mapping/active", new[] { "frame_interval_ms", "13.9" });
                Assert.That(System.IO.Path.GetFileName(labelled), Does.StartWith("frametime-Amappingactive-"), "Label reduced to letters and digits");
            }
            finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        }

        [Test]
        public void LatencyStopIsRefusedDuringAFrameWindow()
        {
            var probe = typeof(CHRISPerfProbe);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var frameEnd = probe.GetField("s_FrameEnd", flags);
            try
            {
                CHRISPerfProbe.Latency(1);
                frameEnd.SetValue(null, 1e9f);
                Assert.That(CHRISPerfProbe.Latency(0), Does.Contain("frame-time window is running"));
                Assert.That(CHRISPerfProbe.LatencyActive, Is.True, "Samples are kept until the window ends");
            }
            finally
            {
                frameEnd.SetValue(null, 0f);
                probe.GetMethod("ResetForPlay", flags).Invoke(null, null);
            }
        }

        [Test]
        public void HeldMovementKeysSampleOnlyTheirPressFrame()
        {
            var zero = UnityEngine.Vector2.zero;
            var up = UnityEngine.Vector2.up;
            Assert.That(CHRISInputMappingHost.ShouldSampleMovement(up, zero), Is.True, "Press frame");
            Assert.That(CHRISInputMappingHost.ShouldSampleMovement(up, up), Is.False, "Held: unrelated events add no samples");
            Assert.That(CHRISInputMappingHost.ShouldSampleMovement(UnityEngine.Vector2.right, up), Is.False, "Changing direction while held");
            Assert.That(CHRISInputMappingHost.ShouldSampleMovement(zero, up), Is.True, "Mouse-driven movement (no keys) samples per new event");
        }
    }
}
