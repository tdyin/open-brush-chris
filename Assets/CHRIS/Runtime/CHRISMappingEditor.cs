// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiltBrush
{
    // Keeps a headset edit local until the complete document passes the same native validator.
    // CHRISMappingAuthority still owns approval, neutral activation and persistence.
    internal sealed class CHRISMappingEditor
    {
        readonly string m_BeforeJson;
        string m_WorkingJson;
        CHRISInputMapping m_Working;

        internal CHRISMappingEditor(string beforeJson, string workingJson)
        {
            m_BeforeJson = beforeJson;
            m_Working = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(workingJson));
            if (m_Working.SchemaVersion != CHRISInputMapping.BimanualVersion)
                throw new CHRISMappingException("schema", "headset edits require v0.1.2");
            m_WorkingJson = workingJson;
        }

        internal string WorkingJson => m_WorkingJson;
        internal IReadOnlyList<CHRISInputMappingEntry> Entries => m_Working.Mappings;

        internal bool TryChangeSource(int index, JObject source, out string error)
        {
            error = null;
            if (index < 0 || index >= Entries.Count) { error = "No binding selected."; return false; }
            var document = JObject.Parse(m_WorkingJson);
            ((JObject)((JArray)document["mappings"])[index])["source"] = source;
            string proposed = document.ToString(Formatting.Indented) + "\n";
            try
            {
                var validated = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(proposed));
                m_Working = validated;
                m_WorkingJson = proposed;
                return true;
            }
            catch (CHRISMappingException problem)
            {
                error = problem.Code + ": " + problem.Detail;
                return false;
            }
        }

        internal static string[] SourceTypes(CHRISMappedAction action)
        {
            switch (action)
            {
                case CHRISMappedAction.ModeUI: return new[] { "key" }; // F1 is fixed recovery.
                case CHRISMappedAction.BrushSize: return new[] { "mouse_wheel" };
                case CHRISMappedAction.ModePosition:
                case CHRISMappedAction.ModeRotation:
                case CHRISMappedAction.SelectHand:
                case CHRISMappedAction.Recenter:
                case CHRISMappedAction.HandBack: return new[] { "key" };
                case CHRISMappedAction.PoseDepth:
                case CHRISMappedAction.RotateRoll: return new[] { "key_axis1", "mouse_wheel" };
                case CHRISMappedAction.PoseXY:
                case CHRISMappedAction.RotateXY:
                case CHRISMappedAction.MoveView: return new[] { "key_vector2", "mouse_delta" };
                case CHRISMappedAction.StickAxis: return new[] { "key_vector2" };
                default: return new[] { "key", "mouse_button" };
            }
        }

        internal static string SourceType(CHRISMappingSource source)
        {
            switch (source)
            {
                case CHRISMappingSource.Key: return "key";
                case CHRISMappingSource.MouseButton: return "mouse_button";
                case CHRISMappingSource.KeyVector2: return "key_vector2";
                case CHRISMappingSource.MouseDelta: return "mouse_delta";
                case CHRISMappingSource.MouseWheel: return "mouse_wheel";
                default: return "key_axis1";
            }
        }

        internal static string Describe(CHRISInputMappingEntry entry)
        {
            string action = Regex.Replace(entry.Action.ToString(), "([a-z])([A-Z])", "$1 $2");
            string target = entry.Target == null ? "" : " (" + entry.Target + ")";
            string source;
            switch (entry.Source)
            {
                case CHRISMappingSource.Key: source = "Key " + entry.Key; break;
                case CHRISMappingSource.MouseButton: source = "Mouse " + entry.Button; break;
                case CHRISMappingSource.MouseDelta: source = "Mouse movement"; break;
                case CHRISMappingSource.MouseWheel: source = "Mouse wheel"; break;
                case CHRISMappingSource.KeyAxis1:
                    source = "Negative " + entry.Negative + ", positive " + entry.Positive; break;
                default:
                    source = "Up " + entry.Up + ", down " + entry.Down +
                        ", left " + entry.Left + ", right " + entry.Right; break;
            }
            return action + target + ": " + source;
        }

        internal string ReviewText() => ReviewText(m_BeforeJson, m_WorkingJson);

        internal static string ReviewText(string beforeJson, string proposedJson)
        {
            var before = beforeJson == null ? null : CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(beforeJson));
            var proposed = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(proposedJson));
            var lines = new List<string> { "Binding changes" };
            if (before == null) lines.Add("Before: no active mapping.");
            else if (before.SchemaVersion != proposed.SchemaVersion)
                lines.Add("Profile: " + before.SchemaVersion + " to " + proposed.SchemaVersion);
            if (proposed.SchemaVersion == CHRISInputMapping.BimanualVersion &&
                before?.SchemaVersion != CHRISInputMapping.BimanualVersion)
                lines.Add("Frame: fixed in room space; recenter only on command.");
            int unchanged = 0;
            if (before != null)
                foreach (var old in before.Mappings)
                    if (proposed.Find(old.Action, old.Target) == null)
                        lines.Add("Removed: " + Describe(old));
            foreach (var entry in proposed.Mappings)
            {
                var old = before?.Find(entry.Action, entry.Target);
                string current = Describe(entry);
                if (old == null) lines.Add("Added: " + current);
                else if (Describe(old) != current)
                    lines.Add("Before: " + Describe(old) + "\nAfter: " + current);
                else unchanged++;
            }
            if (unchanged > 0) lines.Add(unchanged + " unchanged bindings.");
            if (lines.Count == 1) lines.Add("No binding changed.");
            return string.Join("\n", lines);
        }
    }
}
