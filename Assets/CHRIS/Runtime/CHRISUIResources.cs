// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class CHRISUIResources : ScriptableObject
    {
        public Shader SurfaceShader;
        public TMP_FontAsset Font;
        public TMP_FontAsset BodyFont;
        public TMP_FontAsset HeadingFont;
        public Material BodyMaterial;
        public Material HeadingMaterial;
        public Mesh RoundedMesh;
        public Mesh BorderMesh;
        public Shader IconShader;
        public GameObject PopupPrefab;
        // The wireframe border of Open Brush's wand panels (the Labs panel "Border": its rounded
        // mesh, outline material and BakedMeshOutline), the native icon-button material, the
        // Oswald title font and the What's-new body font. Shared assets, copied by instantiation
        // and never owned or destroyed by CHRIS.
        public GameObject NativeBorder;
        public Material NativeIconMaterial;
        public TMP_FontAsset NativeFont;
        public TMP_FontAsset NativeBodyFont;
        public static CHRISUIResources Load() => Resources.Load<CHRISUIResources>("CHRIS/UI");

        // Copies the wand-panel border at the Labs panel's own scale, so its line width and corner
        // radius match Tools and Labs, and bakes its outline the way BasePanel.InitPanel does.
        public Renderer NativeWindow(Transform parent, Vector2 size, bool advanced)
        {
            var obj = Instantiate(NativeBorder, parent, false);
            obj.name = "Native panel border";
            obj.layer = parent.gameObject.layer;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localPosition = Vector3.zero;
            var filter = obj.GetComponent<MeshFilter>();
            // BakedMeshOutline bakes into the filter's own mesh instance, never the shared asset.
            // Outside Play mode a CHRIS copy stands in for that instance.
            var instance = Application.isPlaying ? filter.mesh : (filter.sharedMesh = Instantiate(filter.sharedMesh));
            CHRISMaterialOwner.Own(obj, instance);
            var scale = obj.transform.localScale;
            FitBorder(instance, new Vector2(size.x / scale.x, size.y / scale.y));
            var pm = PanelManager.m_Instance;
            Color line = pm != null ? pm.PanelBorderMeshBaseColor : Color.white;
            Color shadow = pm != null ? pm.PanelBorderMeshOutlineColor : Color.black;
            obj.GetComponent<BakedMeshOutline>().Bake(advanced ? line : shadow, advanced ? shadow : line,
                advanced ? 0.02f : 0.01f);
            CHRISMaterialOwner.Own(obj, filter.sharedMesh);
            var renderer = obj.GetComponent<Renderer>();
            CHRISMaterialOwner.Assign(renderer, new Material(renderer.sharedMaterial));
            return renderer;
        }

        // Resizes the rounded border ring like a nine-slice: each vertex moves outward by half the
        // size change on each axis, so the band width and corners keep their native shape.
        internal static void FitBorder(Mesh mesh, Vector2 size)
        {
            var bounds = mesh.bounds;
            var grow = new Vector2(size.x - bounds.size.x, size.y - bounds.size.y) / 2;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i] - bounds.center;
                vertices[i] = new Vector3(Mathf.Sign(v.x) * (Mathf.Abs(v.x) + grow.x),
                    Mathf.Sign(v.y) * (Mathf.Abs(v.y) + grow.y), v.z);
            }
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        // Title text as on the What's-new panel: Oswald, centred.
        public TextMeshPro NativeText(Transform parent, string name, Vector3 position, Vector2 size, string value,
            float fontSize, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            var text = Text(parent, name, position, size, value, fontSize, alignment);
            text.font = NativeFont;
            text.fontSharedMaterial = NativeFont.material;
            return text;
        }

        // Body text as on the What's-new panel: its body font, left aligned.
        public TextMeshPro NativeBody(Transform parent, string name, Vector3 position, Vector2 size, string value, float fontSize)
        {
            var text = Text(parent, name, position, size, value, fontSize, TextAlignmentOptions.Left);
            text.font = NativeBodyFont;
            text.fontSharedMaterial = NativeBodyFont.material;
            return text;
        }

        // A native wand-panel icon button: the PanelButton mesh and material with an icon from
        // Resources/Icons, driven by CHRISNativeButton (native BaseButton hover, press and input).
        // The hover text is the native button description.
        public CHRISNativeButton NativeIconButton(Transform parent, string name, Vector3 position, float size,
            string icon, string hover, Action click)
        {
            var obj = new GameObject(name);
            obj.SetActive(false);
            obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = Vector3.one * size;
            obj.AddComponent<MeshFilter>().sharedMesh = RoundedMesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            var material = new Material(NativeIconMaterial);
            material.mainTexture = Resources.Load<Texture2D>("Icons/" + icon);
            CHRISMaterialOwner.Assign(renderer, material);
            obj.AddComponent<BoxCollider>().size = new Vector3(1, 1, 0.1f);
            var button = obj.AddComponent<CHRISNativeButton>();
            button.Tint = Color.white;
            button.Click = click;
            obj.SetActive(true);
            // In Play mode BaseButton.Awake replaces the material with its own instance; own that too.
            if (renderer.sharedMaterial != material) CHRISMaterialOwner.Own(obj, renderer.sharedMaterial);
            button.SetHover(hover);
            return button;
        }

        // An invisible strip along the panel border: pressing the trigger on it starts a drag.
        public CHRISDragStrip DragStrip(Transform parent, string name, Vector3 position, Vector2 size, Action begin)
        {
            var obj = new GameObject(name);
            obj.SetActive(false);
            obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.AddComponent<BoxCollider>().size = new Vector3(size.x, size.y, 0.1f);
            var strip = obj.AddComponent<CHRISDragStrip>();
            strip.Begin = begin;
            obj.SetActive(true);
            strip.SetHover("Drag to move");
            return strip;
        }

        public static void SetLabel(CHRISNativeButton button, string label)
        {
            if (button.Label != null && button.Label.text != label) button.Label.text = label;
        }

        public GameObject Surface(Transform parent, string name, Vector3 position, Vector2 size, Color color)
        {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            obj.SetActive(false);
            obj.name = name;
            obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = new Vector3(size.x, size.y, 1);
            if (RoundedMesh != null)
            {
                obj.GetComponent<MeshFilter>().sharedMesh = RoundedMesh;
                obj.transform.localScale = new Vector3(size.x / RoundedMesh.bounds.size.x, size.y / RoundedMesh.bounds.size.y, 1);
            }
            DestroyImmediate(obj.GetComponent<Collider>());
            var material = new Material(SurfaceShader);
            material.SetColor("_Color", color);
            material.SetColor("_SecondaryColor", color * 0.7f);
            CHRISMaterialOwner.Assign(obj.GetComponent<Renderer>(), material);
            obj.SetActive(true);
            return obj;
        }

        public void Frame(Transform parent, Vector2 size)
        {
            var frame = Surface(parent, "Native rounded rim", new Vector3(0, 0, -0.015f), size, new Color(0.6f, 0.63f, 0.65f));
            frame.GetComponent<MeshFilter>().sharedMesh = BorderMesh;
            frame.transform.localScale = new Vector3(size.x / BorderMesh.bounds.size.x, size.y / BorderMesh.bounds.size.y, 0.4f);
        }

        public MeshRenderer Icon(Transform parent, string name, Vector3 position, float size)
        {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            obj.name = "Icon";
            obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = Vector3.one * size;
            DestroyImmediate(obj.GetComponent<Collider>());
            var renderer = obj.GetComponent<MeshRenderer>();
            CHRISMaterialOwner.Assign(renderer, new Material(IconShader));
            SetIcon(renderer, name);
            renderer.sharedMaterial.color = Color.white;
            return renderer;
        }

        internal static void SetIcon(MeshRenderer renderer, string name)
        {
            renderer.sharedMaterial.mainTexture = Resources.Load<Texture2D>("Icons/" + name);
            renderer.sharedMaterial.SetFloat("_AlphaMask", name == "ticktransparent" ? 1 : 0);
        }

        public TextMeshPro Text(Transform parent, string name, Vector3 position, Vector2 size, string value, float fontSize = 1.4f,
            TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
        {
            var obj = new GameObject(name);
            obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            var text = obj.AddComponent<TextMeshPro>();
            text.font = BodyFont != null ? BodyFont : Font;
            if (BodyMaterial != null) text.fontSharedMaterial = BodyMaterial;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.richText = false;
            text.alignment = alignment;
            text.rectTransform.sizeDelta = size;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.text = value;
            return text;
        }

        public CHRISNativeButton Button(Transform parent, string name, Vector3 position, Vector2 size, string label, Action click,
            bool stop = false)
        {
            Color color = stop ? new Color(0.72f, 0.015f, 0.015f) : new Color(0.09f, 0.105f, 0.12f);
            var obj = Surface(parent, name, position, size, color);
            obj.SetActive(false);
            var collider = obj.AddComponent<BoxCollider>();
            collider.size = new Vector3(size.x / obj.transform.localScale.x, size.y / obj.transform.localScale.y, 0.05f);
            var button = obj.AddComponent<CHRISNativeButton>();
            button.Click = click;
            button.Tint = color;
            button.Configure();
            // Keep text proportions independent of the rectangular button's scale.
            var holder = new GameObject("Label");
            holder.layer = obj.layer;
            holder.transform.SetParent(obj.transform, false);
            holder.transform.localScale = new Vector3(1 / obj.transform.localScale.x, 1 / obj.transform.localScale.y, 1);
            button.Label = Text(holder.transform, "Text", new Vector3(0, 0, -0.025f), size - new Vector2(0.08f, 0.02f), label, 1.25f,
                TextAlignmentOptions.Center);
            button.Icon = Icon(holder.transform, "mic", new Vector3(-size.x / 2 + 0.23f, 0, -0.03f), Math.Min(0.28f, size.y * 0.7f));
            button.Icon.gameObject.SetActive(false);
            obj.SetActive(true);
            return button;
        }
    }
}
