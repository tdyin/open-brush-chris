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
        public GameObject KeyboardPrefab;
        public GameObject PopupPrefab;
        public static CHRISUIResources Load() => Resources.Load<CHRISUIResources>("CHRIS/UI");

        public GameObject Surface(Transform parent, string name, Vector3 position, Vector2 size, Color color)
        {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            obj.SetActive(false);
            obj.name = name; obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = new Vector3(size.x, size.y, 1);
            DestroyImmediate(obj.GetComponent<Collider>());
            var material = new Material(SurfaceShader);
            material.SetColor("_Color", color); material.SetColor("_SecondaryColor", color * 0.7f);
            obj.GetComponent<Renderer>().sharedMaterial = material;
            obj.SetActive(true);
            return obj;
        }
        public TextMeshPro Text(Transform parent, string name, Vector3 position, Vector2 size, string value,
            float fontSize = 1.4f, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
        {
            var obj = new GameObject(name); obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false); obj.transform.localPosition = position;
            var text = obj.AddComponent<TextMeshPro>();
            text.font = Font; text.fontSize = fontSize; text.color = Color.white;
            text.richText = false; text.alignment = alignment;
            text.rectTransform.sizeDelta = size;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.text = value;
            return text;
        }
        public CHRISNativeButton Button(Transform parent, string name, Vector3 position, Vector2 size,
            string label, Action click, bool stop = false)
        {
            Color color = stop ? new Color(0.65f, 0.06f, 0.035f) : new Color(0.12f, 0.23f, 0.32f);
            var obj = Surface(parent, name, position, size, color);
            obj.SetActive(false);
            var collider = obj.AddComponent<BoxCollider>(); collider.size = new Vector3(1, 1, 0.05f);
            var button = obj.AddComponent<CHRISNativeButton>(); button.Click = click; button.Tint = color;
            button.Configure();
            // Keep text proportions independent of the rectangular button's scale.
            var holder = new GameObject("Label"); holder.layer = obj.layer; holder.transform.SetParent(obj.transform, false);
            holder.transform.localScale = new Vector3(1 / size.x, 1 / size.y, 1);
            button.Label = Text(holder.transform, "Text", new Vector3(0, 0, -0.025f),
                size - new Vector2(0.08f, 0.02f), label, 1.35f, TextAlignmentOptions.Center);
            obj.SetActive(true);
            return button;
        }
    }
}
