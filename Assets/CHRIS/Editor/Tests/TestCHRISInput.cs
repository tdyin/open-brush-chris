// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR;

namespace TiltBrush
{
    // input-v0.2 (D117): the generic controller-input executor, its routes, observation,
    // authority and hand-back. No Play mode, HTTP or headset.
    public class TestCHRISInput
    {
        const BindingFlags AnyFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;
        const float Dt = 1 / 90f;
        static readonly Vector3 Start = new Vector3(0.2f, 1.2f, 0.4f);
        static readonly CHRISInputLimits Limits = new CHRISInputLimits { Center = new Vector3(0, 1.3f, 0.5f), RadiusM = 0.6f,
            MaxSpeedMps = 1f, MaxTurnDps = 180f };

        static string Backend => System.Environment.GetEnvironmentVariable("CHRIS_REPO") is string repo && repo.Length > 0
            ? repo : Path.GetFullPath("../chris");
        static JObject SharedFile()
        {
            string path = Path.Combine(Backend, "schemas/input/v0.2/fixtures/cases.json");
            Assert.That(File.Exists(path), Is.True, path + " missing; generate the input-v0.2 cases in the CHRIS repository");
            var file = JObject.Parse(File.ReadAllText(path));
            Assert.That((string)file["version"], Is.EqualTo("input-v0.2"));
            return file;
        }
        static JArray SharedCases() => (JArray)SharedFile()["cases"];
        static JObject ValidDocument(string message) => (JObject)SharedCases().First(c =>
            (string)c["message"] == message && (string)c["expect"] == "valid")["document"];
        static string[] Keys(JObject o) => o.Properties().Select(p => p.Name).OrderBy(n => n).ToArray();
        internal static int SharedCasesChecked;

        static CHRISInputExecutor Lease(float now = 0) =>
            new CHRISInputExecutor("lease", "task", new JObject { ["source"] = "physical" }, 0, Limits, Start, Quaternion.identity, now);

        static JObject S(int t, Vector3 p, bool trigger = false, Quaternion? q = null)
        {
            var r = q ?? Quaternion.identity;
            return new JObject { ["t_ms"] = t, ["position"] = new JArray(p.x, p.y, p.z),
                ["rotation"] = new JArray(r.x, r.y, r.z, r.w), ["trigger"] = trigger };
        }

        static JObject B(int seq, params JObject[] samples) =>
            new JObject { ["batch_id"] = "b" + seq, ["seq"] = seq, ["frame_epoch"] = 0, ["samples"] = new JArray(samples) };

        // A stationary press: down at 50 ms, up at 150 ms.
        static JObject Press(int seq, Vector3 at) => B(seq, S(0, at), S(50, at, true), S(150, at));

        static void Tick(CHRISInputExecutor lease, float now, int frame, string revocation = null)
        {
            lease.Heartbeat(now);
            lease.Tick(new CHRISInputFrame { Now = now, Index = frame, Revocation = revocation });
        }

        static JObject Batch(CHRISInputExecutor lease, int index) => (JObject)lease.Status(t => t)["batches"][index];

