// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush
{
    // A CHRIS window owns its generated materials and meshes, including controls never made visible.
    // Native tooltip materials, fonts and textures are shared assets and are not registered.
    [ExecuteAlways]
    public sealed class CHRISMaterialOwner : MonoBehaviour
    {
        readonly List<Object> m_Owned = new List<Object>();

        public static void Assign(Renderer renderer, Material material)
        {
            Own(renderer.gameObject, material);
            renderer.sharedMaterial = material;
        }

        public static void Own(GameObject user, Object generated)
        {
            var popup = user.GetComponentInParent<CHRISNativePopup>(true);
            var panel = user.GetComponentInParent<CHRISFloatingPanel>(true);
            var root = popup != null ? popup.gameObject : panel != null ? panel.gameObject : user;
            var owner = root.GetComponent<CHRISMaterialOwner>();
            if (owner == null) owner = root.AddComponent<CHRISMaterialOwner>();
            if (!owner.m_Owned.Contains(generated)) owner.m_Owned.Add(generated);
        }

        public void Release()
        {
            foreach (var generated in m_Owned)
            {
                if (generated == null) continue;
                if (Application.isPlaying) Destroy(generated);
                else DestroyImmediate(generated);
            }
            m_Owned.Clear();
        }

        void OnDestroy() => Release();
    }
}
