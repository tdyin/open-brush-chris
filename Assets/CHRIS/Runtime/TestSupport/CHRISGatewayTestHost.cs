#if UNITY_EDITOR
// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TiltBrush
{
    // Exercises the real gateway ledger/scheduler with deterministic in-memory native adapters.
    public sealed class CHRISGatewayTestHost : CHRISCommandGateway
    {
        public JObject State = new JObject {
            ["ready"] = true, ["stroke_active"] = false,
            ["brush_id"] = "ink", ["brush_size"] = 0.5, ["brush_color"] = "#FFFFFF",
            ["brushes"] = new JObject { ["ink"] = "Ink" },
            ["panels"] = new JObject { ["Brush"] = false, ["Color"] = false },
            ["scene_position"] = new JArray(0.0, 0.0, 0.0),
            ["scene_rotation"] = new JArray(0.0, 0.0, 0.0, 1.0), ["scene_scale"] = 1.0 };
        public int Applied, ThrowOnAction;
        public bool Busy, SkipMutation, DelayPanel;
        public double Clock = Now;
        protected override double AuthorityTime => Clock;
        protected override bool IsNativeInteractionBusy() => Busy;

        public void Initialize()
        {
            typeof(CHRISCommandGateway).GetField("m_Registered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(this, true);
            Capture();
        }

        public void Tick() => typeof(CHRISCommandGateway).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(this, null);
        public bool Readback(JObject action, JObject before, JObject after) => base.Verify(action, before, after);
        protected override JObject ReadContext()
        {
            var context = base.ReadContext();
            foreach (var property in State.Properties()) context[property.Name] = property.Value.DeepClone();
            return context;
        }

        protected override void Apply(JObject action)
        {
            Applied++;
            if (Applied == ThrowOnAction) throw new InvalidOperationException("Simulated adapter failure");
            if (SkipMutation) return;
            switch ((string)action["tool"])
            {
                case "brush.size": State["brush_size"] = action["number"]; break;
                case "brush.color": State["brush_color"] = action["text"]; break;
                case "brush.select": State["brush_id"] = action["text"]; break;
                case "panel.visibility":
                    if (!DelayPanel) State["panels"][(string)action["text"]] = action["visible"];
                    break;
                case "view.move":
                    for (int i = 0; i < 3; i++) State["scene_position"][i] = (double)State["scene_position"][i] - (double)action["vector"][i];
                    break;
                case "view.turn":
                    var q = State["scene_rotation"];
                    var rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]) * Quaternion.AngleAxis(-(float)action["number"], Vector3.up);
                    State["scene_rotation"] = new JArray(rotation.x, rotation.y, rotation.z, rotation.w);
                    break;
            }
        }

        protected override bool Verify(JObject action, JObject before, JObject after) =>
            (string)action["tool"] == "panel.visibility" ?
                (bool)after["panels"][(string)action["text"]] == (bool)action["visible"] : base.Verify(action, before, after);
    }
}

#endif
