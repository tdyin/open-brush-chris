// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TiltBrush
{
    // In-headset test prompts for the CHRIS test runner. Python pushes what to show; native
    // records only physical clicks on the status panel's buttons. Display carries no authority:
    // an approve event is only valid with the one-time nonce of a live display, and the runner
    // still approves through the service's existing review binding.
    public sealed class CHRISTestDisplay
    {
        public const int MaxLines = 4, MaxLineLength = 80, MaxCases = 8, MaxTitleLength = 60, MaxTtl = 60, RingSize = 32;
        // The runner refreshes its idle display every 30 s; after this long it is presumed gone.
        public const float StaleSeconds = 70;
        public const string NotConnected = "CHRIS tests not connected";
        static readonly Regex s_Id = new Regex("\\A[A-Za-z0-9_-]{1,80}\\z");

        public static CHRISTestDisplay Instance { get; private set; } = new CHRISTestDisplay();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay() => Instance = new CHRISTestDisplay();

        readonly Queue<JObject> m_Events = new Queue<JObject>();
        float m_NonceExpiry, m_LastShow;
        long m_Seq;

        public string[] Lines { get; private set; } = new string[0];
        public string Nonce { get; private set; }
        public (int id, string title)[] Cases { get; private set; } = new (int, string)[0];
        public long Latest => m_Seq;
        // Bumped on every change the panel must redraw.
        public int Version { get; private set; }

        public bool ButtonsLive(float now) => Nonce != null && now < m_NonceExpiry;
        bool Empty => Lines.Length == 0 && Cases.Length == 0;

        static bool IsNull(JToken token) => token.Type == JTokenType.Null || (token is JValue v && v.Value == null);

        public static string Validate(JObject d)
        {
            var fields = new[] { "lines", "nonce", "buttons", "cases", "ttl_s" };
            if (d == null || !d.Properties().All(p => fields.Contains(p.Name)) || !fields.All(f => d[f] != null))
                return "Invalid display fields";
            if (!(d["lines"] is JArray lines) || lines.Count > MaxLines ||
                lines.Any(l => l.Type != JTokenType.String || ((string)l).Length > MaxLineLength))
                return "Invalid lines";
            bool hasNonce = !IsNull(d["nonce"]);
            if (hasNonce && (d["nonce"].Type != JTokenType.String || !s_Id.IsMatch((string)d["nonce"])))
                return "Invalid nonce";
            if (!(d["buttons"] is JArray buttons)) return "Invalid buttons";
            bool hasButtons = buttons.Count > 0;
            if (hasButtons && !JToken.DeepEquals(buttons, new JArray("approve", "decline"))) return "Invalid buttons";
            if (hasButtons != hasNonce) return "Buttons require a nonce";
            if (!(d["cases"] is JArray cases) || cases.Count > MaxCases) return "Invalid cases";
            var ids = new HashSet<long>();
            foreach (var c in cases)
            {
                if (!(c is JObject o) || o.Count != 2 || o["id"]?.Type != JTokenType.Integer || o["title"]?.Type != JTokenType.String ||
                    (long)o["id"] < 1 || (long)o["id"] > 9 || ((string)o["title"]).Length > MaxTitleLength || !ids.Add((long)o["id"]))
                    return "Invalid cases";
            }
            if (d["ttl_s"].Type != JTokenType.Integer || (long)d["ttl_s"] < 1 || (long)d["ttl_s"] > MaxTtl) return "Invalid ttl_s";
            return null;
        }

        // Replaces the whole display. Returns the latest event seq at display time. Opened is
        // true when an empty display becomes non-empty, so the host can open the panel once.
        public long Show(JObject d, float now) => Show(d, now, out _);

        public long Show(JObject d, float now, out bool opened)
        {
            bool wasEmpty = Empty;
            m_LastShow = now;
            Lines = ((JArray)d["lines"]).Select(l => (string)l).ToArray();
            Nonce = IsNull(d["nonce"]) ? null : (string)d["nonce"];
            m_NonceExpiry = now + (long)d["ttl_s"];
            Cases = ((JArray)d["cases"]).Select(c => ((int)(long)c["id"], (string)c["title"])).ToArray();
            Version++;
            opened = wasEmpty && !Empty;
            return m_Seq;
        }

        // Expired buttons disappear; the lines stay as the last instruction. A runner that stopped
        // without clearing (for example, killed) leaves no live Start or Approve behind.
        public void Tick(float now)
        {
            if (Nonce != null && now >= m_NonceExpiry) { Nonce = null; Version++; }
            if (!Empty && now - m_LastShow >= StaleSeconds && !(Lines.Length == 1 && Lines[0] == NotConnected))
            {
                Lines = new[] { NotConnected };
                Cases = new (int, string)[0];
                Nonce = null;
                Version++;
            }
        }

        void Emit(string kind, int? caseId, string nonce, double at)
        {
            m_Events.Enqueue(new JObject { ["seq"] = ++m_Seq, ["kind"] = kind,
                ["case"] = caseId.HasValue ? new JValue(caseId.Value) : JValue.CreateNull(),
                ["nonce"] = nonce != null ? new JValue(nonce) : JValue.CreateNull(), ["at"] = at });
            while (m_Events.Count > RingSize) m_Events.Dequeue();
        }

        // Called only by the panel's native buttons, from physical clicks.
        public bool Start(int caseId, double at)
        {
            if (!Cases.Any(c => c.id == caseId)) return false;
            Emit("start", caseId, null, at);
            return true;
        }

        public bool Decide(bool approve, float now, double at)
        {
            if (!ButtonsLive(now)) return false;
            Emit(approve ? "approve" : "decline", null, Nonce, at);
            // One decision per nonce; the buttons go away until the runner shows another.
            Nonce = null;
            Version++;
            return true;
        }

        public JObject Events(long after) => new JObject
        {
            ["latest"] = m_Seq,
            ["events"] = new JArray(m_Events.Where(e => (long)e["seq"] > after)),
        };
    }
}
