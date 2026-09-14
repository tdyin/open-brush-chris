// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using UnityEngine;

namespace TiltBrush
{
    // Retains independent local Stop access; the CHRIS launcher is a native Lab icon.
    public class CHRISMenuEntry : MonoBehaviour
    {
        void Start()
        {
            var resources = CHRISUIResources.Load();
            resources.Button(transform, "CHRIS local Stop", new Vector3(0, -0.73f, -0.06f), new Vector2(1.2f, 0.26f), "STOP",
                () => CHRISPanel.Instance?.StopLocal(), true);
        }
    }
}
