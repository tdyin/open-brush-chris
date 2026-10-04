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
        // Open Brush's own "What's new" panel frame (its stretchable border and background
        // pieces), the Oswald title font, the What's-new body font and the outlined button used
        // for "Find out more" (OpenBrowserButton). Shared assets, copied by instantiation and
        // never owned or destroyed by CHRIS.
        public GameObject[] NativeFrameParts;
        public TMP_FontAsset NativeFont;
        public TMP_FontAsset NativeBodyFont;
        public GameObject NativeButtonPrefab;
        public static CHRISUIResources Load() => Resources.Load<CHRISUIResources>("CHRIS/UI");

        // Copies the What's-new frame pieces with their native arrangement and fits their
        // measured bounds to the panel size.
        public GameObject NativeWindow(Transform parent, Vector2 size)
        {
            var obj = new GameObject("Native panel frame");
            obj.transform.SetParent(parent, false);
            foreach (var part in NativeFrameParts)
            {
                var copy = Instantiate(part, obj.transform, false);
                copy.name = part.name;
            }
            foreach (var t in obj.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = parent.gameObject.layer;
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            var bounds = new Bounds(obj.transform.InverseTransformPoint(renderers[0].bounds.center), Vector3.zero);
            foreach (var r in renderers)
            {
                bounds.Encapsulate(obj.transform.InverseTransformPoint(r.bounds.min));
                bounds.Encapsulate(obj.transform.InverseTransformPoint(r.bounds.max));
            }
            var scale = new Vector3(size.x / bounds.size.x, size.y / bounds.size.y, 1);
            obj.transform.localScale = scale;
            obj.transform.localPosition = -Vector3.Scale(new Vector3(bounds.center.x, bounds.center.y, 0), scale);
            return obj;
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

        // A copy of the What's-new outlined button. Its own script opens a web page, so the copy
        // gets CHRISNativeButton instead: the same native BaseButton hover, press and input path,
        // with no URL action. Scaled uniformly so the native label is not stretched.
        public CHRISNativeButton NativeButton(Transform parent, string name, Vector3 position, float width, string label, Action click)
        {
            var obj = Instantiate(NativeButtonPrefab, parent, false);
            obj.name = name;
            foreach (var t in obj.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = parent.gameObject.layer;
            foreach (var original in obj.GetComponents<BaseButton>()) DestroyImmediate(original);
            var nativeScale = NativeButtonPrefab.transform.localScale;
            var nativeWidth = NativeButtonPrefab.GetComponent<BoxCollider>().size.x * nativeScale.x;
            obj.transform.localPosition = position;
            obj.transform.localScale = nativeScale * (width / nativeWidth);
            var button = obj.AddComponent<CHRISNativeButton>();
            button.Label = obj.GetComponentInChildren<TextMeshPro>(true);
            button.Tint = Color.white;
            button.Click = click;
            SetLabel(button, label);
            return button;
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
