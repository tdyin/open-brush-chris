// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;

namespace TiltBrush
{
    // Small native list UI. No backend, model, prefab, or web page is needed.
    [DefaultExecutionOrder(9000)]
    public class CHRISPanel : MonoBehaviour
    {
        public CHRISCommandGateway Gateway;
        private readonly List<KeyValuePair<string, JObject>> m_Options = new List<KeyValuePair<string, JObject>>();
        private TextMesh m_Text;
        private bool m_Open, m_Confirm, m_ToggleHeld, m_NextHeld, m_ConfirmHeld, m_BackHeld, m_StopHeld;
        private int m_Index;
        private long m_SelectedRevision, m_SelectedEpoch;
        private bool m_WasReady;
        private string m_SelectedSession;
        private string m_Notice, m_LastActionLabel, m_LastTool, m_ResultShown;
        private float m_NoticeUntil;
        private Key m_Next = Key.F6, m_Accept = Key.F7,
            m_Back = Key.F9, m_Stop = Key.Escape, m_Toggle = Key.F8;
        private string m_Rebind;
        private readonly string[] m_ButtonNames = { "primaryButton", "secondaryButton", "primary2DAxisClick" };
        private int m_XrNext, m_XrAccept, m_XrBack = 1, m_XrStop = 1;
        // Controller shortcuts stay off until the user checks shared native bindings in-headset.
        private bool m_ControllerShortcuts;

        void Start()
        {
            m_Next = LoadKey("Next", m_Next); m_Accept = LoadKey("Confirm", m_Accept);
            m_Back = LoadKey("Back", m_Back); m_Stop = LoadKey("Stop", m_Stop);
            m_Toggle = LoadKey("Toggle", m_Toggle);
            m_XrNext = LoadButton("Next", 0); m_XrAccept = LoadButton("Confirm", 0);
            m_XrBack = LoadButton("Back", 1); m_XrStop = LoadButton("Stop", 1);
            if (m_XrNext == m_XrStop) m_XrStop = (m_XrNext + 1) % 3;
            if (m_XrAccept == m_XrBack) m_XrBack = (m_XrAccept + 1) % 3;
            var obj = new GameObject("CHRIS direct palette");
            obj.transform.SetParent(transform, false);
            m_Text = obj.AddComponent<TextMesh>();
            m_Text.fontSize = 40; m_Text.characterSize = 0.025f;
            m_Text.anchor = TextAnchor.UpperLeft; m_Text.color = Color.white;
            obj.SetActive(false);
        }
        static Key LoadKey(string name, Key fallback)
        {
            int value = PlayerPrefs.GetInt("CHRIS.InputKey." + name, (int)fallback);
            return Enum.IsDefined(typeof(Key), value) && value != (int)Key.None ? (Key)value : fallback;
        }
        static int LoadButton(string name, int fallback) =>
            Mathf.Clamp(PlayerPrefs.GetInt("CHRIS.Xr" + name, fallback), 0, 2);
        static bool KeyDown(Key key) => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
        static bool Press(InputDevice device, string name)
        {
            // OpenXR buttons are populated by the same Input System action bindings
            // used by native Open Brush. Legacy XR feature polling can stay false.
            string control = name == "primary2DAxisClick" ? "thumbstickClicked" :
                name == "gripButton" ? "gripPressed" : name;
            return device != null && device.enabled &&
                device.TryGetChildControl<ButtonControl>(control)?.isPressed == true;
        }
        static bool Edge(bool value, ref bool previous)
        { bool edge = value && !previous; previous = value; return edge; }

