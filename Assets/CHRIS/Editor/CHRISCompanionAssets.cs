// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace TiltBrush
{
    public static class CHRISCompanionAssets
    {
        public static void ConfigureAndVerify()
        {
            Configure();
            TestCHRISNativeUI.Run();
        }

        public static void Configure()
        {
            var resources = CHRISUIResources.Load();
            resources.BodyFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/Poly URL/Roboto-Medium SDF Poly.asset");
            resources.HeadingFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/Countdown Timer/Roboto-Bold SDF Timer Label.asset");
            resources.BodyMaterial = FontMaterial(resources.BodyFont, "BodyText");
            resources.HeadingMaterial = FontMaterial(resources.HeadingFont, "HeadingText");
            resources.RoundedMesh = AssetDatabase.LoadAllAssetsAtPath("Assets/Models/PanelButton_Large_Normalized.fbx").OfType<Mesh>().First();
            resources.BorderMesh = AssetDatabase.LoadAllAssetsAtPath("Assets/Models/PanelBorder_3x4_Rounded.fbx").OfType<Mesh>().First();
            resources.IconShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/CHRIS/Shaders/CHRISIcon.shader");
            EditorUtility.SetDirty(resources);
            string popupPath = AssetDatabase.GetAssetPath(resources.PopupPrefab);
            var popup = PrefabUtility.LoadPrefabContents(popupPath);
            try
            {
                popup.GetComponent<BoxCollider>().size = new Vector3(CHRISNativePopup.Width, CHRISNativePopup.Height, 0.1f);
                var settings = new SerializedObject(popup.GetComponent<CHRISNativePopup>());
                settings.FindProperty("m_ReticleBounds").vector3Value = new Vector3(CHRISNativePopup.Width, CHRISNativePopup.Height, -0.35f);
                settings.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(popup, popupPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(popup); }
            ConfigureLab();
            AssetDatabase.SaveAssets();
            Debug.Log("CHRIS native companion resources and Lab entry configured.");
        }

        static Material FontMaterial(TMP_FontAsset font, string name)
        {
            string path = "Assets/CHRIS/Resources/CHRIS/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(font.material);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_FaceColor", Color.white);
            material.SetFloat("_FaceDilate", 0);
            material.SetFloat("_OutlineWidth", 0);
            material.SetFloat("_OutlineSoftness", 0);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void ConfigureLab()
        {
            const string path = "Assets/Prefabs/Panels/LabsPanel.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var entry = root.GetComponentInChildren<CHRISLabButton>(true);
                var peer = root.GetComponentsInChildren<OptionButton>(true).First(button => button.name == "OptionButton_Export");
                var mesh = peer.transform.parent;
                var buttons = mesh.GetComponentsInChildren<BaseButton>(true).Where(button =>
                        button.transform.parent == mesh && button.gameObject.activeSelf && button != entry)
                    .OrderByDescending(button => button.transform.localPosition.y).ThenBy(button => button.transform.localPosition.x).ToList();
                bool creating = entry == null;
                if (creating)
                {
                    var clone = UnityEngine.Object.Instantiate(peer.gameObject, mesh);
                    clone.name = "CHRIS Lab";
                    var old = clone.GetComponent<OptionButton>();
                    entry = clone.AddComponent<CHRISLabButton>();
                    EditorUtility.CopySerializedManagedFieldsOnly(old, entry);
                    UnityEngine.Object.DestroyImmediate(old);
                }
                var settings = new SerializedObject(entry);
                settings.FindProperty("m_DescriptionText").stringValue = "CHRIS";
                var localized = settings.FindProperty("m_LocalizedDescription");
                if (localized != null)
                {
                    localized.FindPropertyRelative("m_TableReference.m_TableCollectionName").stringValue = "";
                    localized.FindPropertyRelative("m_TableEntryReference.m_KeyId").longValue = 0;
                    localized.FindPropertyRelative("m_TableEntryReference.m_Key").stringValue = "";
                }
                settings.FindProperty("m_ButtonTexture").objectReferenceValue = Resources.Load<Texture2D>("Icons/mic");
                settings.FindProperty("m_ToggleButton").boolValue = true;
                settings.ApplyModifiedPropertiesWithoutUndo();
                buttons.Add(entry);
                for (int i = 0; i < buttons.Count; i++)
                    buttons[i].transform.localPosition = new Vector3(-0.63f + i % 4 * 0.42f, 0.6f - i / 4 * 0.45f, 0.05f);
                var panel = root.GetComponent<BasePanel>();
                var panelSettings = new SerializedObject(panel);
                var bounds = panelSettings.FindProperty("m_ReticleBounds").vector3Value;
                bounds.x = 1.8f;
                panelSettings.FindProperty("m_ReticleBounds").vector3Value = bounds;
                var border = panelSettings.FindProperty("m_Border").objectReferenceValue as Renderer;
                if (creating && border != null) { var scale = border.transform.localScale; scale.x *= 4f / 3; border.transform.localScale = scale; }
                foreach (string property in new[] { "m_Collider", "m_MeshCollider" })
                    if (panelSettings.FindProperty(property).objectReferenceValue is BoxCollider collider)
                    { var size = collider.size; size.x = Math.Max(size.x, 1.8f); collider.size = size; }
                panelSettings.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

    }
}
