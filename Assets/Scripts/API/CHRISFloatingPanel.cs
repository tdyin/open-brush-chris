// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Linq;
using UnityEngine;

namespace TiltBrush
{
    // A separate native panel owns the CHRIS popup and keyboard. It never attaches to a hand.
    // Moving uses the ordinary UI trigger, so it also works in basic mode.
    [DefaultExecutionOrder(-100)]
    public class CHRISFloatingPanel : BasePanel
    {
        public bool IsDragging { get; private set; }
        float m_DragDistance;
        Vector3 m_DragOffset;

        public static CHRISFloatingPanel Create(Transform parent)
        {
            var obj = new GameObject("CHRIS floating panel");
            obj.transform.SetParent(parent, false);
            obj.AddComponent<UIComponentManager>();
            var panel = obj.AddComponent<CHRISFloatingPanel>();
            panel.BuildHost();
            return panel;
        }

        public void BuildHost()
        {
            m_PanelType = PanelType.CHRIS;
            m_Fixed = false;
            m_UseGazeRotation = false;
            m_PanelDescription = "CHRIS";
            m_Decor = Array.Empty<GameObject>();
            m_PromoBorders = Array.Empty<MeshRenderer>();
            m_PanelPopUpMap = Array.Empty<PopupMapKey>();
            m_ReticleBounds = new Vector3(3.8f, 4, 0);
            m_Mesh = CHRISUIResources.Load().Surface(transform, "Floating panel backing",
                Vector3.zero, new Vector2(3.8f, 4), new Color(0.025f, 0.045f, 0.06f));
            // Keep the mesh transform unscaled: BasePanel parents popups under this transform.
            m_Mesh.transform.localScale = Vector3.one;
            m_Border = m_Mesh.GetComponent<Renderer>();
            m_Border.enabled = false;
            var collider = m_Mesh.AddComponent<BoxCollider>();
            collider.size = m_ReticleBounds + new Vector3(0, 0, 0.1f);
            m_Collider = m_MeshCollider = collider;
        }

        public override void InitPanel()
        {
            base.InitPanel();
            VerifyStateForFloating();
            gameObject.SetActive(false);
        }

        public static void Show()
        {
            var manager = PanelManager.m_Instance;
            var panel = manager?.GetOrCreateCHRISPanel();
            if (panel == null || !manager.IsPanelAvailable(panel)) return;
            panel.PlaceInFront(ViewpointScript.Head);
            panel.gameObject.SetActive(true);
            if (panel.PanelPopUp == null)
                panel.CreatePopUp(CHRISUIResources.Load().PopupPrefab, Vector3.zero, false, true);
        }

        // Called only when opening/retrieving the window, never every frame.
        public void PlaceInFront(Transform head)
        {
            EndDrag();
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            transform.position = head.position + App.METERS_TO_UNITS *
                (forward * 0.75f + right * 0.32f - Vector3.up * 0.08f);
            FaceUser(head.position);
        }

        public void FaceUser(Vector3 headPosition)
        {
            // Native panel fronts point along local -Z. Rotate around the panel's fixed
            // position, using world up so head roll does not tilt the controls sideways.
            var forward = transform.position - headPosition;
            if (forward.sqrMagnitude < 0.000001f) return;
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            m_Mesh.transform.rotation = transform.rotation;
        }

        static bool PointerRay(out Ray ray)
        {
            ray = default;
            if (InputManager.m_Instance == null) return false;
            if (App.Config.m_SdkMode != SdkMode.Monoscopic)
            {
                if (InputManager.Brush == null || !InputManager.Brush.IsTrackedObjectValid) return false;
                var pointer = InputManager.m_Instance.GetBrushControllerAttachPoint();
                ray = new Ray(pointer.position, pointer.forward);
            }
            else ray = ViewpointScript.Gaze;
            return true;
        }

        public void BeginDrag()
        {
            if (!PointerRay(out var ray) || PanelPopUp == null || !PanelPopUp.IsOpen()) return;
            CHRISPanel.Instance?.Back(); // A pending confirmation must not survive a move gesture.
            BeginDrag(ray, SketchControlsScript.m_Instance.GetUIReticlePos());
        }

        public void BeginDrag(Ray ray, Vector3 hit)
        {
            m_DragDistance = Mathf.Max(0.1f, Vector3.Dot(hit - ray.origin, ray.direction));
            m_DragOffset = transform.position - ray.GetPoint(m_DragDistance);
            IsDragging = true;
        }

        public void MoveDrag(Ray ray, bool held)
        {
            if (!IsDragging) return;
            if (!held) { EndDrag(); return; }
            transform.position = ray.GetPoint(m_DragDistance) + m_DragOffset;
        }

        public void EndDrag() { IsDragging = false; }

        public override bool RaycastAgainstMeshCollider(Ray ray, out RaycastHit hit, float distance)
        {
            // The wand menus use a short reach. A floating window must remain selectable
            // at its spawn position and after it has been moved farther from the controller.
            return base.RaycastAgainstMeshCollider(ray, out hit, Mathf.Max(distance, 2 * App.METERS_TO_UNITS));
        }

        void Update()
        {
            BaseUpdate();
            if (IsDragging)
            {
                if (PanelPopUp == null || !PanelPopUp.IsOpen() || !PointerRay(out var ray))
                    EndDrag();
                else MoveDrag(ray, InputManager.m_Instance.GetCommand(InputManager.SketchCommands.Activate));
            }
            var head = ViewpointScript.Head;
            if (head != null) FaceUser(head.position);
        }

        protected override void OnDisablePanel()
        {
            base.OnDisablePanel();
            EndDrag();
            // Native availability changes may hide the host with a keyboard on top of CHRIS.
            // Dispose the whole popup stack, releasing direct ownership and queued confirmation.
            var popup = m_ActivePopUp;
            m_ActivePopUp = null;
            while (popup != null)
            {
                var previous = popup.m_PreviousPopUp;
                if (popup is CHRISNativePopup chris) CHRISPanel.Instance?.Closed(chris);
                if (Application.isPlaying) Destroy(popup.gameObject);
                else DestroyImmediate(popup.gameObject);
                popup = previous;
            }
        }
    }
}
