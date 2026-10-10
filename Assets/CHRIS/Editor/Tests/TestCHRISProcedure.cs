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
    // Controller-procedure lease: wire validation, local timing, revocation, hand ownership and
    // the rule that the agent path only reaches the palette through controller input.
    public class TestCHRISProcedure
    {
        const string Target = "brush:00000000-0000-0000-0000-000000000001";
        const string Other = "brush:00000000-0000-0000-0000-000000000002";
        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static JObject Physical() => new JObject { ["source"] = "physical", ["mode"] = null, ["selected_hand"] = null };
        static CHRISProcedureExecutor Lease(float now = 0) =>
            new CHRISProcedureExecutor("lease", "task", Physical(), Vector3.zero, Quaternion.identity, now);
        static JObject Aim(int seq, string id = null, string target = Target, int timeout = 2000) => new JObject {
            ["step_id"] = id ?? "aim" + seq, ["seq"] = seq, ["kind"] = "aim", ["target_id"] = target, ["timeout_ms"] = timeout };
        static JObject Press(int seq, string expected = Target, int timeout = 2000) => new JObject {
            ["step_id"] = "press" + seq, ["seq"] = seq, ["kind"] = "press", ["button"] = "trigger",
            ["expected_hover_target_id"] = expected, ["timeout_ms"] = timeout };
        // Target straight ahead within reach; the panel faces away from the user (+z).
        static CHRISProcedureFrame Frame(float now, string hover = null, bool found = true, bool interactable = true) =>
            new CHRISProcedureFrame { Now = now, HoverTargetId = hover, TargetFound = found, TargetInteractable = interactable,
                TargetCenter = new Vector3(0, 0, 3), TargetForward = Vector3.forward,
                AttachLocalRotation = Quaternion.identity };
        static string StepStatus(CHRISProcedureExecutor lease, int index) => (string)lease.Status()["steps"][index]["status"];

        static string Backend => System.Environment.GetEnvironmentVariable("CHRIS_REPO") is string repo && repo.Length > 0
            ? repo : Path.GetFullPath("../chris");
        // Generated, gitignored message-shape cases from the CHRIS repository; Unity never runs Python.
        static JArray SharedCases()
        {
            string path = Path.Combine(Backend, "schemas/procedure/v0.1/fixtures/cases.json");
            Assert.That(File.Exists(path), Is.True, path + " missing; run `uv run --locked python -m chris.core.procedure_cases` in the CHRIS repository");
            var file = JObject.Parse(File.ReadAllText(path));
            Assert.That((string)file["version"], Is.EqualTo("procedure-v0.1"));
            return (JArray)file["cases"];
        }
        static string[] ValidKeys(string message) => ((JObject)SharedCases().First(c =>
            (string)c["message"] == message && (string)c["expect"] == "valid")["document"]).Properties().Select(p => p.Name).OrderBy(n => n).ToArray();
        internal static int SharedCasesChecked;
        internal static string[] SharedValidKeys(string message) => ValidKeys(message);
        internal static JObject SharedCase(string name) => (JObject)SharedCases().First(c => (string)c["name"] == name)["document"];

        [Test]
        public void SharedProcedureCasesMatchNativeMessageValidation()
        {
            int checkedCases = 0;
            foreach (JObject c in SharedCases())
            {
                string message = (string)c["message"], name = (string)c["name"];
                var document = (JObject)c["document"];
                string error = message == "acquire_request" ? CHRISCommandGateway.ValidateAcquire(document) :
                    message == "step" ? CHRISProcedureExecutor.ValidateStep(document) :
                    message == "test_display" ? CHRISTestDisplay.Validate(document) :
                    message == "test_events_query" ? (document.Count == 1 && document["after"]?.Type == JTokenType.Integer &&
                        CHRISCommandGateway.EventsAfter("?after=" + (long)document["after"]) != null ? null : "Invalid after") : "skip";
                if (error == "skip") continue;
                Assert.That(error == null, Is.EqualTo((string)c["expect"] == "valid"), name + ": " + error);
                checkedCases++;
            }
            Assert.That(checkedCases, Is.GreaterThanOrEqualTo(20));
            SharedCasesChecked = checkedCases;
        }

        [Test]
        public void AcquireRefusesUnlessFocusedNeutralAndUnowned()
        {
            var clear = new CHRISAcquireFacts { ControllersReady = true, Focused = true };
            Assert.That(CHRISHandAuthority.AcquireRefusal(clear), Is.Null);
            var cases = new (Func<CHRISAcquireFacts, CHRISAcquireFacts> set, string reason)[]
            {
                (f => { f.ControllersReady = false; return f; }, "Controllers unavailable"),
                (f => { f.Focused = false; return f; }, "Not focused"),
                (f => { f.LeaseActive = true; return f; }, "Another procedure lease is active"),
                (f => { f.Busy = true; return f; }, "Stroke, grab or widget interaction in progress"),
                (f => { f.KeyboardUI = true; return f; }, "Keyboard UI pointer is active"),
                (f => { f.MappingChangePending = true; return f; }, "Mapping change pending"),
                (f => { f.LegacyBrushPose = true; return f; }, "move_brush mapping owns the brush pose"),
                (f => { f.PhysicalHeld = true; return f; }, "Physical brush controller input held"),
                (f => { f.MappedHeld = true; return f; }, "Mapped brush input held"),
                (f => { f.MouseHeld = true; return f; }, "Mouse button held"),
            };
            foreach (var (set, reason) in cases) Assert.That(CHRISHandAuthority.AcquireRefusal(set(clear)), Is.EqualTo(reason));
            Assert.That(CHRISHandAuthority.Owner(true, true), Is.EqualTo(CHRISHandOwner.Mapped));
            Assert.That(CHRISHandAuthority.Owner(false, false), Is.EqualTo(CHRISHandOwner.Physical));
        }

        [Test]
        public void StepsAreStrictSequencedAndNeverResent()
        {
            var lease = Lease();
            var hold = Press(1); hold["hold_ms"] = 100;
            var point = Aim(1); point.Remove("target_id"); point["target_point"] = new JArray(0, 0, 3);
            var extra = Aim(1); extra["params"] = new JObject();
            var grip = Press(1); grip["button"] = "grip";
            var floatSeq = Aim(1); floatSeq["seq"] = 1.0;
            foreach (var (step, reason) in new[] {
                (hold, "Invalid step fields"), (point, "Invalid step fields"), (extra, "Invalid step fields"),
                (grip, "Unsupported button"), (Aim(2), "Unexpected seq"), (Aim(0), "seq out of range"),
                (Aim(1, timeout: 0), "timeout_ms out of range"), (Aim(1, timeout: 2001), "timeout_ms out of range"),
                (Aim(1, target: "brush:ink"), "Invalid target_id"), (Aim(1, id: "bad/id"), "Invalid step_id"),
                (floatSeq, "Invalid step numbers"), (new JObject { ["kind"] = "wait" }, "Unknown step kind") })
                Assert.That(lease.Submit(step, 0), Is.EqualTo(reason), step.ToString());
            Assert.That(lease.Submit(Aim(1), 0), Is.Null, "rejected steps do not consume seq 1");
            Assert.That(lease.Submit(Press(2), 0), Is.EqualTo("Another step is in progress"));
            lease.Tick(Frame(0.01f, Target)); lease.Tick(Frame(0.02f, Target)); lease.Tick(Frame(0.03f, Target));
            Assert.That(StepStatus(lease, 0), Is.EqualTo("succeeded"));
            Assert.That(lease.Submit(Aim(1), 0.04f), Is.EqualTo("Unexpected seq"), "a resent step is rejected");
            Assert.That(lease.Submit(Aim(2, id: "aim1"), 0.04f), Is.EqualTo("Duplicate step_id"));
            Assert.That(lease.Submit(Press(2), 0.04f), Is.Null);
        }

        [Test]
        public void AimKeepsEveryButtonReleasedAndNeedsThreeHoverFrames()
        {
            var lease = Lease();
            lease.Submit(Aim(1), 0);
            float t = 0;
            foreach (string hover in new[] { Other, Target, Target, Other, Target, Target })
            {
                lease.Tick(Frame(t += 0.011f, hover));
                Assert.That(lease.Hand.IsNeutral, Is.True, "no button is down while aiming");
                Assert.That(lease.Hand.Down(VrInput.Trigger) || lease.Hand.Down(VrInput.Grip), Is.False);
                Assert.That(StepStatus(lease, 0), Is.EqualTo("pending"));
            }
            lease.Tick(Frame(t += 0.011f, Target));
            Assert.That(StepStatus(lease, 0), Is.EqualTo("succeeded"));
            Assert.That(lease.Hand.IsNeutral, Is.True);
        }

        [Test]
        public void AimTurnsThePointerOntoTheTargetAndMovesOnlyWhenOutOfReach()
        {
            // Attach point 0.5 units ahead of the grip and pitched down 30 degrees, like a controller tip.
            Vector3 attachPosition = new Vector3(0, 0, 0.5f);
            Quaternion attachRotation = Quaternion.Euler(30, 0, 0);
            Vector3 position = Vector3.zero, origin = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            Vector3 near = new Vector3(1, 1, 3);
            for (int i = 0; i < 60; i++)
                CHRISProcedureExecutor.AimTowards(ref position, ref rotation, attachPosition, attachRotation, near, Vector3.forward, origin, 0.02f);
            Assert.That(position, Is.EqualTo(Vector3.zero), "a reachable target only turns the hand");
            Vector3 attach = position + rotation * attachPosition;
            Assert.That(Vector3.Angle(rotation * attachRotation * Vector3.forward, near - attach), Is.LessThan(0.5f));

            Vector3 far = new Vector3(0, 0, 12);
            Vector3 before = position;
            CHRISProcedureExecutor.AimTowards(ref position, ref rotation, attachPosition, attachRotation, far, Vector3.forward, origin, 0.02f);
            Assert.That(Vector3.Distance(position, before), Is.LessThanOrEqualTo(CHRISProcedureExecutor.MoveUnitsPerSecond * 0.02f + 1e-4f));
            for (int i = 0; i < 400; i++)
                CHRISProcedureExecutor.AimTowards(ref position, ref rotation, attachPosition, attachRotation, far, Vector3.forward, origin, 0.02f);
            attach = position + rotation * attachPosition;
            Assert.That(Vector3.Distance(attach, far), Is.LessThanOrEqualTo(CHRISProcedureExecutor.AimReachUnits + 0.01f));
            Assert.That(Vector3.Angle(rotation * attachRotation * Vector3.forward, far - attach), Is.LessThan(0.5f));

            Vector3 unreachable = new Vector3(0, 0, 60);
            for (int i = 0; i < 1000; i++)
                CHRISProcedureExecutor.AimTowards(ref position, ref rotation, attachPosition, attachRotation, unreachable, Vector3.forward, origin, 0.02f);
            Assert.That(Vector3.Distance(position, origin), Is.LessThanOrEqualTo(CHRISBimanualInput.MaxHandOffsetUnits + 1e-3f));
        }

        [Test]
        public void PressHoldsLocallyWithinTheCapAndPresentsOneReleaseEdge()
        {
            var lease = Lease();
            lease.Submit(Press(1), 0);
            lease.Tick(Frame(0.011f, Target));
            Assert.That(lease.Hand.Down(VrInput.Trigger), Is.True);
            Assert.That(lease.Hand.TriggerValue, Is.EqualTo(1f));
            Vector3 position = lease.Hand.Position;
            float t = 0.011f;
            while (lease.Hand.TriggerHeld)
            {
                lease.Tick(Frame(t += 0.011f, Target));
                Assert.That(t, Is.LessThan(0.011f + CHRISProcedureExecutor.MaxPressHoldSeconds + 0.012f));
            }
            Assert.That(t - 0.011f, Is.GreaterThanOrEqualTo(CHRISProcedureExecutor.PressHoldSeconds));
            Assert.That(lease.Hand.Up(VrInput.Trigger), Is.True);
            Assert.That(StepStatus(lease, 0), Is.EqualTo("pending"));
            lease.Tick(Frame(t += 0.011f, Target));
            Assert.That(StepStatus(lease, 0), Is.EqualTo("succeeded"));
            Assert.That(lease.Hand.Up(VrInput.Trigger), Is.False);
            Assert.That(lease.Hand.Position, Is.EqualTo(position), "the pose stays fixed during the press");

            // A long frame still releases on the next tick; the cap is never extended.
            var slow = Lease();
            slow.Submit(Press(1), 0);
            slow.Tick(Frame(0.01f, Target));
            slow.Tick(Frame(0.6f, Target));
            Assert.That(slow.Hand.TriggerHeld, Is.False);
        }

        [Test]
        public void PressRefusesAWrongHoverAndTargetsMustBeOnThePage()
        {
            var lease = Lease();
            lease.Submit(Press(1), 0);
            lease.Tick(Frame(0.01f, Other));
            Assert.That(StepStatus(lease, 0), Is.EqualTo("rejected"));
            Assert.That(lease.Hand.IsNeutral, Is.True);

            var missing = Lease();
            missing.Submit(Aim(1), 0);
            missing.Tick(Frame(0.01f, found: false));
            Assert.That(StepStatus(missing, 0), Is.EqualTo("failed"));

            var flipping = Lease();
            flipping.Submit(Aim(1, timeout: 100), 0);
            flipping.Tick(Frame(0.05f, Target, interactable: false));
            flipping.Tick(Frame(0.11f, Target, interactable: false));
            Assert.That(StepStatus(flipping, 0), Is.EqualTo("timeout"));
            Assert.That((string)flipping.Status()["steps"][0]["reason"], Is.EqualTo("Target not interactable"));
        }

        [Test]
        public void HeartbeatExpiryAndLocalStopReleaseImmediately()
        {
            var lease = Lease();
            lease.Submit(Press(1), 0);
            lease.Tick(Frame(0.01f, Target));
            Assert.That(lease.Hand.TriggerHeld, Is.True);
            lease.Tick(Frame(1.02f, Target));
            Assert.That(lease.State, Is.EqualTo(CHRISLeaseState.Revoked));
            Assert.That((string)lease.Status()["reason"], Is.EqualTo("Heartbeat lost"));
            Assert.That(lease.Hand.IsNeutral, Is.True);
            Assert.That(StepStatus(lease, 0), Is.EqualTo("cancelled"));
            Assert.That(lease.Submit(Aim(2), 1.03f), Is.EqualTo("Lease is not active"));
            lease.Heartbeat(1.04f);
            Assert.That(lease.State, Is.EqualTo(CHRISLeaseState.Revoked), "a late heartbeat cannot revive authority");

            var expiring = Lease();
            for (float t = 0.5f; t < CHRISProcedureExecutor.MaxLeaseSeconds; t += 0.5f) { expiring.Heartbeat(t); expiring.Tick(Frame(t)); }
            expiring.Heartbeat(30f);
            expiring.Tick(Frame(30f));
            Assert.That(expiring.State, Is.EqualTo(CHRISLeaseState.Expired));

            var stopped = Lease();
            stopped.Submit(Aim(1), 0);
            var frame = Frame(0.01f, Target);
            frame.Revocation = "Stopped locally";
            stopped.Tick(frame);
            Assert.That(stopped.State, Is.EqualTo(CHRISLeaseState.Revoked));
            Assert.That(StepStatus(stopped, 0), Is.EqualTo("cancelled"));
            stopped.Release();
            Assert.That(stopped.State, Is.EqualTo(CHRISLeaseState.Revoked), "release after revocation keeps the reason");
        }

        static JObject Route(CHRISGatewayTestHost host, string method, string path, JObject body = null)
        {
            var requestType = typeof(CHRISCommandGateway).GetNestedType("Request", BindingFlags.NonPublic);
            var request = Activator.CreateInstance(requestType, true);
            TestCHRISAssistance.Set(request, "Method", method);
            TestCHRISAssistance.Set(request, "Path", path);
            TestCHRISAssistance.Set(request, "Body", body?.ToString() ?? "{}");
            try { return (JObject)typeof(CHRISCommandGateway).GetMethod("Route", InstanceFlags).Invoke(host, new[] { request }); }
            catch (TargetInvocationException error) { return new JObject { ["error"] = error.InnerException?.Message }; }
        }

        static JObject AcquireBody(JObject context, string task) => new JObject {
            ["task_id"] = task, ["host_session"] = context["host_session"], ["authority_epoch"] = context["authority_epoch"],
            ["revision"] = context["revision"], ["hand"] = "brush", ["channels"] = new JArray("pose", "trigger") };

        [Test]
        public void GatewayLeaseRoutesExcludeOneShotCommandsAndReleaseIdempotently()
        {
            CHRISHandAuthority.ResetForPlay();
            var obj = new GameObject("CHRIS procedure gateway host");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>();
                host.Initialize();
                var context = host.Capture();
                Assert.That(context.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("context")));
                var stale = AcquireBody(context, "task_a"); stale["authority_epoch"] = (long)context["authority_epoch"] + 1;
                Assert.That((string)Route(host, "POST", "/chris/procedure/acquire", stale)["error"], Is.EqualTo("Stale context or host session"));
                var wrongChannels = AcquireBody(context, "task_a"); wrongChannels["channels"] = new JArray("trigger", "pose");
                Assert.That(Route(host, "POST", "/chris/procedure/acquire", wrongChannels)["error"], Is.Not.Null);
                var wand = AcquireBody(context, "task_a"); wand["hand"] = "wand";
                Assert.That(Route(host, "POST", "/chris/procedure/acquire", wand)["error"], Is.Not.Null);

                var lease = Route(host, "POST", "/chris/procedure/acquire", AcquireBody(context, "task_a"));
                Assert.That(lease["error"], Is.Null, lease.ToString());
                string id = (string)lease["lease_id"];
                Assert.That(lease.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("lease")));
                Assert.That((double)lease["expires_at"] - (double)lease["granted_at"], Is.GreaterThan(0).And.LessThanOrEqualTo(30));

                var envelope = TestCHRISAssistance.Envelope(host, new JArray(TestCHRISAssistance.Action("brush.size", "number", 0.3)));
                Assert.That((string)host.Submit(envelope)["reason"], Is.EqualTo("A controller procedure is active"));
                Assert.That((string)Route(host, "POST", "/chris/procedure/acquire", AcquireBody(host.Capture(), "task_b"))["error"],
                    Is.EqualTo("Another procedure lease is active"));

                var accepted = Route(host, "POST", "/chris/procedure/" + id + "/steps", Aim(1));
                Assert.That((bool)accepted["accepted"], Is.True);
                Assert.That(accepted.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("step_accepted")));
                var status = Route(host, "GET", "/chris/procedure/" + id);
                Assert.That((string)status["state"], Is.EqualTo("active"));
                Assert.That(status.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("lease_status")));
                Assert.That((string)status["steps"][0]["status"], Is.EqualTo("pending"));

                var cancelled = Route(host, "POST", "/chris/cancel", new JObject {
                    ["task_id"] = "task_a", ["host_session"] = context["host_session"] });
                Assert.That((bool?)cancelled["cancelled"], Is.True, cancelled.ToString());
                status = Route(host, "GET", "/chris/procedure/" + id);
                Assert.That((string)status["state"], Is.EqualTo("revoked"));
                Assert.That((string)status["steps"][0]["status"], Is.EqualTo("cancelled"));
                for (int i = 0; i < 2; i++)
                {
                    var release = Route(host, "POST", "/chris/procedure/" + id + "/release");
                    Assert.That((bool)release["released"], Is.True);
                    Assert.That(release.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("release")));
                    Assert.That((bool)release["buttons_neutral"], Is.True);
                    Assert.That((string)release["restored_mode"]["source"], Is.EqualTo("physical"));
                }
                Assert.That(Route(host, "POST", "/chris/procedure/" + id + "/release", new JObject { ["x"] = 1 })["error"], Is.Not.Null);
                Assert.That((string)Route(host, "GET", "/chris/procedure/" + new string('0', 32))["error"], Is.EqualTo("Unknown lease"));

                CHRISHandAuthority.ResetForPlay();
                Assert.That((string)Route(host, "POST", "/chris/procedure/acquire", AcquireBody(host.Capture(), "task_a"))["error"],
                    Is.EqualTo("Task authority invalidated"), "one lease per task");
                var second = Route(host, "POST", "/chris/procedure/acquire", AcquireBody(host.Capture(), "task_c"));
                Assert.That(second["error"], Is.Null);
                host.Stop();
                Assert.That((string)Route(host, "GET", "/chris/procedure/" + (string)second["lease_id"])["reason"], Is.EqualTo("Stopped locally"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                CHRISHandAuthority.ResetForPlay();
            }
        }

        [Test]
        public void ManualBrushChangesDoNotRevokeTheLeaseThatCausedThem()
        {
            CHRISHandAuthority.ResetForPlay();
            var obj = new GameObject("CHRIS procedure revision host");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>();
                host.Initialize();
                host.State["brushes"]["other"] = "Other";
                var lease = Route(host, "POST", "/chris/procedure/acquire", AcquireBody(host.Capture(), "task_rev"));
                host.State["brush_id"] = "other";
                host.Tick();
                var status = Route(host, "GET", "/chris/procedure/" + (string)lease["lease_id"]);
                Assert.That((string)status["state"], Is.EqualTo("active"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                CHRISHandAuthority.ResetForPlay();
            }
        }

        // Live headset run 2026-10-03: the saved mapping set HandBackPending while the terminal had
        // focus, and that stale flag revoked every lease on its first tick.
        [Test]
        public void OnlyMappedChangesDuringTheLeaseCountAsTakeover()
        {
            var ui = CHRISControlMode.UI;
            Assert.That(CHRISHandAuthority.MappedTakeover(ui, true, false, ui, true, false), Is.Null,
                "a hand-back flag already set at acquire is not a new request");
            Assert.That(CHRISHandAuthority.MappedTakeover(ui, false, false, ui, false, false), Is.Null);
            Assert.That(CHRISHandAuthority.MappedTakeover(ui, false, false, ui, true, false), Is.EqualTo("Manual takeover"), "F6");
            Assert.That(CHRISHandAuthority.MappedTakeover(ui, true, false, ui, false, false), Is.EqualTo("Manual takeover"),
                "a mode key clearing the stale flag");
            Assert.That(CHRISHandAuthority.MappedTakeover(ui, true, false, CHRISControlMode.Position, false, false),
                Is.EqualTo("Manual takeover"), "F2");
            Assert.That(CHRISHandAuthority.MappedTakeover(ui, false, false, ui, false, true), Is.EqualTo("Manual takeover"), "F5");
        }

        [Test]
        public void StepsLogStartAndEndWithHoverFramesAndHold()
        {
            var lines = new System.Collections.Generic.List<string>();
            var lease = Lease();
            lease.Log = lines.Add;
            lease.Submit(Aim(1), 0);
            for (int i = 1; i <= 3; i++) lease.Tick(Frame(i * 0.011f, Target));
            lease.Submit(Press(2), 0.04f);
            float t = 0.04f;
            while (StepStatus(lease, 1) == "pending") lease.Tick(Frame(t += 0.011f, Target));
            Assert.That(lines.Count, Is.EqualTo(4), string.Join("\n", lines));
            Assert.That(lines[1], Does.Contain("aim succeeded").And.Contain("hover frames 3"));
            Assert.That(lines[3], Does.Contain("press succeeded").And.Contain("trigger held 1"));
        }

        [Test]
        public void ReleasingAnActiveLeaseReportsHandBackPendingDuringTheGraceFrame()
        {
            CHRISHandAuthority.ResetForPlay();
            var obj = new GameObject("CHRIS procedure release host");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>();
                host.Initialize();
                var lease = Route(host, "POST", "/chris/procedure/acquire", AcquireBody(host.Capture(), "task_release"));
                var release = Route(host, "POST", "/chris/procedure/" + (string)lease["lease_id"] + "/release");
                Assert.That((bool)release["released"], Is.True);
                Assert.That((bool)release["hand_back_pending"], Is.True, "the released hand is still presented this frame");
                var status = Route(host, "GET", "/chris/procedure/" + (string)lease["lease_id"]);
                Assert.That((string)status["state"], Is.EqualTo("released"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                CHRISHandAuthority.ResetForPlay();
            }
        }

        // Open Brush reads mouse buttons as Activate/AltActivate/Panic outside the controller path;
        // both InputManager mouse readers must report nothing while a procedure owns the Brush hand.
        [Test]
        public void MouseButtonsAreIgnoredWhileAProcedureOwnsTheBrush()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts/InputManager.cs"));
            foreach (string method in new[] { "public bool GetMouseButton(int button)", "public bool GetMouseButtonDown(int button)" })
            {
                int start = source.IndexOf(method, StringComparison.Ordinal);
                Assert.That(start, Is.GreaterThan(0), method);
                int guard = source.IndexOf("return false;", start, StringComparison.Ordinal);
                Assert.That(source.Substring(start, guard - start), Does.Contain("CHRISHandAuthority.OwnsBrush"), method);
            }
            string authority = File.ReadAllText(Path.Combine(Application.dataPath, "CHRIS/Runtime/CHRISHandAuthority.cs"));
            Assert.That(authority, Does.Contain("return \"Mouse input during the lease\""));
        }

        static JObject Display(string nonce = null, int ttl = 30, params (int id, string title)[] cases) => new JObject {
            ["lines"] = new JArray("Next: case 1 Happy path", "Click Start"), ["nonce"] = nonce,
            ["buttons"] = nonce == null ? new JArray() : new JArray("approve", "decline"),
            ["cases"] = new JArray(cases.Select(c => new JObject { ["id"] = c.id, ["title"] = c.title })), ["ttl_s"] = ttl };

        [Test]
        public void TestDisplayValidatesStrictlyAndApprovalsNeedALiveNonce()
        {
            Assert.That(CHRISTestDisplay.Validate(Display(null, 30, (1, "Happy path"), (3, "Trigger squeeze"))), Is.Null);
            Assert.That(CHRISTestDisplay.Validate(Display("n1")), Is.Null);
            var noNonce = Display(); noNonce["buttons"] = new JArray("approve", "decline");
            var extra = Display(); extra["hold"] = 1;
            var longLine = Display(); longLine["lines"] = new JArray(new string('x', 81));
            var dupCase = Display(null, 30, (1, "a"), (1, "b"));
            foreach (var bad in new[] { noNonce, extra, longLine, dupCase, Display(null, 0), Display(null, 61), Display("bad/nonce") })
                Assert.That(CHRISTestDisplay.Validate(bad), Is.Not.Null, bad.ToString());
            // D89 reasoning: optional; absent and null are equal; at most 400 chars, no control characters.
            JObject WithReasoning(JToken value, string nonce = null) { var d = Display(nonce); d["reasoning"] = value; return d; }
            foreach (var good in new[] { WithReasoning(JValue.CreateNull()), WithReasoning(new string('r', 400)), WithReasoning("Fire looks hot", "n1") })
                Assert.That(CHRISTestDisplay.Validate(good), Is.Null, good.ToString());
            foreach (var bad in new[] { WithReasoning(new string('r', 401)), WithReasoning(3), WithReasoning("two\nlines"), WithReasoning("tab\t") })
                Assert.That(CHRISTestDisplay.Validate(bad), Is.EqualTo("Invalid reasoning"), bad.ToString());
            // D105: an exit clear after a live display says the session ended; staleness leaves
            // that line alone, and the next session's first display opens the panel again.
            var ending = new CHRISTestDisplay();
            Assert.That(ending.Show(Display(null, 30, (1, "a")), 0, out bool firstOpened) >= 0 && firstOpened, Is.True);
            ending.PanelOpened();
            var clear = Display(); clear["lines"] = new JArray();
            ending.Show(clear, 1, out bool clearOpened);
            Assert.That(ending.Lines, Is.EqualTo(new[] { CHRISTestDisplay.SessionEnded }));
            Assert.That(clearOpened || ending.OpenPending, Is.False);
            ending.Tick(1 + CHRISTestDisplay.StaleSeconds + 1);
            Assert.That(ending.Lines, Is.EqualTo(new[] { CHRISTestDisplay.SessionEnded }), "staleness keeps the ended line");
            ending.Show(clear, 200, out _);
            Assert.That(ending.Lines, Is.EqualTo(new[] { CHRISTestDisplay.SessionEnded }), "a second clear changes nothing");
            ending.Show(Display(null, 30, (1, "a")), 201, out bool reopened);
            Assert.That(reopened && ending.OpenPending, Is.True, "a new session after the ended line reopens the panel");
            var lost = Display(); lost["lines"] = new JArray("CHRIS session lost", "The drawing may continue. Escape stops it");
            ending.Show(lost, 202, out _);
            ending.Tick(202 + CHRISTestDisplay.StaleSeconds + 1);
            Assert.That(ending.Lines, Is.EqualTo(new[] { CHRISTestDisplay.NotConnected }), "a lost-session display goes stale as before");
            var unused = new CHRISTestDisplay();
            unused.Show(clear, 0, out bool unusedOpened);
            Assert.That(unused.Lines, Is.Empty, "a clear with nothing before it stays empty");
            Assert.That(unusedOpened, Is.False);

            var shown = new CHRISTestDisplay();
            shown.Show(WithReasoning("Velvet Ink: the name suggests ink"), 0);
            Assert.That(shown.Reasoning, Is.EqualTo("Velvet Ink: the name suggests ink"));
            shown.Show(Display(), 1);
            Assert.That(shown.Reasoning, Is.Null, "a display without reasoning clears it");
            shown.Show(WithReasoning("stale"), 2);
            shown.Tick(2 + CHRISTestDisplay.StaleSeconds + 1);
            Assert.That(shown.Reasoning, Is.Null, "a stale display clears reasoning");

            var display = new CHRISTestDisplay();
            Assert.That(display.Decide(true, 0, 1000), Is.False, "no display: approve is inert");
            Assert.That(display.Show(Display(null, 30, (1, "Happy path")), 0), Is.EqualTo(0));
            Assert.That(display.Start(2, 1000), Is.False, "only listed cases start");
            Assert.That(display.Start(1, 1000), Is.True);
            Assert.That(display.Decide(true, 1, 1001), Is.False, "no nonce: approve is inert");
            long seq = display.Show(Display("nonce_a", 30), 2);
            Assert.That(seq, Is.EqualTo(1));
            Assert.That(display.Decide(true, 3, 1003), Is.True);
            Assert.That(display.Decide(false, 3, 1003), Is.False, "one decision per nonce");
            var events = (JArray)display.Events(seq)["events"];
            Assert.That(events.Count, Is.EqualTo(1));
            Assert.That((string)events[0]["kind"], Is.EqualTo("approve"));
            Assert.That((string)events[0]["nonce"], Is.EqualTo("nonce_a"));
            Assert.That(events[0]["case"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((string)display.Events(0)["events"][0]["kind"], Is.EqualTo("start"));

            display.Show(Display("nonce_b", 5), 10);
            display.Tick(15);
            Assert.That(display.Nonce, Is.Null, "expired nonce is dropped");
            Assert.That(display.Lines.Length, Is.EqualTo(2), "the last instruction stays visible");
            Assert.That(display.Decide(true, 15, 1015), Is.False);

            display.Show(Display(null, 30, (1, "a")), 20);
            for (int i = 0; i < 40; i++) display.Start(1, 1020 + i);
            var ring = display.Events(0);
            Assert.That(((JArray)ring["events"]).Count, Is.EqualTo(CHRISTestDisplay.RingSize));
            Assert.That((long)ring["latest"], Is.EqualTo(42));

            // A session start opens the panel once; refreshes do not reopen it.
            var fresh = new CHRISTestDisplay();
            fresh.Show(Display(null, 60, (4, "Already selected")), 100, out bool opened);
            Assert.That(opened, Is.True);
            fresh.Show(Display(null, 60, (4, "Already selected")), 130, out opened);
            Assert.That(opened, Is.False);
            // A runner that stops without clearing leaves no live buttons behind.
            fresh.Show(Display("nonce_c", 60), 140);
            fresh.Tick(199);
            Assert.That(fresh.ButtonsLive(199), Is.True, "within the nonce ttl");
            fresh.Tick(140 + CHRISTestDisplay.StaleSeconds - 1);
            Assert.That(fresh.Lines, Is.Not.EqualTo(new[] { CHRISTestDisplay.NotConnected }), "not stale yet");
            fresh.Tick(140 + CHRISTestDisplay.StaleSeconds);
            Assert.That(fresh.Lines, Is.EqualTo(new[] { CHRISTestDisplay.NotConnected }));
            Assert.That(fresh.Cases, Is.Empty);
            Assert.That(fresh.Decide(true, 140 + CHRISTestDisplay.StaleSeconds, 2000), Is.False);
            Assert.That(fresh.Start(4, 2000), Is.False);
            fresh.Show(Display(null, 60, (4, "Already selected")), 300, out opened);
            Assert.That(opened, Is.False, "reconnecting after a stale display does not reopen a panel the user may have closed");

            Assert.That(CHRISCommandGateway.EventsAfter("?after=0"), Is.EqualTo(0));
            Assert.That(CHRISCommandGateway.EventsAfter("?after=17"), Is.EqualTo(17));
            foreach (string bad in new[] { "", "?after=-1", "?after=01", "?after=1&x=2", "?after=", "?before=1" })
                Assert.That(CHRISCommandGateway.EventsAfter(bad), Is.Null, bad);

            Assert.That(CHRISPaletteObserver.FacesPanel(Vector3.zero, Vector3.forward, new Vector3(0, 0, 3), 70), Is.True);
            Assert.That(CHRISPaletteObserver.FacesPanel(Vector3.zero, Vector3.forward, new Vector3(3, 0, 0.5f), 70), Is.False);
            Assert.That(CHRISNativePopup.CaseHover(3, "Trigger squeeze stop"), Is.EqualTo("Start case 3: Trigger squeeze stop"));
        }

        [Test]
        public void TestDisplayRoutesShowAndReportEvents()
        {
            CHRISTestDisplay.ResetForPlay();
            var obj = new GameObject("CHRIS test display host");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>();
                host.Initialize();
                Assert.That(Route(host, "POST", "/chris/test/display", Display("nonce_x", 0))["error"], Is.Not.Null);
                var shown = Route(host, "POST", "/chris/test/display", Display("nonce_x", 30));
                Assert.That((bool)shown["shown"], Is.True);
                Assert.That(shown.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("test_display_shown")));
                Assert.That((long)shown["seq"], Is.EqualTo(0));
                Assert.That(CHRISTestDisplay.Instance.Nonce, Is.EqualTo("nonce_x"));
                Assert.That(Route(host, "GET", "/chris/test/events")["error"], Is.Not.Null, "after is required");
                CHRISTestDisplay.Instance.Decide(true, Time.realtimeSinceStartup, CHRISCommandGateway.Now);
                var events = CHRISTestDisplay.Instance.Events(0);
                Assert.That(events.Properties().Select(p => p.Name).OrderBy(n => n), Is.EqualTo(ValidKeys("test_events")));
                var validEvent = (JObject)SharedCases().First(c => (string)c["message"] == "test_events" &&
                    (string)c["expect"] == "valid" && ((JArray)c["document"]["events"]).Count > 0)["document"]["events"][0];
                Assert.That(((JObject)events["events"][0]).Properties().Select(p => p.Name).OrderBy(n => n),
                    Is.EqualTo(validEvent.Properties().Select(p => p.Name).OrderBy(n => n)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                CHRISTestDisplay.ResetForPlay();
            }
        }

        // Headset session 2026-10-04: the first display arrived while Open Brush was loading, so
        // a one-shot open failed and later refreshes never retried it.
        [Test]
        public void PanelOpenRequestPersistsUntilThePanelIsOpen()
        {
            var display = new CHRISTestDisplay();
            display.Show(Display(null, 60, (4, "Already selected")), 0);
            Assert.That(display.OpenPending, Is.True);
            display.Show(Display(null, 60, (4, "Already selected")), 30);
            Assert.That(display.OpenPending, Is.True, "a refresh keeps the request until the host opens the panel");
            display.PanelOpened();
            display.Show(Display(null, 60, (4, "Already selected")), 60);
            Assert.That(display.OpenPending, Is.False, "an open panel the user later closes is not reopened");
            var stale = new CHRISTestDisplay();
            stale.Show(Display(null, 60, (4, "a")), 0);
            stale.Tick(CHRISTestDisplay.StaleSeconds);
            Assert.That(stale.OpenPending, Is.False, "a stale display stops asking to open");
            var cleared = new CHRISTestDisplay();
            cleared.Show(Display(null, 60, (4, "a")), 0);
            cleared.Show(new JObject { ["lines"] = new JArray(), ["nonce"] = null, ["buttons"] = new JArray(),
                ["cases"] = new JArray(), ["ttl_s"] = 1 }, 1);
            Assert.That(cleared.OpenPending, Is.False);
        }

        // A lease that took the Brush from physical control must hand its driver back before a
        // mapping in UI mode runs, or the mapping would keep the held driver as its own pose.
        [Test]
        public void PhysicalReturnLeaseGivesTheBrushDriverBack()
        {
            var brush = new GameObject("CHRIS hand-back brush");
            try
            {
                var driver = brush.AddComponent<UnityEngine.SpatialTracking.TrackedPoseDriver>();
                driver.enabled = true;
                CHRISBimanualHost.RestoreDriverOwnership();
                CHRISBimanualHost.ReconcileDriverOwnership(driver, null);
                Assert.That(driver.enabled, Is.False);
                Assert.That(CHRISBimanualHost.OwnsBrushPose, Is.True);
                CHRISBimanualHost.BeginPhysicalHandBack();
                Assert.That(CHRISBimanualHost.FinishPhysicalHandBack(busy: true), Is.False, "waits for a stroke or grab");
                Assert.That(CHRISBimanualHost.OwnsBrushPose, Is.True);
                Assert.That(CHRISBimanualHost.FinishPhysicalHandBack(busy: false), Is.True);
                Assert.That(driver.enabled, Is.True);
                Assert.That(CHRISBimanualHost.OwnsBrushPose, Is.False);
                Assert.That(CHRISBimanualHost.OwnsWandPose, Is.False);
            }
            finally
            {
                CHRISBimanualHost.RestoreDriverOwnership();
                UnityEngine.Object.DestroyImmediate(brush);
            }
        }

        // A malformed palette would make the service reject every context, not only the procedure.
        static void AssertPaletteValues(JObject palette)
        {
            int page = (int)palette["page"], count = (int)palette["page_count"];
            Assert.That(palette["visible"].Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(count, Is.GreaterThanOrEqualTo(1));
            Assert.That(page, Is.InRange(0, count - 1));
            var targets = (JArray)palette["targets"];
            Assert.That(targets.Count, Is.LessThanOrEqualTo(CHRISPaletteObserver.MaxTargets));
            Assert.That(targets.Select(t => (string)t["target_id"]).Distinct().Count(), Is.EqualTo(targets.Count));
            foreach (JObject t in targets)
            {
                string brush = (string)t["brush_id"];
                Assert.That(brush, Is.EqualTo(Guid.Parse(brush).ToString("D")), "lowercase Guid D format");
                Assert.That((string)t["target_id"], Is.EqualTo("brush:" + brush));
                Assert.That(((string)t["label"]).Length, Is.LessThanOrEqualTo(80));
                Assert.That(t["interactable"].Type, Is.EqualTo(JTokenType.Boolean));
                var center = t["center_room"];
                Assert.That(center.Type == JTokenType.Null || (center is JArray a && a.Count == 3 &&
                    a.All(v => (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) &&
                        !double.IsNaN((double)v) && !double.IsInfinity((double)v))), Is.True, t.ToString());
            }
        }

        [Test]
        public void PaletteContextValuesStayWithinTheServiceContract()
        {
            var a = Guid.Parse("CB92B597-94CA-4255-B017-0E3F42F12F9E");
            var buttons = Enumerable.Range(0, 70).Select(i => (Guid.NewGuid(), "Brush " + i, new Vector3(i, 1, 2), i % 2 == 0))
                .Prepend((a, new string('x', 120), new Vector3(float.NaN, 0, 0), true))
                .Prepend((a, "Duplicate first", Vector3.one, true)).ToList();
            var palette = CHRISPaletteObserver.BuildPalette(buttons, 7, 3);
            AssertPaletteValues(palette);
            var targets = (JArray)palette["targets"];
            Assert.That(targets.Count, Is.EqualTo(CHRISPaletteObserver.MaxTargets));
            Assert.That((string)targets[0]["brush_id"], Is.EqualTo("cb92b597-94ca-4255-b017-0e3f42f12f9e"));
            Assert.That((int)palette["page"], Is.EqualTo(2));
            var invalid = CHRISPaletteObserver.BuildPalette(new[] { (a, new string('y', 120), new Vector3(float.PositiveInfinity, 0, 0), false) }, -1, 0);
            AssertPaletteValues(invalid);
            Assert.That(invalid["targets"][0]["center_room"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(((string)invalid["targets"][0]["label"]).Length, Is.EqualTo(80));
            Assert.That((int)invalid["page_count"], Is.EqualTo(1));
            // The fixture's own valid context palette satisfies the same assertions.
            foreach (JObject c in SharedCases().Where(c => (string)c["message"] == "context" && (string)c["expect"] == "valid"))
                if (c["document"]["palette"] is JObject fixture) AssertPaletteValues(fixture);
        }

        // The agent path reaches the palette only through controller input.
        [Test]
        public void AgentPathNeverCallsBrushButtonOrPanelSetters()
        {
            string[] forbidden = { "SetActiveBrush", "ButtonPressed", "ButtonReleased", "OnButtonPressed", "ApiMethods",
                "brush.select", "SetValue(", ".Invoke(", "SendMessage", "IssueGlobalCommand", "ResetPanel", ".Show(",
                "SetActive(", "GotoPage", "m_PageIndex =", "m_RequestedPageIndex =", "m_Brush =", "WidgetSibling" };
            string runtime = Path.Combine(Application.dataPath, "CHRIS/Runtime");
            var sources = new[] { "CHRISProcedureExecutor.cs", "CHRISHandAuthority.cs", "CHRISPaletteObserver.cs" }
                .Select(name => (name, text: File.ReadAllText(Path.Combine(runtime, name)))).ToList();
            string gateway = File.ReadAllText(Path.Combine(runtime, "CHRISCommandGateway.cs"));
            int start = gateway.IndexOf("// ---- Controller procedure lease", StringComparison.Ordinal);
            int end = gateway.IndexOf("CHRISHandAuthority.Acquire(task", start, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThan(0)); Assert.That(end, Is.GreaterThan(start));
            sources.Add(("CHRISCommandGateway.cs procedure routes", gateway.Substring(start, end - start)));
            foreach (var (name, text) in sources)
                foreach (string token in forbidden)
                    Assert.That(text.Contains(token), Is.False, name + " references " + token);
        }
    }
}
