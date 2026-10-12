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
        public const int MaxLines = 4, MaxLineLength = 80, MaxCases = 8, MaxCaseId = 15, MaxTitleLength = 60, MaxTtl = 60, RingSize = 32;
        // The agent's latest reasoning, wrapped in its own panel area (D89). Optional: absent and
        // null are the same.
        public const int MaxReasoningLength = 400;
        // The runner refreshes its idle display every 30 s; after this long it is presumed gone.
        public const float StaleSeconds = 70;
        public const string NotConnected = "CHRIS tests not connected";
        // Shown when an empty display (the runner's exit clear) replaces a live one (D105).
        public const string SessionEnded = "CHRIS test session ended";
        static readonly Regex s_Id = new Regex("\\A[A-Za-z0-9_-]{1,80}\\z");

        public static CHRISTestDisplay Instance { get; private set; } = new CHRISTestDisplay();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay() => Instance = new CHRISTestDisplay();

        readonly Queue<JObject> m_Events = new Queue<JObject>();
        float m_NonceExpiry, m_LastShow;
        long m_Seq;

        public string[] Lines { get; private set; } = new string[0];
        public string Reasoning { get; private set; }
        public string Nonce { get; private set; }
        public (int id, string title)[] Cases { get; private set; } = new (int, string)[0];
        public long Latest => m_Seq;
        // Bumped on every change the panel must redraw.
        public int Version { get; private set; }
        // Set when a test session starts showing; the host opens the panel and clears it.
        public bool OpenPending { get; private set; }
        public void PanelOpened() => OpenPending = false;

        public bool ButtonsLive(float now) => Nonce != null && now < m_NonceExpiry;
        bool Empty => Lines.Length == 0 && Cases.Length == 0;
        // A native status line in place of a runner display; a new session starts fresh after it.
        bool Placeholder => Cases.Length == 0 && Lines.Length == 1 && (Lines[0] == NotConnected || Lines[0] == SessionEnded);

        static bool IsNull(JToken token) => token.Type == JTokenType.Null || (token is JValue v && v.Value == null);

        public static string Validate(JObject d)
        {
            var fields = new[] { "lines", "nonce", "buttons", "cases", "ttl_s" };
            if (d == null || !d.Properties().All(p => fields.Contains(p.Name) || p.Name == "reasoning") || !fields.All(f => d[f] != null))
                return "Invalid display fields";
            var reasoning = d["reasoning"];
            if (reasoning != null && !IsNull(reasoning) && (reasoning.Type != JTokenType.String ||
                ((string)reasoning).Length > MaxReasoningLength || ((string)reasoning).Any(char.IsControl)))
                return "Invalid reasoning";
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
                    (long)o["id"] < 1 || (long)o["id"] > MaxCaseId || ((string)o["title"]).Length > MaxTitleLength || !ids.Add((long)o["id"]))
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
            // "Session ended" reopens like a blank display did before it existed; "not connected"
            // never reopens a panel the user may have closed while the runner was gone.
            bool wasBlank = Empty, wasPlaceholder = Placeholder;
            bool wasEmpty = wasBlank || wasPlaceholder && Lines[0] == SessionEnded;
            var previous = Lines;
            m_LastShow = now;
            Lines = ((JArray)d["lines"]).Select(l => (string)l).ToArray();
            Reasoning = d["reasoning"] == null || IsNull(d["reasoning"]) ? null : (string)d["reasoning"];
            Nonce = IsNull(d["nonce"]) ? null : (string)d["nonce"];
            m_NonceExpiry = now + (long)d["ttl_s"];
            Cases = ((JArray)d["cases"]).Select(c => ((int)(long)c["id"], (string)c["title"])).ToArray();
            // The runner clears the display only when its session exits; say so rather than blank.
            // A clear over a native status line keeps that line.
            if (Empty && !wasBlank) Lines = wasPlaceholder ? previous : new[] { SessionEnded };
            Version++;
            opened = wasEmpty && !Empty && !Placeholder;
            if (opened) OpenPending = true;
            if (Empty || Placeholder) OpenPending = false;
            return m_Seq;
        }

        // Expired buttons disappear; the lines stay as the last instruction. A runner that stopped
        // without clearing (for example, killed) leaves no live Start or Approve behind.
        public void Tick(float now)
        {
            if (Nonce != null && now >= m_NonceExpiry) { Nonce = null; Version++; }
            if (!Empty && !Placeholder && now - m_LastShow >= StaleSeconds)
            {
                Lines = new[] { NotConnected };
                Reasoning = null;
                Cases = new (int, string)[0];
                Nonce = null;
                OpenPending = false;
                Version++;
            }
        }

        void Emit(string kind, int? caseId, string nonce, double at, string text = null, string id = null)
        {
            var e = new JObject { ["seq"] = ++m_Seq, ["kind"] = kind,
                ["case"] = caseId.HasValue ? new JValue(caseId.Value) : JValue.CreateNull(),
                ["nonce"] = nonce != null ? new JValue(nonce) : JValue.CreateNull(), ["at"] = at };
            if (kind == UtteranceKind) { e["text"] = text; e["id"] = id; }
            m_Events.Enqueue(e);
            while (m_Events.Count > RingSize) m_Events.Dequeue();
        }

        // D129: what the user said with the panel's mic button, for the test session. Only a
        // non-empty final transcript is sent; cancels, errors and silence send nothing.
        public const string UtteranceKind = "utterance";
        public const int MaxUtteranceLength = 400;

        public bool Utterance(string text, string id, double at)
        {
            text = CleanUtterance(text);
            if (text == null || id == null || !s_Id.IsMatch(id)) return false;
            Emit(UtteranceKind, null, null, at, text, id);
            return true;
        }

        // Control characters (newlines and tabs included) become spaces; runs of spaces collapse;
        // at most 400 characters. Null when nothing is left.
        public static string CleanUtterance(string raw)
        {
            if (raw == null) return null;
            var chars = raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
            string text = Regex.Replace(new string(chars), " {2,}", " ").Trim();
            if (text.Length > MaxUtteranceLength) text = text.Substring(0, MaxUtteranceLength).TrimEnd();
            return text.Length == 0 ? null : text;
        }

        // Event shape only, as the shared test_events cases describe.
        internal static string ValidateEvent(JObject e)
        {
            if (e == null || e["seq"]?.Type != JTokenType.Integer || (long)e["seq"] < 1 || e["kind"]?.Type != JTokenType.String ||
                !(e["at"]?.Type == JTokenType.Float || e["at"]?.Type == JTokenType.Integer)) return "Invalid event";
            string kind = (string)e["kind"];
            var keys = new HashSet<string> { "seq", "kind", "case", "nonce", "at" };
            if (kind == UtteranceKind) { keys.Add("text"); keys.Add("id"); }
            else if (kind != "start" && kind != "approve" && kind != "decline") return "Invalid event kind";
            if (e.Properties().Count() != keys.Count || !e.Properties().All(p => keys.Contains(p.Name))) return "Invalid event fields";
            if (kind == "start" && (e["case"].Type != JTokenType.Integer || (long)e["case"] < 1 || (long)e["case"] > MaxCaseId ||
                !IsNull(e["nonce"]))) return "Start needs a case and no nonce";
            if ((kind == "approve" || kind == "decline") && (!IsNull(e["case"]) || e["nonce"].Type != JTokenType.String ||
                !s_Id.IsMatch((string)e["nonce"]))) return "Approve and decline need a nonce and no case";
            if (kind == UtteranceKind)
            {
                if (!IsNull(e["case"]) || !IsNull(e["nonce"])) return "Utterance carries case or nonce";
                if (e["text"].Type != JTokenType.String || CleanUtterance((string)e["text"]) != (string)e["text"]) return "Invalid utterance text";
                if (e["id"].Type != JTokenType.String || !s_Id.IsMatch((string)e["id"])) return "Invalid utterance id";
            }
            return null;
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