        void Update()
        {
            if (Gateway == null || m_Text == null) return;
            var left = XRController.leftHand;
            var right = XRController.rightHand;
            bool toggle = Edge(m_ControllerShortcuts && Press(left, "gripButton") && Press(left, "primaryButton"), ref m_ToggleHeld);
            bool next = Edge(m_ControllerShortcuts && Press(left, m_ButtonNames[m_XrNext]), ref m_NextHeld);
            bool confirm = Edge(m_ControllerShortcuts && Press(right, m_ButtonNames[m_XrAccept]), ref m_ConfirmHeld);
            bool back = Edge(m_ControllerShortcuts && Press(right, m_ButtonNames[m_XrBack]), ref m_BackHeld);
            bool stop = Edge(m_ControllerShortcuts && Press(left, m_ButtonNames[m_XrStop]), ref m_StopHeld);
            if (KeyDown(m_Stop) || stop)
            { StopSelection(); Render(); return; }
            if (m_Rebind != null) { RebindKey(); return; }
            if (KeyDown(m_Toggle) || toggle)
            {
                m_Open = !m_Open; m_Confirm = false; m_Text.gameObject.SetActive(m_Open);
                Gateway.DirectPaletteOpen = m_Open;
                if (m_Open) { Gateway.Stop("Direct palette opened"); BuildOptions(); Position(); }
                return;
            }
            if (!m_Open) { Render(); return; }
            var c = Gateway.Capture();
            if ((bool)c["ready"] != m_WasReady) { BuildOptions(); m_Confirm = false; }
            if (m_Confirm && !SelectionCurrent(c)) { m_Confirm = false; Notice("State changed. Review the choice again."); }
            if (KeyDown(m_Back) || back) BackSelection();
            else if (KeyDown(m_Next) || next)
            { m_Confirm = false; m_Index = (m_Index + 1) % Math.Max(1, m_Options.Count); }
            else if (KeyDown(m_Accept) || confirm) ConfirmSelection();
            Render();
        }
        void Notice(string message)
        { m_Notice = message; m_NoticeUntil = Time.unscaledTime + 8; }
        void StopSelection()
        {
            Gateway.Stop(); m_Confirm = false; m_Rebind = null;
            var result = Gateway.LastDirectResult;
            if (result != null) m_ResultShown = (string)result["command_id"] + ":" + (string)result["status"];
            Notice("STOP received. Pending work cancelled.\nCompleted changes are kept.");
            if (!m_Open) Position();
        }
        void BackSelection()
        {
            if (m_Confirm) { m_Confirm = false; Notice("Confirmation cancelled. No change applied."); }
            else if (m_Options.Count > 0)
            { m_Index = (m_Index - 1 + m_Options.Count) % m_Options.Count; Notice("Previous option"); }
        }
        string ResultText(JObject result)
        {
            string status = (string)result["status"];
            if (status != "succeeded") return "Change " + status + ": " + (string)result["reason"];
            var observed = result["observed"];
            string detail = "";
            if (m_LastTool == "brush.size" && observed?["brush_size"] is JValue size &&
                (size.Type == JTokenType.Float || size.Type == JTokenType.Integer))
                detail = "\nObserved size: " + ((double)size).ToString("F2") + " (0-1).\nDraw a new stroke to compare.";
            else if (m_LastTool == "view.move" && observed?["scene_position"] is JArray position)
                detail = "\nObserved scene position: " + string.Join(", ", position.Select(v => ((double)v).ToString("F3")));
            else if (m_LastTool == "view.turn") detail = "\nScene turn verified. Compare against existing artwork.";
            return "APPLIED: " + m_LastActionLabel + detail;
        }
        void Render()
        {
            var result = Gateway.LastDirectResult;
            if (result != null && (string)result["status"] != "queued")
            {
                string key = (string)result["command_id"] + ":" + (string)result["status"];
                if (key != m_ResultShown) { m_ResultShown = key; Notice(ResultText(result)); }
            }
            bool notice = !string.IsNullOrEmpty(m_Notice) && Time.unscaledTime < m_NoticeUntil;
            m_Text.gameObject.SetActive(m_Open || notice);
            if (!m_Open) { m_Text.text = notice ? m_Notice : ""; return; }
            var c = Gateway.Capture();
            string selected = m_Options.Count == 0 ? "Waiting for host" : m_Options[m_Index].Key;
            m_Text.text = "CHRIS / Direct controls\n" + (m_Confirm ? "REVIEW: " : "> ") + selected +
                "\n" + (Available(c) ? m_Next + " Next / " + m_Accept + " Confirm / " + m_Back + " Back" : "Waiting for idle native host") +
                "\n" + (m_Confirm ? "Confirm again to apply; Back cancels." : "Back goes to the previous option.") +
                "\nStop: " + m_Stop + (m_ControllerShortcuts ? " / left " + m_ButtonNames[m_XrStop] : "") +
                "\n" + (notice ? m_Notice : Gateway.Status);
        }
        bool Available(JObject c) => (bool)c["ready"] && !(bool)c["stroke_active"] &&
            !CHRISCommandGateway.NativeInteractionBusy() && c["active_task"].Type == JTokenType.Null;
        bool SelectionCurrent(JObject c) => Available(c) && m_SelectedRevision == (long)c["revision"] &&
            m_SelectedEpoch == (long)c["authority_epoch"] && m_SelectedSession == (string)c["host_session"];
        void ConfirmSelection()
        {
            if (m_Rebind != null) return;
            var c = Gateway.Capture();
            if (!Available(c) || m_Options.Count == 0) { m_Confirm = false; Notice("No change: finish drawing or using native controls first."); return; }
            if (m_Confirm)
            {
                if (SelectionCurrent(c))
                {
                    m_LastActionLabel = m_Options[m_Index].Key;
                    m_LastTool = (string)m_Options[m_Index].Value["tool"];
                    try { Gateway.Direct((JObject)m_Options[m_Index].Value.DeepClone()); Notice("Applying: " + m_LastActionLabel); }
                    catch (ArgumentException ex) { Notice("Change rejected: " + ex.Message); }
                    catch (Exception) { Notice("Change failed. Check native status before trying again."); }
                }
                else Notice("State changed. Review the choice again.");
                m_Confirm = false;
            }
            else
            {
                m_Confirm = true; m_SelectedRevision = (long)c["revision"];
                m_SelectedEpoch = (long)c["authority_epoch"]; m_SelectedSession = (string)c["host_session"];
            }
        }
        void Position()
        {
            if (App.CurrentState != App.AppState.Standard) return;
            var camera = App.VrSdk == null ? null : App.VrSdk.GetVrCamera();
            if (camera == null) return;
            var head = camera.transform;
            m_Text.transform.position = head.position + head.forward * 3 + head.right * -1 + head.up * 0.6f;
            m_Text.transform.rotation = head.rotation;
        }
        void Add(string label, string tool, JToken number = null, string text = null,
            JArray vector = null, JToken visible = null)
        {
            var action = new JObject { ["action_id"] = Guid.NewGuid().ToString("N"), ["version"] = 1,
                ["tool"] = tool, ["number"] = number, ["text"] = text,
                ["vector"] = vector, ["visible"] = visible };
            m_Options.Add(new KeyValuePair<string, JObject>(label, action));
        }
        void BuildOptions()
        {
            m_Options.Clear(); m_Index = 0;
            var c = Gateway.Capture();
            m_WasReady = (bool)c["ready"];
            if (!m_WasReady) return;
            foreach (float size in new[] { 0.1f, 0.3f, 0.5f, 0.8f }) Add("Brush size " + size, "brush.size", size);
            foreach (string color in new[] { "#FFFFFF", "#FF4040", "#40A0FF", "#40FF80" })
                Add("Color " + color, "brush.color", text: color);
            foreach (var brush in ((JObject)c["brushes"]).Properties().OrderBy(p => (string)p.Value).Take(8))
                Add("Brush " + (string)brush.Value, "brush.select", text: brush.Name);
            foreach (var panel in ((JObject)c["panels"]).Properties())
            {
                Add("Open " + panel.Name, "panel.visibility", text: panel.Name, visible: true);
                Add("Close " + panel.Name, "panel.visibility", text: panel.Name, visible: false);
            }
            Add("Move +X (0.25 native units)", "view.move", vector: new JArray(0.25, 0, 0));
            Add("Move -X (0.25 native units)", "view.move", vector: new JArray(-0.25, 0, 0));
            Add("Move +Z (0.25 native units)", "view.move", vector: new JArray(0, 0, 0.25));
            Add("Move -Z (0.25 native units)", "view.move", vector: new JArray(0, 0, -0.25));
            Add("Turn left 15 degrees", "view.turn", -15);
            Add("Turn right 15 degrees", "view.turn", 15);
        }
        void KeyButton(string name, Key code)
        {
            if (GUILayout.Button(name + ": " + code)) { m_Rebind = name; m_Confirm = false; }
        }
        void RebindKey()
        {
            if (Keyboard.current == null) return;
            foreach (var control in Keyboard.current.allKeys)
            {
                if (!control.wasPressedThisFrame) continue;
                var key = control.keyCode;
                var keys = new[] { m_Next, m_Accept, m_Back, m_Stop, m_Toggle };
                if (keys.Contains(key)) return;
                switch (m_Rebind)
                {
                    case "Next": m_Next = key; break;
                    case "Confirm": m_Accept = key; break;
                    case "Back": m_Back = key; break;
                    case "Stop": m_Stop = key; break;
                    case "Toggle": m_Toggle = key; break;
                }
                PlayerPrefs.SetInt("CHRIS.InputKey." + m_Rebind, (int)key);
                PlayerPrefs.Save(); m_Rebind = null; return;
            }
        }
        void XrButton(string name, ref int value, int conflict)
        {
            if (!GUILayout.Button(name + ": " + m_ButtonNames[value])) return;
            do { value = (value + 1) % 3; } while (value == conflict);
            PlayerPrefs.SetInt("CHRIS.Xr" + name, value); PlayerPrefs.Save();
        }
        void OnGUI()
        {
            if (!m_Open || Gateway == null) return;
            GUILayout.BeginArea(new Rect(15, 15, 390, 700), GUI.skin.box);
            GUILayout.Label("CHRIS direct controls / bindings");
            if (GUILayout.Button("STOP")) { StopSelection(); Render(); }
            GUILayout.Label(Time.unscaledTime < m_NoticeUntil ? m_Notice : Gateway.Status);
            GUILayout.Label("Stop handler: " + Gateway.LastStopMilliseconds.ToString("F3") +
                " ms / authority invalidations: " + Gateway.StopCount);
            if (GUILayout.Button("Close palette"))
            { m_Open = false; m_Confirm = false; m_Rebind = null; Gateway.DirectPaletteOpen = false; m_Text.gameObject.SetActive(false); }
            GUILayout.Label(m_Options.Count == 0 ? "Waiting for host" : m_Options[m_Index].Key);
            if (GUILayout.Button("Next")) { m_Confirm = false; m_Index = (m_Index + 1) % Math.Max(1, m_Options.Count); }
            if (GUILayout.Button(m_Confirm ? "Confirm change" : "Review change")) ConfirmSelection();
            if (GUILayout.Button(m_Confirm ? "Cancel confirmation" : "Previous option")) BackSelection();
            KeyButton("Toggle", m_Toggle); KeyButton("Next", m_Next);
            KeyButton("Confirm", m_Accept); KeyButton("Back", m_Back); KeyButton("Stop", m_Stop);
            bool shortcuts = GUILayout.Toggle(m_ControllerShortcuts, "Enable shared controller shortcuts for this session");
            if (shortcuts != m_ControllerShortcuts) { m_ControllerShortcuts = shortcuts; m_Confirm = false; Gateway.Stop("Controller bindings changed"); }
            GUILayout.Label("Controller buttons also retain Open Brush actions. Check bindings before enabling.");
            GUILayout.Label("Controller: Next/Stop left; Confirm/Back right");
            GUILayout.Label("Quest: primary = X/A, secondary = Y/B, axis click = press thumbstick.");
            if (m_ControllerShortcuts)
                GUILayout.Label("Input devices: left " + (XRController.leftHand != null ? "connected" : "not detected") +
                    ", right " + (XRController.rightHand != null ? "connected" : "not detected"));
            XrButton("Next", ref m_XrNext, m_XrStop); XrButton("Stop", ref m_XrStop, m_XrNext);
            XrButton("Confirm", ref m_XrAccept, m_XrBack); XrButton("Back", ref m_XrBack, m_XrAccept);
            GUILayout.Label("Toggle: left grip + primary button");
            if (m_Rebind != null) GUILayout.Label("Press an unused key for " + m_Rebind);
            GUILayout.EndArea();
        }
        void OnDisable()
        {
            m_Open = false; m_Confirm = false;
            if (Gateway != null) { Gateway.DirectPaletteOpen = false; Gateway.Stop("Direct palette disabled"); }
            if (m_Text != null) m_Text.gameObject.SetActive(false);
        }
        void OnDestroy() { if (m_Text != null) Destroy(m_Text.gameObject); }
    }
}
