// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TiltBrush
{
    public class TestCHRISMappingAuthority
    {
        static JObject BuildApplyRequest(JObject status, string json, string requestId) => new JObject {
            ["request_id"] = requestId,
            ["expected_session_id"] = status["session_id"].DeepClone(),
            ["expected_revision"] = status["revision"].DeepClone(),
            ["expected_active_digest"] = status["active_digest"].DeepClone(),
            ["proposed_digest"] = CHRISCommandGateway.Hash(json),
            ["mapping_json"] = json
        };

        static bool SameMappingStatus(JObject before, JObject now) => before != null &&
            JToken.DeepEquals(before["session_id"], now["session_id"]) &&
            JToken.DeepEquals(before["revision"], now["revision"]) &&
            JToken.DeepEquals(before["active_digest"], now["active_digest"]);

        static string CandidateOutcome(JObject status, string json, string requestId)
        {
            string digest = CHRISCommandGateway.Hash(json);
            if ((string)status["pending_digest"] == digest) return null;
            if ((string)status["pending_digest"] == null && (string)status["active_digest"] == digest)
                return "Mapping active.";
            string receipt = (string)status["last_request_id"] == requestId ?
                (string)status["last_request_result"] : "superseded";
            return "Mapping " + (receipt ?? "cancelled") + ". Review again before submitting.";
        }
        sealed class FakeInput : ICHRISInputState
        {
            public readonly HashSet<string> Held = new HashSet<string>();
            public bool StopPressedThisFrame { get; set; }
            public bool IsPressed(string input) => Held.Contains(input);
            public float WheelNotches => 0;
            public Vector2 MouseDelta => Vector2.zero;
        }

        static JObject Wire()
        {
            // Use a local copy beside generated cases when needed; otherwise read the schema
            // owner's checked-in wire example directly.
            string path = Path.Combine(TestCHRISInputMapping.Fixtures, "mapping_wire.json");
            if (!File.Exists(path)) path = Path.GetFullPath("../chris/tests/core/fixtures/mapping_wire.json");
            return JObject.Parse(File.ReadAllText(path));
        }

        static void Tick(CHRISMappingAuthority authority, CHRISInputRemap remap, FakeInput input,
            bool focused = true, bool stroke = false)
        {
            authority.TryActivate(input, focused, stroke, remap);
            remap.Tick(input, focused, stroke);
            if (input.StopPressedThisFrame) authority.Stop();
            else authority.CompleteActivation(remap);
        }

        [Test]
        public void SharedWireDigestPendingActivationAndLostAcknowledgement()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string proposed = (string)wire["apply_request"]["mapping_json"];
            string path = Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);
                var active = authority.Status();
                Assert.That((string)active["active_digest"], Is.EqualTo((string)wire["before"]["active_digest"]));
                Assert.That((string)active["active_mapping_json"], Is.EqualTo(before));

                var request = (JObject)wire["apply_request"].DeepClone();
                request["expected_session_id"] = active["session_id"];
                request["expected_revision"] = active["revision"];
                var pending = authority.Apply(request);
                Assert.That((string)pending["result"], Is.EqualTo("pending_neutral"));
                Assert.That((string)pending["pending_digest"], Is.EqualTo((string)wire["pending_reply"]["pending_digest"]));
                Assert.That((string)pending["pending_mapping_json"], Is.EqualTo(proposed));
                Assert.That((string)pending["pending_request_id"], Is.EqualTo("r-7"));
                Assert.That((string)authority.Status()["last_request_result"], Is.EqualTo("pending_neutral"),
                    "A lost HTTP reply is resolved from native status");
                Assert.That(File.ReadAllText(path), Is.EqualTo(before), "Pending must not write the file");

                input.Held.Add("key.space");
                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("pending_neutral"));
                input.Held.Clear();
                Tick(authority, remap, input);
                var after = authority.Status();
                Assert.That((string)after["state"], Is.EqualTo("active"));
                Assert.That((string)after["active_digest"], Is.EqualTo((string)wire["active_status"]["active_digest"]));
                Assert.That((string)after["active_mapping_json"], Is.EqualTo(proposed));
                Assert.That((string)after["active_request_id"], Is.EqualTo("r-7"));
                Assert.That((string)after["last_request_result"], Is.EqualTo("active"));
                Assert.That((long)after["revision"], Is.EqualTo((long)pending["revision"] + 1));
                Assert.That(File.ReadAllText(path), Is.EqualTo(proposed), "Only activation persists exact approved bytes");
                Assert.That((string)authority.Apply(request)["reason"], Is.EqualTo("duplicate_request"),
                    "A replayed request cannot reactivate after an uncertain reply");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        static string TempPath() => Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N") + ".json");

        [Test]
        public void InvalidSavedFileIsReplacedOnlyByAReviewedApply()
        {
            var wire = Wire();
            string proposed = (string)wire["apply_request"]["mapping_json"];
            var invalidFiles = new Dictionary<string, byte[]> {
                ["schema-invalid JSON"] = Encoding.UTF8.GetBytes("{\"version\": \"v0.1.1\", \"mappings\": ["),
                ["bad UTF-8"] = new byte[] { 0x7B, 0xFF, 0xFE, 0x7D },
                ["too large"] = Encoding.UTF8.GetBytes(new string(' ', CHRISInputMapping.MaxBytes + 10) + "{}"),
            };
            foreach (var kv in invalidFiles)
            {
                string path = TempPath();
                File.WriteAllBytes(path, kv.Value);
                try
                {
                    var authority = new CHRISMappingAuthority(path);
                    var remap = new CHRISInputRemap();
                    var input = new FakeInput();
                    authority.NoteFileRejected();
                    var status = authority.Status();
                    Assert.That(authority.SavedFileInvalid, Is.True, kv.Key);
                    Assert.That((string)status["state"], Is.EqualTo("none"), kv.Key + ": wire state unchanged");
                    Assert.That(status["active_digest"].Type, Is.EqualTo(JTokenType.Null), kv.Key);

                    var reply = authority.Apply(BuildApplyRequest(status, proposed, "headset-invalid"));
                    Assert.That((string)reply["result"], Is.EqualTo("pending_neutral"), kv.Key + ": " + (string)reply["reason"]);
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(kv.Value), kv.Key + ": pending never writes");
                    Tick(authority, remap, input);
                    Assert.That((string)authority.Status()["state"], Is.EqualTo("active"), kv.Key);
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(new UTF8Encoding(false).GetBytes(proposed)),
                        kv.Key + ": exact approved bytes persisted");
                    Assert.That(authority.SavedFileInvalid, Is.False, kv.Key);
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
        }

        [Test]
        public void FileChangedAfterRejectedOrValidLoadStillRejectsApply()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string proposed = (string)wire["apply_request"]["mapping_json"];

            string invalidPath = TempPath();
            File.WriteAllText(invalidPath, "{ not json", new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(invalidPath);
                authority.NoteFileRejected();
                File.WriteAllText(invalidPath, "{ still not json, but different", new UTF8Encoding(false));
                var reply = authority.Apply(BuildApplyRequest(authority.Status(), proposed, "r-invalid"));
                Assert.That((string)reply["reason"], Is.EqualTo("file_changed"), "The rejected file changed before apply");
                Assert.That(File.ReadAllText(invalidPath), Is.EqualTo("{ still not json, but different"), "Never overwritten");
            }
            finally { if (File.Exists(invalidPath)) File.Delete(invalidPath); }

            string validPath = TempPath();
            File.WriteAllText(validPath, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(validPath);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);
                File.WriteAllText(validPath, before + " ", new UTF8Encoding(false));
                var reply = authority.Apply(BuildApplyRequest(authority.Status(), proposed, "r-valid"));
                Assert.That((string)reply["reason"], Is.EqualTo("file_changed"), "A valid file edited after load");
                Assert.That((string)authority.Status()["state"], Is.EqualTo("active"), "The active mapping is kept");
                Assert.That(authority.SavedFileInvalid, Is.False);
            }
            finally { if (File.Exists(validPath)) File.Delete(validPath); }
        }

        [Test]
        public void MappingRequestsReportActiveCancelledSupersededAndStale()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string proposed = (string)wire["apply_request"]["mapping_json"];
            string path = TempPath();
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);

                // Confirm -> pending -> active.
                var reviewBase = authority.Status();
                Assert.That(SameMappingStatus(reviewBase, authority.Status()), Is.True);
                Assert.That((string)authority.Apply(BuildApplyRequest(reviewBase, proposed, "headset-1"))["result"],
                    Is.EqualTo("pending_neutral"));
                Assert.That(CandidateOutcome(authority.Status(), proposed, "headset-1"), Is.Null, "Still pending");
                Tick(authority, remap, input);
                Assert.That(CandidateOutcome(authority.Status(), proposed, "headset-1"), Is.EqualTo("Mapping active."));

                // Stale: the status moved after review, so Confirm is refused and the apply is rejected.
                Assert.That(SameMappingStatus(reviewBase, authority.Status()), Is.False);
                Assert.That((string)authority.Apply(BuildApplyRequest(reviewBase, before, "headset-stale"))["reason"],
                    Is.EqualTo("stale_status"));

                // Cancelled: a pending candidate stopped by Escape.
                var reviewAgain = authority.Status();
                Assert.That((string)authority.Apply(BuildApplyRequest(reviewAgain, before, "headset-2"))["result"],
                    Is.EqualTo("pending_neutral"));
                input.StopPressedThisFrame = true;
                Tick(authority, remap, input);
                input.StopPressedThisFrame = false;
                Assert.That(CandidateOutcome(authority.Status(), before, "headset-2"),
                    Is.EqualTo("Mapping cancelled. Review again before submitting."));

                // Superseded: after that, another request (not ours) becomes the latest.
                Assert.That((string)authority.Apply(BuildApplyRequest(authority.Status(), proposed, "backend-3"))["result"],
                    Is.EqualTo("pending_neutral"));
                Assert.That(CandidateOutcome(authority.Status(), before, "headset-2"),
                    Is.EqualTo("Mapping superseded. Review again before submitting."));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ApprovalReviewedBeforeTheInvalidFileChangedIsStale()
        {
            var wire = Wire();
            string proposed = (string)wire["apply_request"]["mapping_json"];
            string path = TempPath();
            File.WriteAllText(path, "{ invalid A", new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                authority.NoteFileRejected();
                var reviewed = authority.Status();

                // The invalid file is replaced on disk by another invalid file, then reloaded.
                File.WriteAllText(path, "{ invalid B", new UTF8Encoding(false));
                authority.NoteFileRejected();
                var now = authority.Status();
                Assert.That((long)now["revision"], Is.GreaterThan((long)reviewed["revision"]), "The changed file advances the revision");
                Assert.That((string)now["state"], Is.EqualTo("none"), "Wire state unchanged");

                var reply = authority.Apply(BuildApplyRequest(reviewed, proposed, "headset-old"));
                Assert.That((string)reply["reason"], Is.EqualTo("stale_status"), "The old approval is refused");
                Assert.That(File.ReadAllText(path), Is.EqualTo("{ invalid B"), "The new file is untouched");

                // Re-reviewed against the current file, the replacement is accepted.
                Assert.That((string)authority.Apply(BuildApplyRequest(now, proposed, "headset-new"))["result"],
                    Is.EqualTo("pending_neutral"));

                // Reloading the same unchanged file does not move the revision.
                authority.DiscardPending();
                long revision = (long)authority.Status()["revision"];
                authority.NoteFileRejected();
                Assert.That((long)authority.Status()["revision"], Is.EqualTo(revision), "Same bytes, same revision");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void OnlyPersistedActivationsCountAsSavedFileWrites()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string proposed = (string)wire["apply_request"]["mapping_json"];
            string path = TempPath();
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);
                Assert.That(authority.PersistCount, Is.Zero, "Activating the loaded file writes nothing");
                Assert.That((string)authority.Apply(BuildApplyRequest(authority.Status(), proposed, "headset-1"))["result"],
                    Is.EqualTo("pending_neutral"));
                Assert.That(authority.PersistCount, Is.Zero, "Pending writes nothing");
                Tick(authority, remap, input);
                Assert.That(authority.PersistCount, Is.EqualTo(1),
                    "A persisted approval counts, so the popup's saved-file cache (SavedFileRevision) is invalidated");
                input.StopPressedThisFrame = true;
                Tick(authority, remap, input);
                Assert.That(authority.PersistCount, Is.EqualTo(1), "Stop writes nothing");
                Assert.That(File.ReadAllText(path), Is.EqualTo(proposed), "After Stop the saved file is the approved B");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void StopCancelsPendingWithoutWritingAndStaleRequestCannotReviveIt()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string path = Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);
                var request = (JObject)wire["apply_request"].DeepClone();
                request["expected_session_id"] = authority.Status()["session_id"];
                request["expected_revision"] = authority.Status()["revision"];
                Assert.That((string)authority.Apply(request)["result"], Is.EqualTo("pending_neutral"));
                input.StopPressedThisFrame = true;
                Tick(authority, remap, input);
                var stopped = authority.Status();
                Assert.That((string)stopped["state"], Is.EqualTo("stopped"));
                Assert.That(stopped["active_digest"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That(stopped["pending_digest"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That((string)stopped["last_request_result"], Is.EqualTo("cancelled"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(before));
                input.StopPressedThisFrame = false;
                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("stopped"));
                Assert.That((string)authority.Apply(request)["reason"], Is.EqualTo("duplicate_request"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ExternalFileChangeCancelsPendingAndPreservesActive()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string path = Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);
                var request = (JObject)wire["apply_request"].DeepClone();
                request["expected_session_id"] = authority.Status()["session_id"];
                request["expected_revision"] = authority.Status()["revision"];
                authority.Apply(request);
                string external = before + "\n";
                File.WriteAllText(path, external, new UTF8Encoding(false));
                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("active"));
                Assert.That((string)authority.Status()["active_mapping_json"], Is.EqualTo(before));
                Assert.That((string)authority.Status()["last_request_result"], Is.EqualTo("cancelled"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(external), "An external edit is never overwritten");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void UnfocusedPendingMappingWaitsForFocusAndNeutralBeforePersistence()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string proposed = (string)wire["apply_request"]["mapping_json"];
            string path = Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                authority.OfferLoaded(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(before)), Encoding.UTF8.GetBytes(before));
                Tick(authority, remap, input);
                var request = (JObject)wire["apply_request"].DeepClone();
                request["expected_session_id"] = authority.Status()["session_id"];
                request["expected_revision"] = authority.Status()["revision"];
                var pending = authority.Apply(request);
                Assert.That((string)pending["result"], Is.EqualTo("pending_neutral"));

                Tick(authority, remap, input, focused: false);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("pending_neutral"),
                    "A neutral frame without XR focus cannot activate the mapping");
                Assert.That(File.ReadAllText(path), Is.EqualTo(before));

                input.Held.Add("key.space");
                Tick(authority, remap, input, focused: false);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("pending_neutral"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(before), "Focus loss cannot persist a candidate");

                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("pending_neutral"),
                    "The old input held through refocus still blocks activation");
                input.Held.Clear();
                Tick(authority, remap, input, stroke: true);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("pending_neutral"),
                    "An existing stroke still blocks activation");
                input.Held.Add("mouse.left");
                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("pending_neutral"),
                    "The candidate's held draw input still blocks activation");
                Assert.That(File.ReadAllText(path), Is.EqualTo(before));

                input.Held.Clear();
                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["state"], Is.EqualTo("active"));
                Assert.That((string)authority.Status()["active_request_id"], Is.EqualTo("r-7"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(proposed),
                    "The exact approved bytes persist only on a focused neutral frame");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ManualReloadReportsExactPendingAndFailedReloadKeepsActive()
        {
            var wire = Wire();
            string before = (string)wire["before"]["active_mapping_json"];
            string proposed = (string)wire["apply_request"]["mapping_json"];
            string path = Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, before, new UTF8Encoding(false));
            try
            {
                var authority = new CHRISMappingAuthority(path);
                var remap = new CHRISInputRemap();
                var input = new FakeInput();
                var store = new CHRISInputMappingStore();
                Assert.That(store.Reload(path), Is.True);
                authority.OfferLoaded(store.Current, store.CurrentBytes);
                Tick(authority, remap, input);

                File.WriteAllText(path, proposed, new UTF8Encoding(false));
                Assert.That(store.Reload(path), Is.True);
                authority.OfferLoaded(store.Current, store.CurrentBytes);
                var pending = authority.Status();
                Assert.That((string)pending["state"], Is.EqualTo("pending_neutral"));
                Assert.That((string)pending["active_mapping_json"], Is.EqualTo(before));
                Assert.That((string)pending["pending_mapping_json"], Is.EqualTo(proposed));
                Assert.That(pending["pending_request_id"].Type, Is.EqualTo(JTokenType.Null));
                Tick(authority, remap, input);
                Assert.That((string)authority.Status()["active_mapping_json"], Is.EqualTo(proposed));

                File.WriteAllText(path, "not JSON", new UTF8Encoding(false));
                Assert.That(store.Reload(path), Is.False);
                Assert.That((string)authority.Status()["active_mapping_json"], Is.EqualTo(proposed),
                    "A rejected hand-authored reload leaves the runtime mapping active");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void MappingEnvelopeHasItsOwnBoundedTransportLimit()
        {
            Assert.That(CHRISCommandGateway.MaxBodyForPath("/chris/mapping/activate"), Is.EqualTo(49152));
            Assert.That(CHRISCommandGateway.MaxBodyForPath("/chris/commands"), Is.EqualTo(16384));
            string document = (string)Wire()["apply_request"]["mapping_json"];
            string padded = document.Insert(document.Length - 1, new string('\n', CHRISInputMapping.MaxBytes - Encoding.UTF8.GetByteCount(document)));
            Assert.That(Encoding.UTF8.GetByteCount(padded), Is.EqualTo(CHRISInputMapping.MaxBytes));
            Assert.That(CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(padded)), Is.Not.Null);
            var envelope = (JObject)Wire()["apply_request"].DeepClone();
            envelope["mapping_json"] = padded;
            Assert.That(Encoding.UTF8.GetByteCount(envelope.ToString(Newtonsoft.Json.Formatting.None)),
                Is.LessThan(CHRISCommandGateway.MaxBodyForPath("/chris/mapping/activate")),
                "An otherwise valid 16 KiB mapping can be escaped inside the transport envelope");
        }
    }
}
