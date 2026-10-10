// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TiltBrush
{
    // Headset sessions: opens the main scene and enters Play mode, from the menu or
    // `-executeMethod TiltBrush.CHRISPlayModeLauncher.OpenMainAndPlay`. Changes no assets.
    // Entering Play is retried until it happens (an early request during editor start-up can
    // be dropped) and is never toggled off. On every Play start, however started, the Game
    // view is focused and the editor brought to the front, so keyboard Stop (Escape) reaches
    // Open Brush without a click.
    [InitializeOnLoad]
    public static class CHRISPlayModeLauncher
    {
        const string MainScene = "Assets/Scenes/Main.unity";
        const string PendingUntil = "CHRIS.LaunchPlayUntil";
        const double RetrySeconds = 2, GiveUpSeconds = 180;
        static double s_NextAttempt;
        static int s_Attempts;

        static CHRISPlayModeLauncher()
        {
            EditorApplication.playModeStateChanged += FocusWhenPlaying;
            if (SessionState.GetFloat(PendingUntil, 0) > 0) EditorApplication.update += RetryPlay;
        }

        [MenuItem("CHRIS/Open Main scene and Play")]
        public static void OpenMainAndPlay()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            SessionState.SetFloat(PendingUntil, (float)(EditorApplication.timeSinceStartup + GiveUpSeconds));
            s_Attempts = 0;
            s_NextAttempt = 0;
            EditorApplication.update -= RetryPlay;
            EditorApplication.update += RetryPlay;
        }

        static void RetryPlay()
        {
            float until = SessionState.GetFloat(PendingUntil, 0);
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            if (playing || until <= 0 || EditorApplication.timeSinceStartup > until)
            {
                if (!playing && until > 0) Debug.LogWarning($"CHRIS Play launcher: Play mode did not start after {s_Attempts} attempts");
                SessionState.EraseFloat(PendingUntil);
                EditorApplication.update -= RetryPlay;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < s_NextAttempt) return;
            s_NextAttempt = EditorApplication.timeSinceStartup + RetrySeconds;
            Debug.Log($"CHRIS Play launcher: entering Play mode (attempt {++s_Attempts})");
            EditorApplication.EnterPlaymode();
        }

        static void FocusWhenPlaying(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            // Keyboard input reaches Play mode only through a focused Game view.
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            Debug.Log("CHRIS Play launcher: Game view focused; editor foreground " + BringEditorToFront());
        }

#if UNITY_EDITOR_WIN
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        const byte AltKey = 0x12;
        const uint KeyUp = 0x0002;

        // Windows lets a background process take the foreground after it sends input; a single
        // Alt tap is the usual way to qualify. Skipped when the editor is already in front, so a
        // manual Play click never sends it. No system setting is changed.
        static bool BringEditorToFront()
        {
            var window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (window == IntPtr.Zero) return false;
            if (GetForegroundWindow() == window) return true;
            keybd_event(AltKey, 0, 0, UIntPtr.Zero);
            keybd_event(AltKey, 0, KeyUp, UIntPtr.Zero);
            SetForegroundWindow(window);
            return GetForegroundWindow() == window;
        }
#else
        static bool BringEditorToFront() => false;
#endif
    }
}
