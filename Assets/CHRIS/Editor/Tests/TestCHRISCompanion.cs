// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace TiltBrush
{
    public class TestCHRISCompanion
    {
        internal static readonly JObject Measurements = new JObject {
            ["scope"] = "Edit-mode microbenchmarks and deterministic worker checks; no microphone, provider, player or headset latency measurement."
        };

        [Test]
        public void ClosingPopupPreservesNativeTooltipMaterials()
        {
            var resources = CHRISUIResources.Load();
            var tooltipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/ButtonDescription_Pill_OneLine_Center.prefab");
            // Use nonpersistent copies so a failing cleanup cannot destroy project assets.
            var sharedMaterials = tooltipPrefab.GetComponentsInChildren<MeshRenderer>(true)
                .Where(renderer => renderer.GetComponent<TMPro.TextMeshPro>() == null)
                .Select(renderer => renderer.sharedMaterial).Distinct()
                .ToDictionary(material => material, material => new Material(material));
            GameObject popupObject = null;
            var nativeTooltip = UnityEngine.Object.Instantiate(tooltipPrefab);
            try
            {
                UseSharedTooltipMaterials(nativeTooltip, sharedMaterials);
                for (int iteration = 0; iteration < 3; iteration++)
                {
                    popupObject = UnityEngine.Object.Instantiate(resources.PopupPrefab);
                    popupObject.GetComponent<CHRISNativePopup>().BuildView();
                    var generatedMaterials = popupObject.GetComponentsInChildren<MeshRenderer>(true)
                        .Where(renderer => renderer.GetComponent<TMPro.TextMeshPro>() == null)
                        .Select(renderer => renderer.sharedMaterial).Distinct().ToArray();
                    // UIComponent.Awake creates this same native description under each live button.
                    var button = popupObject.GetComponentInChildren<CHRISNativeButton>();
                    var buttonTooltip = UnityEngine.Object.Instantiate(tooltipPrefab, button.transform);
                    UseSharedTooltipMaterials(buttonTooltip, sharedMaterials);
                    // The popup's play-mode lifecycle does not run automatically in editor previews.
                    TestCHRISAssistance.Call(popupObject.GetComponent<CHRISNativePopup>(), "OnDestroy");
                    UnityEngine.Object.DestroyImmediate(popupObject);
                    popupObject = null;

                    Assert.That(sharedMaterials.Values.All(material => material != null), Is.True,
                        "Closing CHRIS must preserve materials shared with ordinary native tooltips.");
                    Assert.That(nativeTooltip.GetComponentsInChildren<MeshRenderer>(true)
                        .All(renderer => renderer.sharedMaterial != null), Is.True);
                    Assert.That(generatedMaterials.All(material => material == null), Is.True,
                        "Closing CHRIS must release its generated surface and icon materials: " +
                        string.Join(", ", generatedMaterials.Where(material => material != null).Select(material => material.name)));
                }
            }
            finally
            {
                if (popupObject != null) UnityEngine.Object.DestroyImmediate(popupObject);
                UnityEngine.Object.DestroyImmediate(nativeTooltip);
                foreach (var material in sharedMaterials.Values)
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        static void UseSharedTooltipMaterials(GameObject tooltip, Dictionary<Material, Material> materials)
        {
            foreach (var renderer in tooltip.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.GetComponent<TMPro.TextMeshPro>() == null)
                    renderer.sharedMaterial = materials[renderer.sharedMaterial];
        }

        [Test]
        public void CancellationCallbackCannotBlockTheCallingThread()
        {
            var type = typeof(CHRISVoiceInput).GetNestedType("RecordingRun", BindingFlags.NonPublic);
            var run = Activator.CreateInstance(type, true);
            var cancellation = (CancellationTokenSource)type.GetField("Cancellation").GetValue(run);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int caller = Thread.CurrentThread.ManagedThreadId, callback = caller;
                using (cancellation.Token.Register(() => {
                    callback = Thread.CurrentThread.ManagedThreadId;
                    entered.Set();
                    release.Wait(5000);
                }))
                {
                    Task work = null;
                    try
                    {
                        var timer = Stopwatch.StartNew();
                        type.GetMethod("Cancel").Invoke(run, null);
                        Measurements["cancel_signal_ms"] = timer.Elapsed.TotalMilliseconds;
                        Assert.That(entered.Wait(3000), Is.True);
                        work = (Task)type.GetField("CancellationWork").GetValue(run);
                        Assert.That(work.IsCompleted, Is.False, "Caller returned while cancellation callback was still blocked");
                        Assert.That(callback, Is.Not.EqualTo(caller));
                        Measurements["cancellation_callback_off_caller_thread"] = true;
                    }
                    finally
                    {
                        release.Set();
                        work?.Wait(3000);
                        type.GetMethod("Dispose").Invoke(run, null);
                    }
                }
            }
        }

        [Test]
        public void RecoveryWritesOnlyChangedStateAndKeepsCancellationDurable()
        {
            string[] keys = { "CHRIS.AssistanceTask", "CHRIS.AssistancePending", "CHRIS.AssistanceCancel" };
            var hadKeys = keys.Select(PlayerPrefs.HasKey).ToArray();
            var strings = keys.Take(2).Select(key => PlayerPrefs.GetString(key, "")).ToArray();
            int savedCancel = PlayerPrefs.GetInt(keys[2], 0);
            var obj = new GameObject("CHRIS recovery write checks");
            try
            {
                var client = obj.AddComponent<CHRISAssistanceClient>();
                TestCHRISAssistance.Property(client, "TaskId", null);
                TestCHRISAssistance.Property(client, "PendingRequest", null);
                TestCHRISAssistance.Property(client, "CancelWanted", false);
                var save = (Action)Delegate.CreateDelegate(typeof(Action), client,
                    typeof(CHRISAssistanceClient).GetMethod("SaveRecoveryState", BindingFlags.NonPublic | BindingFlags.Instance));
                save();
                int initialWrites = client.RecoverySaveCount;
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) save();
                Measurements["unchanged_recovery_1000_calls_ms"] = timer.Elapsed.TotalMilliseconds;
                Assert.That(client.RecoverySaveCount, Is.EqualTo(initialWrites));
                TestCHRISAssistance.Property(client, "CancelWanted", true);
                save();
                Assert.That(client.RecoverySaveCount, Is.EqualTo(initialWrites + 1));
                Assert.That(PlayerPrefs.GetInt("CHRIS.AssistanceCancel"), Is.EqualTo(1));
                Measurements["changed_recovery_save_ms"] = client.LastRecoverySaveMilliseconds;
                Measurements["unchanged_recovery_disk_writes"] = 0;
                timer.Restart();
                for (int i = 0; i < 20; i++)
                {
                    PlayerPrefs.SetString("CHRIS.AssistanceTask", "");
                    PlayerPrefs.SetString("CHRIS.AssistancePending", "");
                    PlayerPrefs.SetInt("CHRIS.AssistanceCancel", 1);
                    PlayerPrefs.Save();
                }
                Measurements["previous_repeated_save_mean_ms"] = timer.Elapsed.TotalMilliseconds / 20;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!hadKeys[i]) PlayerPrefs.DeleteKey(keys[i]);
                    else if (i < 2) PlayerPrefs.SetString(keys[i], strings[i]);
                    else PlayerPrefs.SetInt(keys[i], savedCancel);
                }
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void BrushSnapshotsAreOwnedAndRebuildOnlyWhenTheCatalogChanges()
        {
            var previousCatalog = BrushCatalog.m_Instance;
            var obj = new GameObject("CHRIS synthetic catalog benchmark");
            var brushes = new List<BrushDescriptor>();
            try
            {
                var catalog = obj.AddComponent<BrushCatalog>();
                var template = AssetDatabase.LoadAssetAtPath<BrushDescriptor>(
                    AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("t:BrushDescriptor").First()));
                for (int i = 0; i < 97; i++)
                {
                    var brush = UnityEngine.Object.Instantiate(template);
                    brush.m_Guid = Guid.NewGuid();
                    brush.m_DurableName = "Synthetic brush " + i;
                    brush.m_HiddenInGui = false;
                    brush.m_LocalizedDescription = null;
                    brushes.Add(brush);
                }
                TestCHRISAssistance.Set(catalog, "m_AllBrushes", new HashSet<BrushDescriptor>(brushes));
                using (var names = new CHRISBrushNames())
                {
                    var first = names.Snapshot(catalog);
                    var original = first.DeepClone();
                    first.RemoveAll();
                    Assert.That(JToken.DeepEquals(names.Snapshot(catalog), original), Is.True);
                    var timer = Stopwatch.StartNew();
                    for (int i = 0; i < 1000; i++) names.Snapshot(catalog);
                    Measurements["cached_synthetic_97_brushes_mean_ms"] = timer.Elapsed.TotalMilliseconds / 1000;
                    Assert.That(names.BuildCount, Is.EqualTo(1));
                    timer.Restart();
                    for (int i = 0; i < 1000; i++)
                    {
                        var rebuilt = new JObject();
                        foreach (var brush in brushes.OrderBy(brush => brush.m_Guid.ToString()))
                            rebuilt[brush.m_Guid.ToString()] = brush.m_DurableName;
                    }
                    Measurements["rebuild_synthetic_97_brushes_mean_ms"] = timer.Elapsed.TotalMilliseconds / 1000;
                    TestCHRISAssistance.Set(catalog, "m_AllBrushes", new HashSet<BrushDescriptor>(brushes.Take(96)));
                    Assert.That(names.Snapshot(catalog).Count, Is.EqualTo(96));
                    Assert.That(names.BuildCount, Is.EqualTo(2));
                    Assert.That(original.Count(), Is.EqualTo(97), "Previously captured approval context remains unchanged");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                foreach (var brush in brushes) UnityEngine.Object.DestroyImmediate(brush);
                BrushCatalog.m_Instance = previousCatalog;
            }
        }
    }
}
