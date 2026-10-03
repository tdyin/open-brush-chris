// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TiltBrush
{
    // Read-only view of the native Brush palette for controller procedures. Nothing here sets a
    // brush, presses a button or changes a panel or page; it only reads existing state.
    // Positions are Open Brush room space (Unity global space, which the controllers share).
    public static class CHRISPaletteObserver
    {
        public const string TargetPrefix = "brush:";
        public const int MaxTargets = 64;
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        // BrushGrid keeps its page state private; read it without changing upstream code.
        static readonly FieldInfo s_PageIndex = typeof(BrushGrid).GetField("m_PageIndex", Private);
        static readonly FieldInfo s_RequestedPage = typeof(BrushGrid).GetField("m_RequestedPageIndex", Private);
        static readonly FieldInfo s_PageCount = typeof(BrushGrid).GetField("m_NumPages", Private);

        public static string TargetId(BrushDescriptor brush) => brush == null ? null : TargetPrefix + brush.m_Guid.ToString();

        static BasePanel BrushPanel()
        {
            var panel = CHRISCommandGateway.FindPanel("Brush");
            return panel != null && CHRISCommandGateway.PanelVisible(panel) ? panel : null;
        }

        static BrushTypeButton[] Buttons(BasePanel panel) => panel == null ? new BrushTypeButton[0] :
            panel.GetComponentsInChildren<BrushTypeButton>(false).Where(b => b.m_Brush != null).Take(MaxTargets).ToArray();

        // True while the grid is not between pages; buttons mid-flip are not valid targets.
        static bool PageStable(BrushGrid grid, out int page, out int count)
        {
            page = 0; count = 1;
            if (grid == null || s_PageIndex == null || s_RequestedPage == null || s_PageCount == null) return false;
            float index = (float)s_PageIndex.GetValue(grid), requested = (float)s_RequestedPage.GetValue(grid);
            count = Mathf.Max(1, (int)s_PageCount.GetValue(grid));
            page = Mathf.Clamp(Mathf.RoundToInt(index), 0, count - 1);
            return Mathf.Approximately(index, requested) && Mathf.Approximately(index, Mathf.Round(index));
        }

        static Vector3 Center(BrushTypeButton button)
        {
            var collider = button.GetComponent<Collider>();
            return collider != null && collider.enabled ? collider.bounds.center : button.transform.position;
        }

        static bool Interactable(BrushTypeButton button, bool stable) =>
            stable && button.gameObject.activeInHierarchy && button.IsAvailable();

        // The context's palette object, or null when the Brush panel is not shown.
        public static JObject Snapshot()
        {
            var panel = BrushPanel();
            if (panel == null) return null;
            bool stable = PageStable(panel.GetComponentInChildren<BrushGrid>(), out int page, out int count);
            return BuildPalette(Buttons(panel).Select(b => ((Guid)b.m_Brush.m_Guid, b.m_Brush.m_DurableName,
                Center(b), Interactable(b, stable))), page, count);
        }

        // Every /chris/context read carries this object, and the service rejects the whole
        // context if one value is malformed, so the invariants are enforced here: unique
        // lowercase "brush:<guid>" targets, at most 64, labels of at most 80 characters,
        // finite centres or null, and 0 <= page < page_count.
        internal static JObject BuildPalette(IEnumerable<(Guid brush, string label, Vector3 center, bool interactable)> buttons,
            int page, int count)
        {
            count = Mathf.Max(1, count);
            var seen = new HashSet<Guid>();
            var targets = new JArray();
            foreach (var (brush, name, c, interactable) in buttons)
            {
                if (targets.Count >= MaxTargets) break;
                if (!seen.Add(brush)) continue;
                string label = name ?? "";
                bool finite = !float.IsNaN(c.x + c.y + c.z) && !float.IsInfinity(c.x + c.y + c.z);
                targets.Add(new JObject
                {
                    ["target_id"] = TargetPrefix + brush.ToString("D"),
                    ["brush_id"] = brush.ToString("D"),
                    ["label"] = label.Length > 80 ? label.Substring(0, 80) : label,
                    ["center_room"] = finite ? new JArray(c.x, c.y, c.z) : null,
                    ["interactable"] = interactable,
                });
            }
            return new JObject { ["visible"] = true, ["page"] = Mathf.Clamp(page, 0, count - 1),
                ["page_count"] = count, ["targets"] = targets };
        }

        public static string HoverTargetId()
        {
            var hovered = Buttons(BrushPanel()).FirstOrDefault(b => b.IsHover() || b.IsPressed());
            return hovered == null ? null : TargetId(hovered.m_Brush);
        }

        public static bool TryResolve(string targetId, out Vector3 center, out Vector3 forward, out bool interactable)
        {
            center = forward = Vector3.zero;
            interactable = false;
            var panel = BrushPanel();
            var button = Buttons(panel).FirstOrDefault(b => TargetId(b.m_Brush) == targetId);
            if (button == null) return false;
            bool stable = PageStable(panel.GetComponentInChildren<BrushGrid>(), out _, out _);
            center = Center(button);
            forward = panel.transform.forward;
            interactable = Interactable(button, stable);
            return true;
        }
    }
}
