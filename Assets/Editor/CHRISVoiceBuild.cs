// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TiltBrush
{
    public sealed class CHRISVoiceBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.StandaloneWindows64) VerifyResources();
        }

        public static void VerifyResources()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string inventoryPath = Path.Combine(Application.streamingAssetsPath, "CHRISVoice/acquisition.json");
            if (!File.Exists(inventoryPath))
                throw new BuildFailedException("Prepare offline CHRIS speech with Support/Python/acquire-chris-voice.py before building Windows.");
            var inventory = JObject.Parse(File.ReadAllText(inventoryPath));
            var pins = JObject.Parse(File.ReadAllText(Path.Combine(root, "Support/CHRISVoice/resources.json")));
            if (!JToken.DeepEquals(inventory["archives"], pins))
                throw new BuildFailedException("CHRIS speech resources do not match the pinned versions. Re-run acquisition.");
            var files = (JObject)inventory["files"];
            if (files == null || files.Count < 20) throw new BuildFailedException("Incomplete CHRIS speech resource inventory.");
            foreach (var entry in files.Properties())
            {
                string path = Path.GetFullPath(Path.Combine(root, entry.Name));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || Hash(path) != (string)entry.Value)
                    throw new BuildFailedException("CHRIS speech resource missing or changed: " + entry.Name);
            }
            foreach (string name in new[] { "libvosk.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll", "libwinpthread-1.dll" })
            {
                var importer = AssetImporter.GetAtPath("Assets/Plugins/CHRISVoice/x86_64/" + name) as PluginImporter;
                if (importer == null || importer.GetCompatibleWithAnyPlatform() ||
                    !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64) || !importer.GetCompatibleWithEditor())
                    throw new BuildFailedException("Incorrect Windows speech plugin import settings: " + name);
            }
        }

        static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2")));
        }
    }
}
