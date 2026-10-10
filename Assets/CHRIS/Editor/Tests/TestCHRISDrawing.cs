// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TiltBrush
{
    // Agent drawing (D95): the draw lease scope, the stroke step's caps, local timing and
    // instant release, the per-task frame and sketch guard, and the read-only snapshot.
    public class TestCHRISDrawing
    {
        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const float Dt = 1f / 90;

        static JObject Physical() => new JObject { ["source"] = "physical", ["mode"] = null, ["selected_hand"] = null };
        // A frame 5.5 units ahead of a head at the origin, facing +z.
        static CHRISDrawFrame TestFrame() => CHRISDrawFrame.Facing(Vector3.zero, Vector3.forward, "frame");
        static CHRISProcedureExecutor DrawLease(float now = 0, int budget = CHRISDrawingRuns.MaxStrokesPerRun) =>
            new CHRISProcedureExecutor("lease", "task", Physical(), TestFrame().Point(0.5f, 0.5f, 0.5f) - Vector3.forward * 2,
                Quaternion.identity, now, CHRISProcedureExecutor.DrawScope, TestFrame(), budget);
        static JObject Stroke(int seq, int timeout = 8000, params float[] xy)
        {
            var points = new JArray();
            for (int i = 0; i < xy.Length; i += 2) points.Add(new JArray(xy[i], xy[i + 1]));
            return new JObject { ["step_id"] = "stroke" + seq, ["seq"] = seq, ["kind"] = "stroke", ["points"] = points, ["timeout_ms"] = timeout };
        }
        static CHRISProcedureFrame Frame(float now) => new CHRISProcedureFrame { Now = now, AttachLocalRotation = Quaternion.identity };
        static JToken Step(CHRISProcedureExecutor lease, int index) => lease.Status()["steps"][index];

        [Test]
        public void StrokeStepShapesAndScopesAreStrict()
        {
            Assert.That(CHRISProcedureExecutor.ValidateStep(Stroke(1, 8000, 0, 0, 1, 1)), Is.Null);
            var sixtyFour = Stroke(1, 8000, Enumerable.Range(0, 128).Select(i => (i % 2 == 0 ? i / 128f : 0.5f)).ToArray());
            Assert.That(CHRISProcedureExecutor.ValidateStep(sixtyFour), Is.Null);
            var withDepth = Stroke(1, 8000, 0, 0, 1, 1); withDepth["depth"] = 1;
            Assert.That(CHRISProcedureExecutor.ValidateStep(withDepth), Is.Null);
            var tooMany = Stroke(1, 8000, Enumerable.Range(0, 130).Select(i => 0.5f).ToArray());
            var badDepth = Stroke(1, 8000, 0, 0, 1, 1); badDepth["depth"] = 1.2;
            var textPoint = Stroke(1, 8000, 0, 0, 1, 1); textPoint["points"][0][0] = "0";
            var triple = Stroke(1, 8000, 0, 0, 1, 1); triple["points"][0] = new JArray(0, 0, 0);
            var extra = Stroke(1, 8000, 0, 0, 1, 1); extra["duration_ms"] = 100;
            foreach (var bad in new[] { Stroke(1, 8000, 0, 0), tooMany, Stroke(1, 8000, 0, 0, 1.01f, 1), Stroke(1, 8000, 0, -0.01f, 1, 1),
                badDepth, Stroke(1, 8001, 0, 0, 1, 1), textPoint, triple, extra })
                Assert.That(CHRISProcedureExecutor.ValidateStep(bad), Is.Not.Null, bad.ToString());

            var palette = new CHRISProcedureExecutor("lease", "task", Physical(), Vector3.zero, Quaternion.identity, 0);
            Assert.That(palette.Submit(Stroke(1, 8000, 0, 0, 1, 1), 0), Is.EqualTo("Lease is not draw scope"));
            var draw = DrawLease();
            var aim = new JObject { ["step_id"] = "aim1", ["seq"] = 1, ["kind"] = "aim",
                ["target_id"] = "brush:00000000-0000-0000-0000-000000000001", ["timeout_ms"] = 2000 };
            Assert.That(draw.Submit(aim, 0), Is.EqualTo("Aim and press need a palette lease"));
            // Five frame widths back and forth: 3 m of path is over the 2.4 m cap.
            Assert.That(draw.Submit(Stroke(1, 8000, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0), 0), Is.EqualTo("Stroke path too long"));
            // 0.6 m at 0.4 m/s is 1.5 s of pen time alone.
            Assert.That(draw.Submit(Stroke(1, 1500, 0, 0.5f, 1, 0.5f), 0), Is.EqualTo("Stroke too long for its timeout"));
            Assert.That(draw.Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 0), Is.Null, "a refused step does not consume its seq");
            Assert.That(DrawLease(0, 0).Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 0), Is.EqualTo("Stroke limit for this drawing reached"));
            // The shared case that is schema-valid but too long for its own timeout.
            var noted = TestCHRISProcedure.SharedCase("step_stroke_too_long_for_timeout_valid");
            Assert.That(CHRISProcedureExecutor.ValidateStep(noted), Is.Null);
            Assert.That(DrawLease().Submit(noted, 0), Is.EqualTo("Stroke too long for its timeout"));
        }

        static CHRISDrawFrame TestBox() => CHRISDrawFrame.Boxed(Vector3.zero, Vector3.forward, "box");
        static CHRISProcedureExecutor BoxLease() =>
            new CHRISProcedureExecutor("lease", "task", Physical(), TestBox().Point3(0.5f, 0.5f, 0) - Vector3.forward * 2,
                Quaternion.identity, 0, CHRISProcedureExecutor.Draw3dScope, TestBox(), CHRISDrawingRuns.MaxStrokesPerRun);
        static JObject Stroke3(int seq, int timeout = 8000, params float[] xyz)
        {
            var points = new JArray();
            for (int i = 0; i < xyz.Length; i += 3) points.Add(new JArray(xyz[i], xyz[i + 1], xyz[i + 2]));
            return new JObject { ["step_id"] = "stroke3d" + seq, ["seq"] = seq, ["kind"] = "stroke3d", ["points"] = points, ["timeout_ms"] = timeout };
        }

        [Test]
        public void ThreeDStrokesUseABoxAtArmsLengthAndStrictScopes()
        {
            // D106 box: 0.6 m cube, centre 0.70 m ahead and 0.15 m below the eyes; near face 0.40 m away.
            var box = TestBox();
            float units = App.METERS_TO_UNITS;
            Assert.That(box.Box, Is.True);
            Assert.That(box.Center, Is.EqualTo(new Vector3(0, -0.15f * units, 0.70f * units)));
            Assert.That(box.Point3(0.5f, 0.5f, 0).z, Is.EqualTo(0.40f * units).Within(1e-4f), "near face");
            Assert.That(box.Point3(0.5f, 0.5f, 1).z, Is.EqualTo(1.00f * units).Within(1e-4f), "far face");
            Assert.That(box.Point3(0, 0, 0).x, Is.LessThan(box.Center.x), "x = 0 is the left");
            Assert.That(box.Point3(0, 0, 0).y, Is.GreaterThan(box.Center.y), "y = 0 is the top");

            Assert.That(CHRISProcedureExecutor.ValidateStep(Stroke3(1, 8000, 0, 0, 0, 1, 1, 1)), Is.Null);
            var sixtyFour = Stroke3(1, 8000, Enumerable.Range(0, 192).Select(i => (i % 64) / 64f).ToArray());
            Assert.That(CHRISProcedureExecutor.ValidateStep(sixtyFour), Is.Null);
            var twoValue = Stroke3(1, 8000, 0, 0, 0, 1, 1, 1); twoValue["points"][0] = new JArray(0, 0);
            var fourValue = Stroke3(1, 8000, 0, 0, 0, 1, 1, 1); fourValue["points"][0] = new JArray(0, 0, 0, 0);
            var depth = Stroke3(1, 8000, 0, 0, 0, 1, 1, 1); depth["depth"] = 0.5;
            foreach (var bad in new[] { twoValue, fourValue, Stroke3(1, 8000, 0, 0, 0, 1, 1, 1.01f), depth, Stroke3(1, 8001, 0, 0, 0, 1, 1, 1),
                Stroke3(1, 8000, 0, 0, 0), Stroke3(1, 8000, Enumerable.Range(0, 195).Select(i => 0.5f).ToArray()) })
                Assert.That(CHRISProcedureExecutor.ValidateStep(bad), Is.Not.Null, bad.ToString());

            var draw3d = BoxLease();
            Assert.That(draw3d.Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 0), Is.EqualTo("2D strokes need a draw lease"));
            Assert.That(DrawLease().Submit(Stroke3(1, 8000, 0, 0, 0, 1, 1, 1), 0), Is.EqualTo("3D strokes need a draw3d lease"));
            var palette = new CHRISProcedureExecutor("lease", "task", Physical(), Vector3.zero, Quaternion.identity, 0);
            Assert.That(palette.Submit(Stroke3(1, 8000, 0, 0, 0, 1, 1, 1), 0), Is.EqualTo("3D strokes need a draw3d lease"));
            var aim = new JObject { ["step_id"] = "aim1", ["seq"] = 1, ["kind"] = "aim",
                ["target_id"] = "brush:00000000-0000-0000-0000-000000000001", ["timeout_ms"] = 2000 };
            Assert.That(draw3d.Submit(aim, 0), Is.EqualTo("Aim and press need a palette lease"));
            // Four box diagonals (about 4.2 m) exceed the 2.4 m path cap.
            Assert.That(draw3d.Submit(Stroke3(1, 8000, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0), 0), Is.EqualTo("Stroke path too long"));

            // A 3D stroke travels, presses at its first point, follows every point and releases.
            Assert.That(draw3d.Submit(Stroke3(1, 8000, 0.2f, 0.5f, 0.1f, 0.8f, 0.5f, 0.9f), 0), Is.Null);
            float now = 0;
            Vector3 firstDown = Vector3.zero;
            bool down = false;
            while (now < 8 && (string)Step(draw3d, 0)["status"] == "pending")
            {
                now += Dt; draw3d.Heartbeat(now); draw3d.Tick(Frame(now));
                if (draw3d.Hand.TriggerHeld && !down) { down = true; firstDown = draw3d.Hand.Position; }
            }
            Assert.That((string)Step(draw3d, 0)["status"], Is.EqualTo("succeeded"));
            Assert.That((double)Step(draw3d, 0)["progress"], Is.EqualTo(1));
            Assert.That(Vector3.Distance(firstDown, box.Point3(0.2f, 0.5f, 0.1f)), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(draw3d.Hand.Position, box.Point3(0.8f, 0.5f, 0.9f)), Is.LessThan(0.01f));
            Assert.That(draw3d.Hand.TriggerHeld, Is.False);

            CHRISDrawingRuns.ResetForPlay();
            try
            {
                CHRISDrawingRuns.Begin("flat", Vector3.zero, Vector3.forward, 0, false);
                CHRISDrawingRuns.Begin("solid", Vector3.zero, Vector3.forward, 0, false, true);
                Assert.That(CHRISDrawingRuns.AcquireRefusal("flat", 0, true), Is.EqualTo("This task has a 2D drawing frame"));
                Assert.That(CHRISDrawingRuns.AcquireRefusal("solid", 0, false), Is.EqualTo("This task has a 3D drawing box"));
                Assert.That(CHRISDrawingRuns.AcquireRefusal("solid", 0, true), Is.Null);
            }
            finally { CHRISDrawingRuns.ResetForPlay(); }
        }

        // Atlas review 2026-10-09, item 1: only a physical brush-controller click may start a case
        // or decide an approval; every mapped or virtual source of the same click is refused.
        [Test]
        public void TestPanelClicksNeedThePhysicalBrushController()
        {
            Assert.That(CHRISHandAuthority.PhysicalClick(true, false, false, false, false, false, false), Is.True);
            Assert.That(CHRISHandAuthority.PhysicalClick(false, false, false, false, false, false, false), Is.False, "no real trigger");
            Assert.That(CHRISHandAuthority.PhysicalClick(true, true, false, false, false, false, false), Is.False, "procedure lease");
            Assert.That(CHRISHandAuthority.PhysicalClick(true, false, true, false, false, false, false), Is.False, "mouse button");
            Assert.That(CHRISHandAuthority.PhysicalClick(true, false, false, true, false, false, false), Is.False, "mapped draw key");
            Assert.That(CHRISHandAuthority.PhysicalClick(true, false, false, false, true, false, false), Is.False, "keyboard UI pointer (Enter)");
            Assert.That(CHRISHandAuthority.PhysicalClick(true, false, false, false, false, true, false), Is.False, "recovery UI");
            Assert.That(CHRISHandAuthority.PhysicalClick(true, false, false, false, false, false, true), Is.False, "mapped brush pose");

            // The real button path: the panel's own press handlers, which every UI activation
            // (physical or mapped) reaches.
            var popupObject = UnityEngine.Object.Instantiate(CHRISUIResources.Load().PopupPrefab);
            CHRISTestDisplay.ResetForPlay();
            try
            {
                var popup = popupObject.GetComponent<CHRISNativePopup>();
                popup.BuildView();
                void Press(string name)
                {
                    var button = popupObject.GetComponentsInChildren<CHRISNativeButton>(true).Single(b => b.name == name);
                    typeof(CHRISNativeButton).GetMethod("OnButtonPressed", InstanceFlags).Invoke(button, null);
                }
                JObject Show(string nonce) => new JObject {
                    ["lines"] = new JArray("test"), ["nonce"] = nonce,
                    ["buttons"] = nonce == null ? new JArray() : new JArray("approve", "decline"),
                    ["cases"] = new JArray(new JObject { ["id"] = 1, ["title"] = "Happy path" }), ["ttl_s"] = 30 };
                var display = CHRISTestDisplay.Instance;
                display.Show(Show(null), Time.realtimeSinceStartup);
                popup.RefreshTest();
                CHRISHandAuthority.PhysicalClickSource = () => false;
                Press("Test case 1");
                Assert.That(display.Latest, Is.EqualTo(0), "a mapped Start click records nothing");
                display.Show(Show("nonce_a"), Time.realtimeSinceStartup);
                popup.RefreshTest();
                Press("Test approve");
                Press("Test decline");
                Assert.That(display.Latest, Is.EqualTo(0), "a mapped Approve/Decline consumes nothing");
                Assert.That(display.ButtonsLive(Time.realtimeSinceStartup), Is.True, "the nonce is still live for a physical click");
                CHRISHandAuthority.PhysicalClickSource = () => true;
                Press("Test approve");
                Assert.That(display.Latest, Is.EqualTo(1));
                Assert.That((string)display.Events(0)["events"][0]["kind"], Is.EqualTo("approve"));
                display.Show(Show(null), Time.realtimeSinceStartup);
                popup.RefreshTest();
                Press("Test case 1");
                Assert.That(display.Latest, Is.EqualTo(2), "a physical Start click records");
                // Stop is never gated.
                CHRISHandAuthority.PhysicalClickSource = () => false;
                Assert.That(() => Press("Local Stop"), Throws.Nothing);
            }
            finally
            {
                CHRISHandAuthority.PhysicalClickSource = null;
                CHRISTestDisplay.ResetForPlay();
                UnityEngine.Object.DestroyImmediate(popupObject);
            }
        }

        // Atlas review item 3: a failure part-way through the grid leaves no textures behind.
        [Test]
        public void GridSnapshotFailureLeavesNoTextures()
        {
            var box = TestBox();
            int mask = 1 << LayerMask.NameToLayer("MainCanvas");
            foreach (int failAt in new[] { 4, 5 })
            {
                int before = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
                CHRISDrawingRuns.FailGridForTest = n => { if (n == failAt) throw new InvalidOperationException("forced"); };
                try { Assert.That(() => CHRISDrawingRuns.RenderGrid(box, mask, 64), Throws.InvalidOperationException); }
                finally { CHRISDrawingRuns.FailGridForTest = null; }
                Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Length, Is.EqualTo(before),
                    failAt == 4 ? "views made, grid not yet made" : "grid made, composition failed");
            }
            // Re-review item 1: a failure inside a tile render, after its texture exists, at the
            // first, second and last tile.
            foreach (int failingTile in new[] { 1, 2, 4 })
            {
                int before = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
                int rendered = 0;
                CHRISDrawingRuns.FailViewForTest = () => { if (++rendered == failingTile) throw new InvalidOperationException("forced"); };
                try { Assert.That(() => CHRISDrawingRuns.RenderGrid(box, mask, 64), Throws.InvalidOperationException); }
                finally { CHRISDrawingRuns.FailViewForTest = null; }
                Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Length, Is.EqualTo(before), "tile " + failingTile + " failed after allocation");
            }
            var frame2d = TestFrame();
            int before2d = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            CHRISDrawingRuns.FailViewForTest = () => throw new InvalidOperationException("forced");
            try { Assert.That(() => CHRISDrawingRuns.RenderFrame(frame2d, mask, 64), Throws.InvalidOperationException); }
            finally { CHRISDrawingRuns.FailViewForTest = null; }
            Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Length, Is.EqualTo(before2d), "2D snapshot render");
            var ok = CHRISDrawingRuns.RenderGrid(box, mask, 64);
            Assert.That(ok.width, Is.EqualTo(128));
            UnityEngine.Object.DestroyImmediate(ok);
        }

        // Atlas re-review item 2: the approved task deadline. A lease ends at it, and no step is
        // admitted that could run past it, whatever the delay before native accepted the step.
        [Test]
        public void TaskDeadlineCapsTheLeaseAndItsSteps()
        {
            var aim = new JObject { ["step_id"] = "aim1", ["seq"] = 1, ["kind"] = "aim",
                ["target_id"] = "brush:00000000-0000-0000-0000-000000000001", ["timeout_ms"] = 2000 };
            var palette = new CHRISProcedureExecutor("lease", "task", Physical(), Vector3.zero, Quaternion.identity, 0);
            palette.LimitTo(5);
            Assert.That(palette.Submit(aim, 3.5f), Is.EqualTo("Step would pass the task deadline"), "3.5 s + 2 s > 5 s");
            Assert.That(palette.Submit(aim, 2.5f), Is.Null, "2.5 s + 2 s fits");
            palette.LimitTo(60);
            Assert.That(palette.SecondsLeft(2.5f), Is.EqualTo(2.5f).Within(1e-4f), "a later deadline never extends the lease");

            // A stroke admitted late: its native estimate (about 1.9 s) no longer fits.
            var late = DrawLease();
            late.LimitTo(4);
            Assert.That(late.Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 2.5f), Is.EqualTo("Step would pass the task deadline"));
            Assert.That((JArray)late.Status()["steps"], Is.Empty, "nothing moved");

            // Atlas second re-review: the estimate fits but the full timeout does not. A 0.6 m stroke
            // estimates about 1.9 s with a 3 s timeout; with 2.5 s left after a request delay it is
            // refused, and with 3 s left it is admitted.
            var tight = DrawLease();
            tight.LimitTo(1 + 2.5f);
            Assert.That(tight.Submit(Stroke(1, 3000, 0, 0.5f, 1, 0.5f), 1), Is.EqualTo("Step would pass the task deadline"),
                "estimate 1.9 s fits in 2.5 s, timeout 3 s does not");
            Assert.That((JArray)tight.Status()["steps"], Is.Empty);
            var room = DrawLease();
            room.LimitTo(1 + 3.05f);
            Assert.That(room.Submit(Stroke(1, 3000, 0, 0.5f, 1, 0.5f), 1), Is.Null, "the full timeout fits");
            // The separate path-duration check still applies.
            Assert.That(DrawLease().Submit(Stroke(1, 1500, 0, 0.5f, 1, 0.5f), 0), Is.EqualTo("Stroke too long for its timeout"));

            // Cut off mid-stroke: released in that frame, cancelled with partial progress.
            var lease = DrawLease();
            Assert.That(lease.Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 0), Is.Null);
            float now = 0;
            while (!lease.Hand.TriggerHeld && now < 4) { now += Dt; lease.Heartbeat(now); lease.Tick(Frame(now)); }
            for (int i = 0; i < 30; i++) { now += Dt; lease.Heartbeat(now); lease.Tick(Frame(now)); }
            Assert.That(lease.Hand.TriggerHeld, Is.True);
            lease.LimitTo(now + Dt / 2);
            now += Dt; lease.Heartbeat(now); lease.Tick(Frame(now));
            Assert.That(lease.Hand.TriggerHeld, Is.False, "released in the deadline frame");
            Assert.That(lease.State, Is.EqualTo(CHRISLeaseState.Expired));
            Assert.That(lease.Reason, Is.EqualTo("Task deadline reached"));
            Assert.That((string)Step(lease, 0)["status"], Is.EqualTo("cancelled"));
            Assert.That((double)Step(lease, 0)["progress"], Is.GreaterThan(0).And.LessThan(1));

            // Shapes.
            JObject Body(JToken deadline)
            {
                var b = new JObject { ["task_id"] = "t", ["host_session"] = "s", ["authority_epoch"] = 0, ["revision"] = 0,
                    ["hand"] = "brush", ["channels"] = new JArray("pose", "trigger") };
                if (deadline != null) b["task_deadline"] = deadline;
                return b;
            }
            Assert.That(CHRISCommandGateway.ValidateAcquire(Body(1791600000.5)), Is.Null);
            Assert.That(CHRISCommandGateway.ValidateAcquire(Body(null)), Is.Null, "optional for old clients");
            Assert.That(CHRISCommandGateway.ValidateAcquire(Body("soon")), Is.EqualTo("Invalid task_deadline"));
            Assert.That(CHRISCommandGateway.ValidateAcquire(Body(-1)), Is.EqualTo("Invalid task_deadline"));
        }

        [Test]
        public void StrokeTravelsPressesFollowsAndReleases()
        {
            var lease = DrawLease();
            Assert.That(lease.Submit(Stroke(1, 8000, 0.25f, 0.5f, 0.75f, 0.5f, 0.75f, 0.25f), 0), Is.Null);
            var frame = TestFrame();
            float now = 0;
            bool everDown = false;
            Vector3 penWhenFirstDown = Vector3.zero;
            int downFrames = 0;
            while (now < 8 && (string)Step(lease, 0)["status"] == "pending")
            {
                now += Dt;
                lease.Heartbeat(now);
                lease.Tick(Frame(now));
                if (lease.Hand.TriggerHeld)
                {
                    if (!everDown) penWhenFirstDown = lease.Hand.Position;
                    everDown = true;
                    downFrames++;
                }
            }
            Assert.That((string)Step(lease, 0)["status"], Is.EqualTo("succeeded"), Step(lease, 0).ToString());
            Assert.That((double)Step(lease, 0)["progress"], Is.EqualTo(1));
            Assert.That(Vector3.Distance(penWhenFirstDown, frame.Point(0.25f, 0.5f, 0.5f)), Is.LessThan(0.01f),
                "the trigger stays released while travelling and goes down at the first point");
            Assert.That(Vector3.Distance(lease.Hand.Position, frame.Point(0.75f, 0.25f, 0.5f)), Is.LessThan(0.01f), "pen ends at the last point");
            Assert.That(lease.Hand.TriggerHeld, Is.False);
            Assert.That(lease.StrokesStarted, Is.EqualTo(1));
            // 0.3 m + 0.15 m at 0.4 m/s is about 1.1 s of pen-down time.
            Assert.That(downFrames * Dt, Is.EqualTo(0.45f / CHRISProcedureExecutor.PenMetersPerSecond).Within(0.05f));
            Assert.That(Step(lease, 0)["progress"].Type, Is.EqualTo(JTokenType.Float));
        }

        [Test]
        public void RevocationMidStrokeReleasesAtOnceWithPartialProgress()
        {
            var lease = DrawLease();
            Assert.That(lease.Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 0), Is.Null);
            float now = 0;
            while (!lease.Hand.TriggerHeld && now < 4) { now += Dt; lease.Heartbeat(now); lease.Tick(Frame(now)); }
            for (int i = 0; i < 45; i++) { now += Dt; lease.Heartbeat(now); lease.Tick(Frame(now)); }
            Assert.That(lease.Hand.TriggerHeld, Is.True, "mid-stroke");
            var stop = Frame(now + Dt); stop.Revocation = "Stopped locally";
            lease.Tick(stop);
            Assert.That(lease.Hand.TriggerHeld, Is.False, "released in the same frame");
            Assert.That(lease.State, Is.EqualTo(CHRISLeaseState.Revoked));
            Assert.That((string)Step(lease, 0)["status"], Is.EqualTo("cancelled"));
            double progress = (double)Step(lease, 0)["progress"];
            Assert.That(progress, Is.GreaterThan(0.2).And.LessThan(0.6));

            // A lost heartbeat ends the stroke the same way.
            var quiet = DrawLease();
            Assert.That(quiet.Submit(Stroke(1, 8000, 0, 0.5f, 1, 0.5f), 0), Is.Null);
            now = 0;
            while (!quiet.Hand.TriggerHeld && now < 4) { now += Dt; quiet.Heartbeat(now); quiet.Tick(Frame(now)); }
            quiet.Tick(Frame(now + CHRISProcedureExecutor.HeartbeatSeconds + 0.1f));
            Assert.That(quiet.Hand.TriggerHeld, Is.False);
            Assert.That(quiet.Reason, Is.EqualTo("Heartbeat lost"));

            var aim = new CHRISProcedureExecutor("lease", "task", Physical(), Vector3.zero, Quaternion.identity, 0);
            aim.Submit(new JObject { ["step_id"] = "aim1", ["seq"] = 1, ["kind"] = "aim",
                ["target_id"] = "brush:00000000-0000-0000-0000-000000000001", ["timeout_ms"] = 2000 }, 0);
            Assert.That(Step(aim, 0)["progress"].Type, Is.EqualTo(JTokenType.Null), "aim and press carry a null progress");
        }

        [Test]
        public void FrameUsesImageCoordinatesAndRunsGuardTheSketch()
        {
            var frame = TestFrame();
            float units = App.METERS_TO_UNITS;
            Assert.That(frame.Center, Is.EqualTo(new Vector3(0, -0.1f * units, 0.55f * units)));
            Assert.That(frame.Point(0, 0, 0.5f).x, Is.LessThan(frame.Center.x), "x = 0 is the left edge");
            Assert.That(frame.Point(0, 0, 0.5f).y, Is.GreaterThan(frame.Center.y), "y = 0 is the top edge");
            Assert.That(Vector3.Distance(frame.Point(0, 0, 0.5f), frame.Point(1, 0, 0.5f)), Is.EqualTo(0.6f * units).Within(1e-4f));
            Assert.That(frame.Point(0.5f, 0.5f, 1).z, Is.LessThan(frame.Center.z), "depth 1 is toward the user");

            CHRISDrawingRuns.ResetForPlay();
            try
            {
                Assert.That(CHRISDrawingRuns.AcquireRefusal("draw_a", 3), Is.EqualTo("Start a new sketch first"));
                Assert.That(CHRISDrawingRuns.AcquireRefusal("draw_a", 0), Is.Null);
                var run = CHRISDrawingRuns.Begin("draw_a", Vector3.zero, Vector3.forward, 0, false);
                var again = CHRISDrawingRuns.Begin("draw_a", new Vector3(5, 0, 0), Vector3.right, 1, false);
                Assert.That(again, Is.SameAs(run));
                Assert.That(again.Frame.Center, Is.EqualTo(run.Frame.Center), "the frame does not follow the head");
                CHRISDrawingRuns.LeaseEnded("draw_a", 4, 2);
                Assert.That(CHRISDrawingRuns.AcquireRefusal("draw_a", 4), Is.Null);
                Assert.That(CHRISDrawingRuns.AcquireRefusal("draw_a", 3), Is.Null, "the user may undo agent strokes");
                Assert.That(CHRISDrawingRuns.AcquireRefusal("draw_a", 5), Is.EqualTo("Sketch changed outside this drawing"));
                CHRISDrawingRuns.LeaseEnded("draw_a", CHRISDrawingRuns.MaxStrokesPerRun, 3);
                Assert.That(CHRISDrawingRuns.AcquireRefusal("draw_a", 0), Is.EqualTo("Stroke limit for this drawing reached"));
                CHRISDrawingRuns.Tick(3 + CHRISDrawingRuns.IdleSeconds - 1, null);
                Assert.That(CHRISDrawingRuns.Find("draw_a"), Is.Not.Null);
                CHRISDrawingRuns.Tick(3 + CHRISDrawingRuns.IdleSeconds + 1, "draw_a");
                Assert.That(CHRISDrawingRuns.Find("draw_a"), Is.Not.Null, "never expires while its lease is active");
                CHRISDrawingRuns.Tick(3 + CHRISDrawingRuns.IdleSeconds + 1, null);
                Assert.That(CHRISDrawingRuns.Find("draw_a"), Is.Null, "idle runs end");
                CHRISDrawingRuns.Begin("one", Vector3.zero, Vector3.forward, 0, false);
                CHRISDrawingRuns.Begin("two", Vector3.zero, Vector3.forward, 1, false);
                CHRISDrawingRuns.Begin("three", Vector3.zero, Vector3.forward, 2, false);
                Assert.That(CHRISDrawingRuns.Find("one"), Is.Null, "at most two runs; the oldest goes");
                CHRISDrawingRuns.EndAll();
                Assert.That(CHRISDrawingRuns.Find("two"), Is.Null);
            }
            finally { CHRISDrawingRuns.ResetForPlay(); }
        }

        static JObject Route(CHRISGatewayTestHost host, string method, string path, JObject body = null, string query = "")
        {
            var requestType = typeof(CHRISCommandGateway).GetNestedType("Request", BindingFlags.NonPublic);
            var request = Activator.CreateInstance(requestType, true);
            TestCHRISAssistance.Set(request, "Method", method);
            TestCHRISAssistance.Set(request, "Path", path);
            TestCHRISAssistance.Set(request, "Query", query);
            TestCHRISAssistance.Set(request, "Body", body?.ToString() ?? "{}");
            try { return (JObject)typeof(CHRISCommandGateway).GetMethod("Route", InstanceFlags).Invoke(host, new[] { request }); }
            catch (TargetInvocationException error) { return new JObject { ["error"] = error.InnerException?.Message }; }
        }

        static JObject Acquire(CHRISGatewayTestHost host, string task, string scope)
        {
            var context = host.Capture();
            var body = new JObject { ["task_id"] = task, ["host_session"] = context["host_session"],
                ["authority_epoch"] = context["authority_epoch"], ["revision"] = context["revision"],
                ["hand"] = "brush", ["channels"] = new JArray("pose", "trigger") };
            if (scope != null) body["scope"] = scope;
            return Route(host, "POST", "/chris/procedure/acquire", body);
        }

        [Test]
        public void GatewayDrawLeasesShareOneFrameAndStopEndsTheRun()
        {
            CHRISHandAuthority.ResetForPlay();
            CHRISDrawingRuns.ResetForPlay();
            var obj = new GameObject("CHRIS drawing gateway host");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>();
                host.Initialize();
                Assert.That((int)host.Capture()["stroke_count"], Is.EqualTo(0));
                Assert.That((string)Acquire(host, "draw_t", "brush")["error"], Is.EqualTo("Invalid scope"));
                host.StrokeCount = 2;
                Assert.That((string)Acquire(host, "draw_t", "draw")["error"], Is.EqualTo("Start a new sketch first"));
                host.StrokeCount = 0;
                Assert.That((string)Route(host, "GET", "/chris/drawing/snapshot", null, "?task_id=draw_t")["error"],
                    Does.StartWith("No drawing frame for this task"));
                var first = Acquire(host, "draw_t", "draw");
                Assert.That(first["error"], Is.Null, first.ToString());
                string frameId = CHRISDrawingRuns.Find("draw_t").Frame.Id;
                Assert.That((bool)Route(host, "POST", "/chris/procedure/" + (string)first["lease_id"] + "/release")["released"], Is.True);
                // Edit mode never advances frames, so the one-frame hand-back grace is cleared by hand.
                CHRISHandAuthority.ResetForPlay();
                var second = Acquire(host, "draw_t", "draw");
                Assert.That(second["error"], Is.Null, "a drawing run leases once per stroke batch: " + second);
                Assert.That(CHRISDrawingRuns.Find("draw_t").Frame.Id, Is.EqualTo(frameId), "one frame per run");
                Route(host, "POST", "/chris/procedure/" + (string)second["lease_id"] + "/release");
                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)Acquire(host, "draw_t", null)["error"], Is.EqualTo("Task authority invalidated"),
                    "a drawing task cannot take a palette lease");
                var palette = Acquire(host, "palette_t", null);
                Route(host, "POST", "/chris/procedure/" + (string)palette["lease_id"] + "/release");
                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)Acquire(host, "palette_t", "draw")["error"], Is.EqualTo("Task authority invalidated"),
                    "a palette task cannot start a drawing");

                Assert.That((string)Route(host, "GET", "/chris/drawing/snapshot", null, "?task_id=../x")["error"], Is.EqualTo("Invalid task_id"));
                var snapshot = Route(host, "GET", "/chris/drawing/snapshot", null, "?task_id=draw_t");
                Assert.That(snapshot["error"], Is.Null, snapshot.ToString());
                Assert.That(snapshot.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(TestCHRISProcedure.SharedValidKeys("drawing_snapshot")));
                Assert.That((string)snapshot["format"], Is.EqualTo("jpeg"));
                Assert.That((int)snapshot["width"], Is.EqualTo(512));
                Assert.That((string)snapshot["frame_id"], Is.EqualTo(frameId));
                byte[] jpeg = Convert.FromBase64String((string)snapshot["data"]);
                Assert.That(jpeg[0] == 0xFF && jpeg[1] == 0xD8, Is.True, "JPEG data");
                Assert.That((string)Route(host, "GET", "/chris/drawing/snapshot", null, "?task_id=draw_t")["error"],
                    Is.EqualTo("Snapshot rate limit; retry after 1 s"));

                // Regression (D101): drawing flips stroke_active, so Observe() makes the gateway's
                // soft, non-revoking Stop. That must keep the frame and the next batch's lease.
                host.State["stroke_active"] = true; host.Tick();
                host.State["stroke_active"] = false; host.Tick();
                Assert.That(CHRISDrawingRuns.Find("draw_t"), Is.Not.Null, "a manual state change keeps the drawing");
                Assert.That((string)Route(host, "GET", "/chris/drawing/snapshot", null, "?task_id=draw_t")["error"],
                    Is.EqualTo("Snapshot rate limit; retry after 1 s"), "the frame still exists (only rate limited)");
                var third = Acquire(host, "draw_t", "draw");
                Assert.That(third["error"], Is.Null, "the next batch can still lease after a manual state change: " + third);
                Route(host, "POST", "/chris/procedure/" + (string)third["lease_id"] + "/release");
                CHRISHandAuthority.ResetForPlay();

                // A 3D task: same route, one 1024 px grid2x2 JPEG.
                var solid = Acquire(host, "solid_t", "draw3d");
                Assert.That(solid["error"], Is.Null, solid.ToString());
                Assert.That(CHRISDrawingRuns.Find("solid_t").Frame.Box, Is.True);
                Route(host, "POST", "/chris/procedure/" + (string)solid["lease_id"] + "/release");
                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)Acquire(host, "solid_t", "draw")["error"], Is.EqualTo("This task has a 3D drawing box"));
                var grid = Route(host, "GET", "/chris/drawing/snapshot", null, "?task_id=solid_t");
                Assert.That(grid["error"], Is.Null, grid.ToString());
                Assert.That((string)grid["layout"], Is.EqualTo("grid2x2"));
                Assert.That((int)grid["width"], Is.EqualTo(1024));
                Assert.That((int)grid["height"], Is.EqualTo(1024));
                var gridKeys = TestCHRISProcedure.SharedValidKeysWhere("drawing_snapshot", d => d["layout"] != null);
                if (gridKeys != null)
                    Assert.That(grid.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(gridKeys));

                // The task deadline at acquire: one about to pass grants nothing and does not burn
                // the task; a later one caps expires_at.
                var past = new JObject { ["task_id"] = "deadline_t", ["host_session"] = host.Capture()["host_session"],
                    ["authority_epoch"] = host.Capture()["authority_epoch"], ["revision"] = host.Capture()["revision"],
                    ["hand"] = "brush", ["channels"] = new JArray("pose", "trigger"), ["task_deadline"] = host.Clock + 0.5 };
                Assert.That((string)Route(host, "POST", "/chris/procedure/acquire", past)["error"], Is.EqualTo("Task deadline already reached"));
                past["task_deadline"] = host.Clock + 10;
                var capped = Route(host, "POST", "/chris/procedure/acquire", past);
                Assert.That(capped["error"], Is.Null, "the refused deadline did not invalidate the task: " + capped);
                Assert.That((double)capped["expires_at"] - (double)capped["granted_at"], Is.EqualTo(10).Within(0.5));
                Assert.That(CHRISHandAuthority.Find((string)capped["lease_id"]).SecondsLeft(Time.realtimeSinceStartup),
                    Is.EqualTo(10).Within(0.5));
                Route(host, "POST", "/chris/procedure/" + (string)capped["lease_id"] + "/release");
                CHRISHandAuthority.ResetForPlay();

                // Atlas review item 2: cancelling between batches (no lease active) ends the run, so
                // the task cannot lease again; the strokes already drawn are kept.
                foreach (var scope in new[] { "draw", "draw3d" })
                {
                    string task = "cancel_" + scope;
                    var leased = Acquire(host, task, scope);
                    Assert.That(leased["error"], Is.Null, leased.ToString());
                    Route(host, "POST", "/chris/procedure/" + (string)leased["lease_id"] + "/release");
                    CHRISHandAuthority.ResetForPlay();
                    Assert.That(CHRISHandAuthority.LeaseActive, Is.False);
                    int strokes = host.StrokeCount;
                    var cancelled = Route(host, "POST", "/chris/cancel", new JObject {
                        ["task_id"] = task, ["host_session"] = host.Capture()["host_session"] });
                    Assert.That((bool?)cancelled["cancelled"], Is.True, cancelled.ToString());
                    Assert.That(CHRISDrawingRuns.Find(task), Is.Null, scope + ": cancellation ends the run");
                    Assert.That((string)Acquire(host, task, scope)["error"], Is.EqualTo("Task authority invalidated"), scope);
                    Assert.That(host.StrokeCount, Is.EqualTo(strokes), "completed strokes are kept");
                }

                host.Stop();
                Assert.That(CHRISDrawingRuns.Find("draw_t"), Is.Null, "Stop ends the drawing");
                Assert.That((string)Acquire(host, "draw_t", "draw")["error"], Is.EqualTo("Task authority invalidated"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                CHRISHandAuthority.ResetForPlay();
                CHRISDrawingRuns.ResetForPlay();
            }
        }
    }
}
