// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TiltBrush
{
    public sealed class CHRISCloudVoiceBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) => VerifyNoLegacyResources();
        public static void VerifyNoLegacyResources()
        {
            if (Directory.Exists(Path.Combine(Application.dataPath, "Plugins/CHRISVoice")) ||
                Directory.Exists(Path.Combine(Application.streamingAssetsPath, "CHRISVoice")))
                throw new BuildFailedException("Retired Vosk resources remain. Remove the generated Assets/Plugins/CHRISVoice and Assets/StreamingAssets/CHRISVoice directories before building cloud voice.");
        }
    }
}
