// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    // A CHRIS window owns its generated materials, including controls never made visible.
    // Native tooltip materials, fonts and textures are shared assets and are not registered.
    [ExecuteAlways]
    public sealed class CHRISMaterialOwner : MonoBehaviour
    {
        readonly List<Material> m_Materials = new List<Material>();

        public static void Assign(Renderer renderer, Material material)
        {
            var popup = renderer.GetComponentInParent<CHRISNativePopup>(true);
            var panel = renderer.GetComponentInParent<CHRISFloatingPanel>(true);
            var root = popup != null ? popup.gameObject : panel != null ? panel.gameObject : renderer.gameObject;
            var owner = root.GetComponent<CHRISMaterialOwner>();
            if (owner == null) owner = root.AddComponent<CHRISMaterialOwner>();
            owner.m_Materials.Add(material);
            renderer.sharedMaterial = material;
        }

        public void Release()
        {
            foreach (var material in m_Materials)
            {
                if (material == null) continue;
                if (Application.isPlaying) Destroy(material);
                else DestroyImmediate(material);
            }
            m_Materials.Clear();
        }

        void OnDestroy() => Release();
    }
}
