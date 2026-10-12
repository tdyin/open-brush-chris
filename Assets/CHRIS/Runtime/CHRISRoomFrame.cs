// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace TiltBrush
{
    // input-v0.2 room frame: Unity global space in metres. Its epoch advances whenever the XR
    // tracking origin changes (a recenter), because poses from before then no longer mean the
    // same place. A recenter during an input lease revokes it.
    public static class CHRISRoomFrame
    {
        public const string Id = "room";
        static long s_Epoch;
        static readonly List<XRInputSubsystem> s_Hooked = new List<XRInputSubsystem>();
        static readonly List<XRInputSubsystem> s_Found = new List<XRInputSubsystem>();

        public static long Epoch => s_Epoch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay()
        {
            foreach (var subsystem in s_Hooked) if (subsystem != null) subsystem.trackingOriginUpdated -= OnOriginUpdated;
            s_Hooked.Clear();
            s_Epoch = 0;
        }

        // Called once per frame; hooks subsystems that started since the last call.
        internal static void Poll()
        {
            SubsystemManager.GetSubsystems(s_Found);
            foreach (var subsystem in s_Found)
            {
                if (s_Hooked.Contains(subsystem)) continue;
                subsystem.trackingOriginUpdated += OnOriginUpdated;
                s_Hooked.Add(subsystem);
            }
        }

        static void OnOriginUpdated(XRInputSubsystem subsystem) => OriginChanged();

        // Also the test entry point for a recenter.
        internal static void OriginChanged()
        {
            s_Epoch++;
            Debug.Log($"CHRIS room frame: tracking origin changed; frame epoch {s_Epoch}");
        }
    }
}
