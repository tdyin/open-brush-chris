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

        static void Tick(CHRISMappingAuthority authority, CHRISInputRemap remap, FakeInput input)
        {
            authority.TryActivate(input, true, false, remap);
            remap.Tick(input, true, false);
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