        [Test]
        public void SharedInputCasesMatchNativeVerdicts()
        {
            var file = SharedFile();
            var refusals = ((JObject)file["refusals"]).Properties().Select(p => (string)p.Value).ToArray();
            foreach (string native in new[] { CHRISInputExecutor.StartRefusal, CHRISInputExecutor.BoundsRefusalText,
                CHRISInputExecutor.SpeedRefusal, CHRISInputExecutor.TurnRefusal, CHRISInputExecutor.EdgesRefusal,
                CHRISInputExecutor.NeutralRefusal, CHRISInputExecutor.BusyBatch, CHRISInputExecutor.StaleSeq,
                CHRISInputExecutor.Inactive, CHRISInputExecutor.DeadlineRefusal, CHRISInputExecutor.TokenRefusal,
                CHRISInputExecutor.FrameRefusal, CHRISInputExecutor.FieldRefusal, CHRISInputExecutor.HeadRefusal,
                "Task stopped; new approval needed", "Authority changed; new approval needed", "Task deadline already reached",
                "Host busy or unavailable", "Requested limit above the native cap" })
                Assert.That(refusals, Does.Contain(native), "native string not in the shared refusals: " + native);

            int checkedCases = 0;
            foreach (JObject c in SharedCases())
            {
                string message = (string)c["message"], name = (string)c["name"];
                bool valid = (string)c["expect"] == "valid";
                var doc = (JObject)c["document"];
                string verdict;
                switch (message)
                {
                    case "acquire_request": verdict = CHRISCommandGateway.ValidateInputAcquire(doc); break;
                    case "batch": verdict = CHRISInputExecutor.ValidateBatch(doc); break;
                    case "batch_in_lease":
                        var batch = (JObject)doc["batch"];
                        var held = (JObject)doc["held"];
                        var p = (JArray)held["position"];
                        var q = (JArray)held["rotation"];
                        var center = (JArray)doc["bounds"]["center"];
                        verdict = CHRISInputExecutor.ValidateBatch(batch) ?? CHRISInputExecutor.LeaseProblem(batch, (long)doc["frame_epoch"],
                            new Vector3((float)p[0], (float)p[1], (float)p[2]), new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]),
                            new CHRISInputLimits { Center = new Vector3((float)center[0], (float)center[1], (float)center[2]),
                                RadiusM = (float)doc["bounds"]["radius_m"], MaxSpeedMps = (float)doc["max_speed_mps"],
                                MaxTurnDps = (float)doc["max_turn_dps"] });
                        if (!valid && c["reason"] != null) Assert.That(verdict, Is.EqualTo((string)c["reason"]), name);
                        break;
                    default: continue;
                }
                Assert.That(verdict == null, Is.EqualTo(valid), name + ": " + verdict);
                checkedCases++;
            }
            Assert.That(checkedCases, Is.GreaterThanOrEqualTo(40));
            SharedCasesChecked = checkedCases;
        }

        // Bunched edges are never lost: one transition per frame, in sample order, with lateness.
        [Test]
        public void EdgesArriveOnePerFrameInOrderAndThePoseHolds()
        {
            var lease = Lease();
            Assert.That(lease.Submit(B(1, S(0, Start), S(50, Start, true), S(90, Start)), 0), Is.Null);
            Tick(lease, 0, 1);
            Assert.That(lease.Hand.TriggerHeld, Is.False);
            Tick(lease, 0.095f, 2);
            Assert.That(lease.Hand.Down(VrInput.Trigger), Is.True, "down applied at 95 ms");
            Tick(lease, 0.105f, 3);
            Assert.That(lease.Hand.Up(VrInput.Trigger), Is.True, "the up edge, also due, waits one frame");
            var receipt = Batch(lease, 0);
            Assert.That((string)receipt["state"], Is.EqualTo("completed"));
            Assert.That((int)receipt["last_applied_index"], Is.EqualTo(2));
            Assert.That(receipt["edges"].Select(e => (int)e["frame"]), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(receipt["edges"].Select(e => (bool)e["trigger"]), Is.EqualTo(new[] { true, false }));
            Assert.That((double)receipt["late_ms"], Is.EqualTo(45).Within(0.5), "the down edge was 45 ms late");
            Tick(lease, 0.2f, 4);
            Assert.That(lease.Hand.TriggerHeld, Is.False, "holds released after the last sample");
            Assert.That(lease.AppliedPosition, Is.EqualTo(Start));
            Assert.That(lease.Active, Is.True, "a completed batch never ends the lease");

            // Exactly one Sample per frame: a held trigger reports no new edge on the next frame.
            var hold = Lease();
            Assert.That(hold.Submit(B(1, S(0, Start), S(10, Start, true), S(200, Start)), 0), Is.Null);
            Tick(hold, 0, 1);
            Tick(hold, 0.02f, 2);
            Assert.That(hold.Hand.Down(VrInput.Trigger), Is.True);
            Tick(hold, 0.03f, 3);
            Assert.That(hold.Hand.TriggerHeld && !hold.Hand.Down(VrInput.Trigger), Is.True, "held, no repeated edge");
        }

        [Test]
        public void PosesInterpolateLinearlyAndByShortestRotation()
        {
            var lease = Lease();
            var end = Start + new Vector3(0.1f, 0, 0);
            var turned = Quaternion.AngleAxis(30, Vector3.up);
            Assert.That(lease.Submit(B(1, S(0, Start), S(200, end, false, turned)), 0), Is.Null);
            Tick(lease, 0, 1);
            Tick(lease, 0.1f, 2);
            Assert.That(Vector3.Distance(lease.AppliedPosition, Start + new Vector3(0.05f, 0, 0)), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(lease.AppliedRotation, Quaternion.identity), Is.EqualTo(15).Within(0.05f));
            Assert.That(Vector3.Distance(lease.Hand.Position, lease.AppliedPosition * App.METERS_TO_UNITS), Is.LessThan(1e-4f),
                "the hand is the same pose in Open Brush units");
            Tick(lease, 0.25f, 3);
            Assert.That((string)Batch(lease, 0)["state"], Is.EqualTo("completed"));
            var controller = (JArray)lease.Status(t => t)["controller"]["position"];
            Assert.That((float)controller[0], Is.EqualTo(end.x).Within(1e-6f), "status reports the exact last applied pose");

            // The negated quaternion of the same rotation does not spin the long way round.
            var flip = Lease();
            var negated = new Quaternion(-turned.x, -turned.y, -turned.z, -turned.w);
            Assert.That(flip.Submit(B(1, S(0, Start), S(200, Start, false, negated)), 0), Is.Null);
            Tick(flip, 0, 1);
            Tick(flip, 0.1f, 2);
            Assert.That(Quaternion.Angle(flip.AppliedRotation, Quaternion.identity), Is.EqualTo(15).Within(0.05f));
        }

        [Test]
        public void LateEdgesAndStaleObservationsInterruptBeforeThePress()
        {
            var late = Lease();
            Assert.That(late.Submit(Press(1, Start), 0), Is.Null);
            Tick(late, 0, 1);
            Tick(late, 0.2f, 2);
            Assert.That(late.Hand.TriggerHeld, Is.False, "a 150 ms late press is not applied");
            Assert.That((string)Batch(late, 0)["state"], Is.EqualTo("interrupted"));
            Assert.That((string)Batch(late, 0)["reason"], Is.EqualTo("Execution late"));
            Assert.That(late.Active, Is.True);
            Assert.That(late.Submit(B(2, S(0, Start), S(100, Start)), 0.3f), Is.Null, "the lease continues from the held pose");

            string token = "t1";
            var guarded = Lease();
            guarded.TokenSource = () => token;
            var press = Press(1, Start);
            press["if_token"] = "t0";
            Assert.That(guarded.Submit(press, 0, guarded.TokenSource), Is.EqualTo("Observation is stale"));
            press["if_token"] = "t1";
            Assert.That(guarded.Submit(press, 0, guarded.TokenSource), Is.Null, "the refused batch did not use its seq");
            Tick(guarded, 0, 1);
            token = "t2";
            Tick(guarded, 0.06f, 2);
            Assert.That(guarded.Hand.TriggerHeld, Is.False, "the trigger never went down");
            Assert.That((string)Batch(guarded, 0)["reason"], Is.EqualTo("Observation changed before a press"));
            Assert.That(Batch(guarded, 0)["edges"], Is.Empty);

            var fresh = Lease();
            fresh.TokenSource = () => "same";
            var ok = Press(1, Start);
            ok["if_token"] = "same";
            Assert.That(fresh.Submit(ok, 0, fresh.TokenSource), Is.Null);
            for (int i = 0; i <= 20; i++) Tick(fresh, i * Dt, i + 1);
            Assert.That((string)Batch(fresh, 0)["state"], Is.EqualTo("completed"));
            Assert.That(Batch(fresh, 0)["edges"].Count(), Is.EqualTo(2));
        }

        [Test]
        public void BatchesAreRefusedWholeAndNeverClamped()
        {
            var lease = Lease();
            Vector3 x(float dx) => Start + new Vector3(dx, 0, 0);
            var stale = B(1, S(0, Start), S(100, Start));
            stale["frame_epoch"] = 1;
            Assert.That(lease.Submit(stale, 0), Is.EqualTo("Frame changed; observe again"));
            Assert.That(lease.Submit(B(2, S(0, Start), S(100, Start)), 0), Is.EqualTo("Duplicate or stale batch"), "seq gap");
            Assert.That(lease.Submit(B(1, S(0, x(0.003f)), S(100, x(0.003f))), 0), Is.EqualTo("Batch must start at the current pose"));
            Assert.That(lease.Submit(B(1, S(0, Start, false, Quaternion.AngleAxis(1.5f, Vector3.up)), S(100, Start)), 0),
                Is.EqualTo("Batch must start at the current pose"));
            Assert.That(lease.Submit(B(1, S(0, Start), S(1000, Start + new Vector3(0, 0, -0.6f))), 0), Is.EqualTo("Sample outside the bounds"));
            Assert.That(lease.Submit(B(1, S(0, Start), S(100, x(0.15f))), 0), Is.EqualTo("Segment exceeds the speed limit"));
            Assert.That(lease.Submit(B(1, S(0, Start), S(100, Start, false, Quaternion.AngleAxis(20, Vector3.up))), 0),
                Is.EqualTo("Segment exceeds the turn limit"));
            var legacy = B(1, S(0, Start), S(100, Start));
            legacy["target_id"] = "brush:00000000-0000-0000-0000-000000000001";
            Assert.That(lease.Submit(legacy, 0), Is.EqualTo("Unsupported field"));
            Assert.That(lease.Status(t => t)["batches"], Is.Empty, "nothing admitted, nothing moved");
            Assert.That(lease.AppliedPosition, Is.EqualTo(Start));

            Assert.That(lease.Submit(B(1, S(0, Start), S(100, x(0.09f))), 0), Is.Null, "0.9 m/s is admitted");
            Assert.That(lease.Submit(B(2, S(0, x(0.09f)), S(100, x(0.09f))), 0), Is.EqualTo("Batch in progress"));
            Tick(lease, 0, 1);
            Tick(lease, 0.11f, 2);
            Assert.That(lease.Submit(B(1, S(0, x(0.09f)), S(100, x(0.09f))), 0.2f), Is.EqualTo("Duplicate or stale batch"), "duplicate");
            var reused = B(2, S(0, x(0.09f)), S(100, x(0.09f)));
            reused["batch_id"] = "b1";
            Assert.That(lease.Submit(reused, 0.2f), Is.EqualTo("Duplicate or stale batch"), "reused batch_id");

            // Admission against the approved task deadline, on native's clock.
            lease.LimitTo(1);
            Assert.That(lease.Submit(B(2, S(0, x(0.09f)), S(900, x(0.09f))), 0.2f), Is.EqualTo("Batch would pass the task deadline"));
            Assert.That(lease.Submit(B(2, S(0, x(0.09f)), S(700, x(0.09f))), 0.2f), Is.Null);
            lease.Release();
            Assert.That(lease.Submit(B(3, S(0, x(0.09f)), S(100, x(0.09f))), 0.3f), Is.EqualTo("Lease is not active"));
            Assert.That((string)Batch(lease, 1)["state"], Is.EqualTo("interrupted"), "release discards the rest");
        }

        // Any end of the lease releases the trigger in that frame, and the running batch ends
        // interrupted with the lease's own reason.
        [Test]
        public void EveryEndReleasesInFrameAndInterruptsWithTheSameReason()
        {
            foreach (var (revocation, deadline, heartbeat, state, reason) in new[] {
                ("Recenter", false, false, "revoked", "Recenter"),
                ("Takeover: physical input on the leased hand", false, false, "revoked", "Takeover: physical input on the leased hand"),
                (null, true, false, "expired", "Task deadline reached"),
                (null, false, true, "revoked", "Heartbeat lost") })
            {
                var lease = Lease();
                Assert.That(lease.Submit(B(1, S(0, Start), S(10, Start, true), S(800, Start)), 0), Is.Null);
                Tick(lease, 0, 1);
                Tick(lease, 0.02f, 2);
                Assert.That(lease.Hand.TriggerHeld, Is.True);
                if (deadline) lease.LimitTo(0.03f);
                if (heartbeat) lease.Tick(new CHRISInputFrame { Now = 1.1f, Index = 3 });
                else Tick(lease, 0.04f, 3, revocation);
                Assert.That(lease.Hand.Up(VrInput.Trigger), Is.True, reason + ": released in the ending frame");
                var status = lease.Status(t => t);
                Assert.That((string)status["state"], Is.EqualTo(state));
                Assert.That((string)status["reason"], Is.EqualTo(reason));
                Assert.That((string)Batch(lease, 0)["state"], Is.EqualTo("interrupted"));
                Assert.That((string)Batch(lease, 0)["reason"], Is.EqualTo(reason));
            }
        }

        [Test]
        public void AuthorityRulesForTakeoverTrackingAndHandBack()
        {
            Assert.That(CHRISHandAuthority.InputRevocation(0, 1, () => null), Is.EqualTo("Recenter"));
            Assert.That(CHRISHandAuthority.InputRevocation(1, 1, () => null), Is.Null);
            Assert.That(CHRISHandAuthority.InputRevocation(1, 1, () => "Stopped locally"), Is.EqualTo("Stop"));
            Assert.That(CHRISHandAuthority.InputRevocation(1, 1, () => "Focus lost"), Is.EqualTo("Focus lost"));
            Assert.That(CHRISHandAuthority.InputRevocation(1, 1, () => "Physical input on the leased hand"),
                Is.EqualTo("Takeover: physical input on the leased hand"));
            Assert.That(CHRISHandAuthority.InputRevocation(1, 1, () => "Manual takeover"), Is.EqualTo("Takeover: manual takeover"));

            var both = InputTrackingState.Position | InputTrackingState.Rotation;
            Assert.That(CHRISHandAuthority.PoseTracked(true, true, both), Is.True);
            Assert.That(CHRISHandAuthority.PoseTracked(true, true, InputTrackingState.Position), Is.False);
            Assert.That(CHRISHandAuthority.PoseTracked(true, false, both), Is.False);
            Assert.That(CHRISHandAuthority.PoseTracked(false, true, both), Is.False);
            Assert.That(CHRISHandAuthority.PhysicalHandBackPending(false, true, false), Is.False);
            Assert.That(CHRISHandAuthority.PhysicalHandBackPending(true, true, false), Is.True, "CHRIS still holds the driver");
            Assert.That(CHRISHandAuthority.PhysicalHandBackPending(false, false, false), Is.True, "not tracked");
            Assert.That(CHRISHandAuthority.PhysicalHandBackPending(false, true, true), Is.True, "release gate closed");

            // A mapped return continues from the last applied pose, clamped to the mapping's reach.
            Assert.That(CHRISHandAuthority.MappedReturnPosition(new Vector3(0.5f, 0, 0), Vector3.zero), Is.EqualTo(new Vector3(5, 0, 0)));
            Assert.That(CHRISHandAuthority.MappedReturnPosition(new Vector3(5, 0, 0), Vector3.zero),
                Is.EqualTo(new Vector3(CHRISBimanualInput.MaxHandOffsetUnits, 0, 0)));

            Assert.That(CHRISHandAuthority.AcquireRefusal(new CHRISAcquireFacts { ControllersReady = true, Focused = true,
                InputLeaseActive = true }), Is.EqualTo("An input lease is active"), "a legacy acquire during an input lease");
            Assert.That(CHRISInputExecutor.BoundsRefusal(new Vector3(0, 1.6f, 0), Limits, Start), Is.Null);
            Assert.That(CHRISInputExecutor.BoundsRefusal(new Vector3(0, 1.4f, 0.4f), Limits, Start),
                Is.EqualTo("Bounds centre must be 0.25-1.0 m from the head"), "on the face");
            Assert.That(CHRISInputExecutor.BoundsRefusal(new Vector3(0, 1.6f, 0),
                new CHRISInputLimits { Center = new Vector3(0, 1.3f, 0.5f), RadiusM = 0.1f }, Start), Is.EqualTo("Controller outside the bounds"));
        }

        [Test]
        public void ObservedPaletteKeepsTheSharedShape()
        {
            var a = new Guid("cb92b597-94ca-4255-b017-0e3f42f12f9e");
            var palette = CHRISPaletteObserver.BuildObservedPalette(new[] {
                (a, "Fire", new Vector3(0.05f, 1.3f, 0.6f), new Vector2(0.04f, 0.04f), true),
                (a, "Duplicate", Vector3.zero, new Vector2(0.04f, 0.04f), true),
                (Guid.NewGuid(), "No size", Vector3.zero, Vector2.zero, true),
                (Guid.NewGuid(), "Not finite", new Vector3(float.NaN, 0, 0), new Vector2(0.04f, 0.04f), true) },
                0, 2, (new Vector3(0, 1.3f, 0.6f), Quaternion.identity, new Vector2(0.3f, 0.2f)));
            var shared = (JObject)ValidDocument("observation")["palette"];
            Assert.That(Keys(palette), Is.EqualTo(Keys(shared)));
            Assert.That(Keys((JObject)palette["surface"]), Is.EqualTo(Keys((JObject)shared["surface"])));
            Assert.That(((JArray)palette["targets"]).Count, Is.EqualTo(1));
            Assert.That(Keys((JObject)palette["targets"][0]), Is.EqualTo(Keys((JObject)shared["targets"][0])));
            Assert.That(CHRISPaletteObserver.BuildObservedPalette(new (Guid, string, Vector3, Vector2, bool)[0], 0, 1, null)["surface"].Type,
                Is.EqualTo(JTokenType.Null));
        }

        static JObject Route(CHRISGatewayTestHost host, string method, string path, JObject body = null)
        {
            var requestType = typeof(CHRISCommandGateway).GetNestedType("Request", BindingFlags.NonPublic);
            var request = Activator.CreateInstance(requestType, true);
            TestCHRISAssistance.Set(request, "Method", method);
            TestCHRISAssistance.Set(request, "Path", path);
            TestCHRISAssistance.Set(request, "Query", "");
            TestCHRISAssistance.Set(request, "Body", body?.ToString() ?? "{}");
            try
            {
                return (JObject)typeof(CHRISCommandGateway).GetMethod("Route", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(host, new[] { request });
            }
            catch (TargetInvocationException error) { return new JObject { ["error"] = error.InnerException?.Message }; }
        }

        static JObject AcquireBody(CHRISGatewayTestHost host, string task)
        {
            var context = host.Capture();
            return new JObject { ["task_id"] = task, ["host_session"] = context["host_session"],
                ["authority_epoch"] = context["authority_epoch"], ["hand"] = "brush", ["channels"] = new JArray("pose", "trigger"),
                ["task_deadline"] = host.Clock + 20,
                ["bounds"] = new JObject { ["center"] = new JArray(0.0, 1.3, 0.5), ["radius_m"] = 0.6 },
                ["max_speed_mps"] = 1.0, ["max_turn_dps"] = 180.0 };
        }

        static JObject AcquireInput(CHRISGatewayTestHost host, string task) =>
            Route(host, "POST", "/chris/input/acquire", AcquireBody(host, task));

        static void Release(CHRISGatewayTestHost host, JObject grant)
        {
            Route(host, "POST", "/chris/input/" + (string)grant["lease_id"] + "/release");
            CHRISHandAuthority.ResetForPlay();
        }

        [Test]
        public void GatewayInputRoutesObserveExcludeLegacyAndTombstone()
        {
            CHRISHandAuthority.ResetForPlay();
            CHRISRoomFrame.ResetForPlay();
            CHRISDrawingRuns.ResetForPlay();
            var obj = new GameObject("CHRIS input gateway host");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>();
                host.Initialize();
                long epoch = (long)host.Capture()["authority_epoch"];

                // Acquire shapes and refusals that leave the task usable.
                var capped = AcquireBody(host, "t1");
                capped["max_speed_mps"] = 1.6;
                Assert.That((string)Route(host, "POST", "/chris/input/acquire", capped)["error"], Is.EqualTo("Requested limit above the native cap"));
                var face = AcquireBody(host, "t1");
                face["bounds"]["center"] = new JArray(0.0, 1.5, 0.1);
                Assert.That((string)Route(host, "POST", "/chris/input/acquire", face)["error"], Is.EqualTo("Bounds centre must be 0.25-1.0 m from the head"));
                var soon = AcquireBody(host, "t1");
                soon["task_deadline"] = host.Clock + 0.5;
                Assert.That((string)Route(host, "POST", "/chris/input/acquire", soon)["error"], Is.EqualTo("Task deadline already reached"));
                var staleEpoch = AcquireBody(host, "t1");
                staleEpoch["authority_epoch"] = epoch + 1;
                Assert.That((string)Route(host, "POST", "/chris/input/acquire", staleEpoch)["error"], Is.EqualTo("Authority changed; new approval needed"));
                var scoped = AcquireBody(host, "t1");
                scoped["scope"] = "palette";
                Assert.That((string)Route(host, "POST", "/chris/input/acquire", scoped)["error"], Is.EqualTo("Unsupported field"));

                var grant = AcquireInput(host, "t1");
                Assert.That(grant["error"], Is.Null, grant.ToString());
                Assert.That(Keys(grant), Is.EqualTo(Keys(ValidDocument("grant"))));
                Assert.That((string)grant["frame"]["id"], Is.EqualTo("room"));
                Assert.That((double)grant["expires_at"] - (double)grant["granted_at"], Is.EqualTo(20).Within(0.5), "capped by task_deadline");
                Assert.That((float)grant["start"]["position"][0], Is.EqualTo(0.2f).Within(1e-6f));
                string lease = (string)grant["lease_id"], route = "/chris/input/" + lease;

                // Observation and the freshness token.
                var observation = Route(host, "GET", "/chris/observe");
                Assert.That(Keys(observation), Is.EqualTo(Keys(ValidDocument("observation"))), observation.ToString());
                Assert.That((long)observation["frame"]["epoch"], Is.EqualTo(CHRISRoomFrame.Epoch));
                string token = (string)observation["token"];
                host.Observed["brush"]["pose"]["position"] = new JArray(0.3, 1.2, 0.4);
                host.Observed["head"]["position"] = new JArray(0.0, 1.7, 0.0);
                Assert.That((string)Route(host, "GET", "/chris/observe")["token"], Is.EqualTo(token), "the controller and head poses never stale it");

                // Legacy is unreachable while an input lease is active.
                var context = host.Capture();
                var legacy = new JObject { ["task_id"] = "legacy", ["host_session"] = context["host_session"],
                    ["authority_epoch"] = context["authority_epoch"], ["revision"] = context["revision"], ["hand"] = "brush",
                    ["channels"] = new JArray("pose", "trigger") };
                Assert.That(Route(host, "POST", "/chris/procedure/acquire", legacy)["error"], Is.Not.Null);
                var oneShot = host.Submit(TestCHRISAssistance.Envelope(host, new JArray(TestCHRISAssistance.Action("brush.size", "number", 0.3))));
                Assert.That((string)oneShot["status"], Is.EqualTo("rejected"));
                Assert.That((string)Route(host, "POST", "/chris/procedure/" + lease + "/steps", new JObject())["error"], Is.EqualTo("Unknown lease"));
                var legacyBatch = Press(1, Start);
                legacyBatch["kind"] = "stroke";
                Assert.That((string)Route(host, "POST", route + "/batches", legacyBatch)["error"], Is.EqualTo("Unsupported field"));

                var press = Press(1, Start);
                press["if_token"] = token;
                var accepted = Route(host, "POST", route + "/batches", press);
                Assert.That(Keys(accepted), Is.EqualTo(Keys(ValidDocument("batch_accepted"))), accepted.ToString());
                var status = Route(host, "GET", route);
                Assert.That(Keys(status), Is.EqualTo(Keys(ValidDocument("status"))), status.ToString());
                Assert.That(Keys((JObject)status["batches"][0]), Is.EqualTo(Keys((JObject)ValidDocument("status")["batches"][0])));
                Assert.That((string)status["batches"][0]["state"], Is.EqualTo("running"));
                var released = Route(host, "POST", route + "/release");
                Assert.That(Keys(released), Is.EqualTo(Keys(ValidDocument("release"))));
                Assert.That((string)Route(host, "GET", route)["batches"][0]["state"], Is.EqualTo("interrupted"));
                CHRISHandAuthority.ResetForPlay();
                Assert.That((long)host.Capture()["authority_epoch"], Is.EqualTo(epoch), "a normal release keeps authority");
                Assert.That(CHRISDrawingRuns.Find("t1"), Is.Null, "the input path never opens a drawing run");

                // Re-acquire after a normal release; a revoking Stop then tombstones the task.
                var again = AcquireInput(host, "t1");
                Assert.That(again["error"], Is.Null, "re-acquire after a normal release: " + again);
                host.Stop();
                var stopped = Route(host, "GET", "/chris/input/" + (string)again["lease_id"]);
                Assert.That((string)stopped["state"], Is.EqualTo("revoked"));
                Assert.That((string)stopped["reason"], Is.EqualTo("Stop"));
                Assert.That((long)host.Capture()["authority_epoch"], Is.EqualTo(epoch + 1));
                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)AcquireInput(host, "t1")["error"], Is.EqualTo("Task stopped; new approval needed"));

                // A recenter during a lease revokes it, advances the frame epoch and tombstones.
                var t2 = AcquireInput(host, "t2");
                Assert.That(t2["error"], Is.Null, t2.ToString());
                long frame = CHRISRoomFrame.Epoch;
                CHRISRoomFrame.OriginChanged();
                var recentered = Route(host, "GET", "/chris/input/" + (string)t2["lease_id"]);
                Assert.That((string)recentered["state"], Is.EqualTo("revoked"));
                Assert.That((string)recentered["reason"], Is.EqualTo("Recenter"));
                Assert.That((long)Route(host, "GET", "/chris/observe")["frame"]["epoch"], Is.EqualTo(frame + 1));
                Assert.That((long)host.Capture()["authority_epoch"], Is.EqualTo(epoch + 2), "takeover changes authority");
                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)AcquireInput(host, "t2")["error"], Is.EqualTo("Task stopped; new approval needed"));
                var t3 = AcquireInput(host, "t3");
                Assert.That((long)t3["frame"]["epoch"], Is.EqualTo(frame + 1), "the next grant carries the new frame");

                // Cancel tombstones even after a normal release.
                Release(host, t3);
                Route(host, "POST", "/chris/cancel", new JObject { ["task_id"] = "t3", ["host_session"] = host.Capture()["host_session"] });
                Assert.That((string)AcquireInput(host, "t3")["error"], Is.EqualTo("Task stopped; new approval needed"));

                // A procedure lease excludes input acquire.
                context = host.Capture();
                legacy["task_id"] = "legacy2";
                legacy["authority_epoch"] = context["authority_epoch"];
                legacy["revision"] = context["revision"];
                var procedure = Route(host, "POST", "/chris/procedure/acquire", legacy);
                Assert.That(procedure["error"], Is.Null, procedure.ToString());
                Assert.That((string)AcquireInput(host, "t4")["error"], Is.EqualTo("Host busy or unavailable"));
                Route(host, "POST", "/chris/procedure/" + (string)procedure["lease_id"] + "/release");
                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)AcquireInput(host, "legacy2")["error"], Is.EqualTo("Task stopped; new approval needed"),
                    "a task that held a procedure lease cannot take input");

                // The soft Stop on a manual change advances the revision only; focus loss changes authority.
                long before = (long)host.Capture()["authority_epoch"], revision = (long)host.Capture()["revision"];
                host.State["brush_size"] = 0.9;
                host.Tick();
                Assert.That((long)host.Capture()["revision"], Is.EqualTo(revision + 1));
                Assert.That((long)host.Capture()["authority_epoch"], Is.EqualTo(before), "no authority change for a manual edit");
                host.Focus = false;
                host.Tick();
                Assert.That((long)host.Capture()["authority_epoch"], Is.EqualTo(before + 1), "focus loss");
                host.Focus = true;
            }
            finally
            {
                CHRISHandAuthority.ResetForPlay();
                CHRISRoomFrame.ResetForPlay();
                CHRISDrawingRuns.ResetForPlay();
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        // ---- The new executor never reaches the aim, stroke or drawing-run code ----
        // A static IL walk: from the input roots, follow every call into CHRIS input code, and fail
        // on any reference to the procedure executor, the drawing runs, the draw frame or the
        // palette target resolver.

        static readonly Dictionary<short, OpCode> s_OpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)).GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First());

        static List<MemberInfo> References(MethodBase method)
        {
            var found = new List<MemberInfo>();
            var body = method.GetMethodBody();
            if (body == null) return found;
            byte[] il = body.GetILAsByteArray();
            var typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i++];
                if (value == 0xFE) value = unchecked((short)(0xFE00 | il[i++]));
                var op = s_OpCodes[value];
                int size;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: size = 1; break;
                    case OperandType.InlineVar: size = 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: size = 8; break;
                    case OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(il, i); break;
                    default: size = 4; break;
                }
                if (op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField ||
                    op.OperandType == OperandType.InlineTok || op.OperandType == OperandType.InlineType)
                {
                    MemberInfo member = null;
                    try { member = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                    catch (Exception) { }
                    if (member != null) found.Add(member);
                }
                i += size;
            }
            return found;
        }

        static Type Outer(Type type)
        {
            while (type.DeclaringType != null) type = type.DeclaringType;
            return type;
        }

        static readonly Type[] s_Forbidden = { typeof(CHRISProcedureExecutor), typeof(CHRISDrawingRuns), typeof(CHRISDrawFrame) };
        // Input code is followed through these types only; shared host infrastructure is not.
        static readonly Type[] s_Followed = { typeof(CHRISInputExecutor), typeof(CHRISHandAuthority), typeof(CHRISCommandGateway),
            typeof(CHRISPaletteObserver), typeof(CHRISRoomFrame), typeof(CHRISVirtualHand) };

        internal static List<string> ForbiddenReach(IEnumerable<MethodBase> roots)
        {
            var seen = new HashSet<MethodBase>(roots);
            var queue = new Queue<MethodBase>(seen);
            var hits = new List<string>();
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                foreach (var member in References(method))
                {
                    var owner = member as Type ?? member.DeclaringType;
                    if (owner == null) continue;
                    var outer = Outer(owner);
                    // The one allowed reference: whether a procedure lease is active, which the two
                    // lease kinds need to exclude each other. It runs no procedure code.
                    if (outer == typeof(CHRISProcedureExecutor) && member.Name == "get_Active") continue;
                    if (s_Forbidden.Contains(outer) || outer == typeof(CHRISPaletteObserver) && member.Name == "TryResolve")
                        hits.Add($"{method.DeclaringType?.Name}.{method.Name} -> {owner.Name}.{member.Name}");
                    else if (member is MethodBase callee && s_Followed.Contains(outer) && seen.Add(callee)) queue.Enqueue(callee);
                }
            }
            return hits;
        }

        static IEnumerable<MethodBase> Methods(Type type, params string[] names) =>
            type.GetMethods(AnyFlags).Where(m => names.Contains(m.Name)).Cast<MethodBase>();

        static IEnumerable<MethodBase> AllMethods(Type type) =>
            new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                .SelectMany(t => t.GetMethods(AnyFlags).Cast<MethodBase>().Concat(t.GetConstructors(AnyFlags)));

        [Test]
        public void TheInputExecutorNeverReachesAimStrokeOrDrawingRunCode()
        {
            var roots = AllMethods(typeof(CHRISInputExecutor))
                .Concat(Methods(typeof(CHRISCommandGateway), "RouteInput", "AcquireInput", "AcquireInputHand", "Observation",
                    "ObservationToken", "ReadObservation", "TokenOf", "SyncInput", "ValidateInputAcquire", "InputLimits"))
                .Concat(Methods(typeof(CHRISHandAuthority), "AcquireInput", "BeginInput", "TickInput", "RevokeInput", "OnInputEnded",
                    "InputRevocation", "Owners", "BrushTriggerHeld", "TrackedPose"))
                .Concat(typeof(CHRISHandAuthority).GetMethods(AnyFlags).Where(m =>
                    m.GetParameters().Any(p => p.ParameterType == typeof(CHRISInputExecutor))))
                .ToList();
            Assert.That(roots.Count, Is.GreaterThan(30));
            var hits = ForbiddenReach(roots);
            Assert.That(hits, Is.Empty, string.Join("\n", hits));

            // The walk does find the legacy path, so an empty result above means something.
            Assert.That(ForbiddenReach(Methods(typeof(CHRISHandAuthority), "Tick")), Is.Not.Empty, "legacy Tick reaches the procedure");
            Assert.That(ForbiddenReach(Methods(typeof(CHRISHandAuthority), "OnEnded")), Is.Not.Empty, "legacy end reaches drawing runs");
        }
    }
}
