// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TiltBrush
{
    public class TestCHRISInputMapping
    {
        // The shared cases are generated (gitignored) in the C:\Dev\chris repository and read in
        // place, not copied. A test-only override can select a fresh generated case set when the
        // default ignored fixture directory is unavailable; CHRIS_REPO selects another checkout.
        internal static string Fixtures => System.Environment.GetEnvironmentVariable("CHRIS_MAPPING_TEST_FIXTURES") is string fixtures
            && fixtures.Length > 0 ? Path.GetFullPath(fixtures) :
            Path.Combine(System.Environment.GetEnvironmentVariable("CHRIS_REPO") is string repo && repo.Length > 0
                ? repo : Path.GetFullPath("../chris"), "schemas/v0.1.1/fixtures");
        internal static readonly StringBuilder Verdicts = new StringBuilder();

        static string Code(byte[] raw)
        {
            try { CHRISInputMapping.Parse(raw); return "valid"; }
            catch (CHRISMappingException error) { return error.Code; }
        }

        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string DrawOnly(string id = "draw") =>
            "{\"version\":\"v0.1.1\",\"app\":\"openbrush\",\"profile\":\"keyboard_mouse\",\"mappings\":[{\"id\":\"" + id +
            "\",\"source\":{\"type\":\"mouse_button\",\"button\":\"left\"},\"action\":\"draw\"}]}";

        [Test]
        public void EverySharedCaseGetsTheReferenceCode()
        {
            string cases = Path.Combine(Fixtures, "cases.json");
            Assert.That(File.Exists(cases), Is.True, "Shared mapping cases not found: " + cases +
                ". Generate them with `uv run python -m chris.core.mapping_cases` in the chris repository (or set CHRIS_REPO).");
            Verdicts.Clear();
            var failures = 0;
            var list = JArray.Parse(File.ReadAllText(cases));
            Assert.That(list.Count, Is.GreaterThan(0));
            foreach (var item in list)
            {
                string file = (string)item["file"], expected = (string)item["expect"];
                string actual;
                try { CHRISInputMapping.Load(Path.Combine(Fixtures, file)); actual = "valid"; }
                catch (CHRISMappingException error) { actual = error.Code + " (" + error.Detail + ")"; }
                bool pass = actual.Split(' ')[0] == expected;
                if (!pass) failures++;
                Verdicts.AppendLine($"{(pass ? "PASS" : "FAIL")}\t{file}\texpect={expected}\tgot={actual}");
            }
            Assert.That(failures, Is.Zero, Verdicts.ToString());
        }

        [Test]
        public void EncodingSizeAndSyntaxEdges()
        {
            byte[] valid = Utf8(DrawOnly());
            Assert.That(Code(valid), Is.EqualTo("valid"));
            Assert.That(Code(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(valid).ToArray()), Is.EqualTo("valid"), "BOM accepted");
            Assert.That(Code(new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(valid).ToArray()), Is.EqualTo("invalid_json"));
            byte[] Padded(int size) => valid.Concat(Enumerable.Repeat((byte)' ', size - valid.Length)).ToArray();
            Assert.That(Code(Padded(CHRISInputMapping.MaxBytes)), Is.EqualTo("valid"));
            Assert.That(Code(Padded(CHRISInputMapping.MaxBytes + 1)), Is.EqualTo("too_large"));
            Assert.That(Code(Utf8(DrawOnly().Replace("\"draw\"}", "\"dr\\u0061w\"}"))), Is.EqualTo("valid"), "escapes decode before checks");
            Assert.That(Code(Utf8(DrawOnly("draw\\n"))), Is.EqualTo("schema"), "an id may not end in a newline");
            Assert.That(Code(Utf8(DrawOnly().Replace("\"app\"", "\"App\""))), Is.EqualTo("schema"), "field names are case-sensitive");
            Assert.That(Code(Utf8(DrawOnly().Replace("\"profile\"", "\"\\u0076ersion\""))), Is.EqualTo("invalid_json"));
            Assert.That(Code(Utf8(DrawOnly() + "x")), Is.EqualTo("invalid_json"));
            Assert.That(Code(Utf8(DrawOnly().Replace("\"v0.1.1\"", "NaN"))), Is.EqualTo("schema"));
            Assert.That(Code(Utf8("[" + DrawOnly() + ",]")), Is.EqualTo("invalid_json"));
            Assert.That(Code(Array.Empty<byte>()), Is.EqualTo("invalid_json"));
            Assert.That(Code(valid.Take(20).Concat(new byte[] { 0xFF }).Concat(valid.Skip(20)).ToArray()), Is.EqualTo("invalid_json"));
            Assert.That(Code(Utf8(new string('[', 5000) + new string(']', 5000))), Is.EqualTo("invalid_json"),
                "deep nesting is rejected without recursing through the stack");
        }

        [Test]
        public void PublishedExampleBindsEveryAction()
        {
            var mapping = CHRISInputMapping.Load(Path.Combine(Fixtures, "valid_example.json"));
            foreach (CHRISMappedAction action in Enum.GetValues(typeof(CHRISMappedAction)))
                Assert.That(mapping.Find(action), Is.Not.Null, action.ToString());
            var vector = mapping.Mappings.First(m => m.Source == CHRISMappingSource.KeyVector2);
            Assert.That(new[] { vector.Up, vector.Down, vector.Left, vector.Right }.All(k => k != null), Is.True);
        }

        [Test]
        public void RejectedFileKeepsThePreviousMapping()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chris-mapping-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new CHRISInputMappingStore();
                string path = CHRISInputMappingStore.PathUnder(dir);
                Assert.That(store.Reload(path), Is.False);
                Assert.That(store.LastResult, Is.EqualTo("missing"));
                Assert.That(store.Current, Is.Null, "No file means no mapping and unchanged Open Brush input");

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, DrawOnly());
                Assert.That(store.Reload(path), Is.True);
                var first = store.Current;
                Assert.That(first.Find(CHRISMappedAction.Draw).Button, Is.EqualTo("left"));

                File.WriteAllText(path, DrawOnly("Draw"));
                Assert.That(store.Reload(path), Is.False);
                Assert.That(store.LastResult, Is.EqualTo("schema"));
                Assert.That(store.Current, Is.SameAs(first));

                File.WriteAllBytes(path, new byte[CHRISInputMapping.MaxBytes + 1]);
                Assert.That(store.Reload(path), Is.False);
                Assert.That(store.LastResult, Is.EqualTo("too_large"));
                Assert.That(store.Current, Is.SameAs(first));

                File.Delete(path);
                Assert.That(store.Reload(path), Is.False);
                Assert.That(store.LastResult, Is.EqualTo("missing"));
                Assert.That(store.Current, Is.SameAs(first));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
