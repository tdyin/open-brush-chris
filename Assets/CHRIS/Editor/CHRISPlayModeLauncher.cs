// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using UnityEditor;
using UnityEditor.SceneManagement;

namespace TiltBrush
{
    // Headset sessions: opens the main scene and enters Play mode, from the menu or
    // `-executeMethod TiltBrush.CHRISPlayModeLauncher.OpenMainAndPlay`. Changes no assets.
    public static class CHRISPlayModeLauncher
    {
        const string MainScene = "Assets/Scenes/Main.unity";

        [MenuItem("CHRIS/Open Main scene and Play")]
        public static void OpenMainAndPlay()
        {
            if (EditorApplication.isPlaying) return;
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            EditorApplication.delayCall += EditorApplication.EnterPlaymode;
        }
    }
}
