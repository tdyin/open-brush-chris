// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using UnityEngine;

namespace TiltBrush
{
    // Added to the existing More menu prefab, used by both basic and advanced Admin panels.
    public class CHRISMenuEntry : MonoBehaviour
    {
        void Start()
        {
            var resources = CHRISUIResources.Load();
            resources.Button(transform, "CHRIS entry", new Vector3(-0.22f, -0.73f, -0.06f),
                new Vector2(0.88f, 0.26f), "CHRIS", () =>
                {
                    var popup = GetComponent<PopUpWindow>();
                    popup.GetParentPanel().CreatePopUp(resources.PopupPrefab, Vector3.zero, false, true);
                });
            resources.Button(transform, "CHRIS local Stop", new Vector3(0.47f, -0.73f, -0.06f),
                new Vector2(0.42f, 0.26f), "STOP", () => CHRISPanel.Instance?.StopLocal(), true);
        }
    }
}
