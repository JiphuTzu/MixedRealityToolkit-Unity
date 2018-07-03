﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.Toolkit.Internal.Definitions.Physics;
using Microsoft.MixedReality.Toolkit.Internal.EventDatum.Input;
using Microsoft.MixedReality.Toolkit.Internal.Extensions;
using Microsoft.MixedReality.Toolkit.Internal.Interfaces.InputSystem;
using Microsoft.MixedReality.Toolkit.Internal.Managers;
using Microsoft.MixedReality.Toolkit.Internal.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Microsoft.MixedReality.Toolkit.InputSystem.Focus
{
    /// <summary>
    /// The focus provider handles the focused objects per input source.
    /// <remarks>There are convenience properties for getting only Gaze Pointer if needed.</remarks>
    /// </summary>
    public class FocusProvider : MonoBehaviour, IMixedRealityFocusProvider
    {
        private IMixedRealityInputSystem inputSystem = null;
        public IMixedRealityInputSystem InputSystem => inputSystem ?? (inputSystem = MixedRealityManager.Instance.GetManager<IMixedRealityInputSystem>());

        /// <summary>
        /// Maximum distance at which the pointer can collide with an object.
        /// </summary>
        [SerializeField]
        private float pointingExtent = 10f;

        float IMixedRealityFocusProvider.GlobalPointingExtent => pointingExtent;

        /// <summary>
        /// The LayerMasks, in prioritized order, that are used to determine the GazeTarget when raycasting.
        /// <example>
        /// Allow the cursor to hit SR, but first prioritize any DefaultRaycastLayers (potentially behind SR)
        /// <code language="csharp"><![CDATA[
        /// int sr = LayerMask.GetMask("SR");
        /// int nonSR = Physics.DefaultRaycastLayers &amp; ~sr;
        /// GazeProvider.Instance.RaycastLayerMasks = new LayerMask[] { nonSR, sr };
        /// ]]></code>
        /// </example>
        /// </summary>
        [SerializeField]
        [Tooltip("The LayerMasks, in prioritized order, that are used to determine the GazeTarget when raycasting.")]
        private LayerMask[] pointingRaycastLayerMasks = { Physics.DefaultRaycastLayers };

        [SerializeField]
        private bool debugDrawPointingRays = false;

        [SerializeField]
        private Color[] debugDrawPointingRayColors = null;

        /// <summary>
        /// GazeProvider is a little special, so we keep track of it even if it's not a registered pointer. For the sake
        /// of StabilizationPlaneModifier and potentially other components that care where the user's looking, we need
        /// to do a gaze raycast even if gaze isn't used for focus.
        /// </summary>
        private PointerData gazeManagerPointingData;

        private readonly HashSet<PointerData> pointers = new HashSet<PointerData>();
        private readonly HashSet<GameObject> pendingOverallFocusEnterSet = new HashSet<GameObject>();
        private readonly HashSet<GameObject> pendingOverallFocusExitSet = new HashSet<GameObject>();
        private readonly List<PointerData> pendingPointerSpecificFocusChange = new List<PointerData>();

        /// <summary>
        /// Cached vector 3 reference to the new raycast position.
        /// <remarks>Only used to update UI raycast results.</remarks>
        /// </summary>
        private Vector3 newUiRaycastPosition = Vector3.zero;

        /// <summary>
        /// Camera to use for raycasting uGUI pointer events.
        /// </summary>
        [SerializeField]
        [Tooltip("Camera to use for raycasting uGUI pointer events.")]
        private Camera uiRaycastCamera = null;

        /// <summary>
        /// The Camera the Event System uses to raycast against.
        /// <para><remarks>Every uGUI canvas in your scene should use this camera as its event camera.</remarks></para>
        /// </summary>
        public Camera UIRaycastCamera
        {
            get
            {
                if (uiRaycastCamera == null)
                {
                    CreateUiRaycastCamera();
                }

                return uiRaycastCamera;
            }
        }

        /// <summary>
        /// To tap on a hologram even when not focused on,
        /// set OverrideFocusedObject to desired game object.
        /// If it's null, then focused object will be used.
        /// </summary>
        public GameObject OverrideFocusedObject { get; set; }

        [Serializable]
        private class PointerData : IPointerResult, IEquatable<PointerData>
        {
            public readonly IMixedRealityPointer Pointer;
            private FocusDetails focusDetails;

            private GraphicInputEventData graphicData;
            public GraphicInputEventData GraphicEventData
            {
                get
                {
                    if (graphicData == null)
                    {
                        graphicData = new GraphicInputEventData(EventSystem.current);
                    }

                    Debug.Assert(graphicData != null);

                    return graphicData;
                }
            }

            public PointerData(IMixedRealityPointer pointer)
            {
                Pointer = pointer;
            }

            public void UpdateHit(RaycastHit hit, RayStep sourceRay, int rayStepIndex)
            {
                PreviousPointerTarget = Details.Object;
                RayStepIndex = rayStepIndex;
                StartPoint = sourceRay.Origin;

                focusDetails.LastRaycastHit = hit;
                focusDetails.Point = hit.point;
                focusDetails.Normal = hit.normal;
                focusDetails.Object = hit.transform.gameObject;
                Details = focusDetails;
                CurrentPointerTarget = Details.Object;
            }

            public void UpdateHit(RaycastResult result, RaycastHit hit, RayStep sourceRay, int rayStepIndex)
            {
                // We do not update the PreviousPointerTarget here because
                // it's already been updated in the first physics raycast.

                RayStepIndex = rayStepIndex;
                StartPoint = sourceRay.Origin;

                focusDetails.Point = hit.point;
                focusDetails.Normal = hit.normal;
                focusDetails.Object = result.gameObject;
                Details = focusDetails;
            }

            public void UpdateHit()
            {
                PreviousPointerTarget = Details.Object;

                RayStep firstStep = Pointer.Rays[0];
                RayStep finalStep = Pointer.Rays[Pointer.Rays.Length - 1];
                RayStepIndex = 0;

                StartPoint = firstStep.Origin;

                focusDetails.Point = finalStep.Terminus;
                focusDetails.Normal = -finalStep.Direction;
                focusDetails.Object = null;
                Details = focusDetails;
                CurrentPointerTarget = Details.Object;
            }

            public void ResetFocusedObjects(bool clearPreviousObject = true)
            {
                if (clearPreviousObject)
                {
                    PreviousPointerTarget = null;
                }

                focusDetails.Point = Details.Point;
                focusDetails.Normal = Details.Normal;
                focusDetails.Object = null;
                Details = focusDetails;
                CurrentPointerTarget = null;
            }

            public bool Equals(PointerData other)
            {
                if (ReferenceEquals(null, other)) return false;
                if (ReferenceEquals(this, other)) return true;
                return Pointer.PointerId == other.Pointer.PointerId;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != GetType()) return false;
                return Equals((PointerData)obj);
            }

            public override int GetHashCode()
            {
                return Pointer != null ? Pointer.GetHashCode() : 0;
            }

            public Vector3 StartPoint { get; private set; }
            public FocusDetails Details { get; private set; }
            public GameObject CurrentPointerTarget { get; private set; }
            public GameObject PreviousPointerTarget { get; private set; }
            public int RayStepIndex { get; private set; }
        }

        #region MonoBehaviour Implementation

        private void Awake()
        {
            if (uiRaycastCamera == null)
            {
                Debug.LogWarning("No UIRaycastCamera assigned! Creating a new UIRaycastCamera.\n" +
                                 "To create a UIRaycastCamera in your scene, find this Focus Provider GameObject and add one there.");
                CreateUiRaycastCamera();
            }
        }

        private void Start()
        {
            Debug.Assert(MixedRealityManager.IsInitialized, "No Mixed Reality Manager found in the scene.  Be sure to run the Mixed Reality Configuration.");
            Debug.Assert(InputSystem != null, "No Input System found, Did you set it up in your configuration profile?");

            // Register the FocusProvider as a global listener to get input events.
            InputSystem.Register(gameObject);
        }

        private void Update()
        {
            UpdatePointers();
            UpdateFocusedObjects();
        }

        #endregion MonoBehaviour Implementation

        #region Focus Details by EventData

        /// <summary>
        /// Gets the currently focused object based on specified the event data.
        /// </summary>
        /// <param name="eventData"></param>
        /// <returns>Currently focused <see cref="GameObject"/> for the events input source.</returns>
        public GameObject GetFocusedObject(BaseInputEventData eventData)
        {
            Debug.Assert(eventData != null);
            if (OverrideFocusedObject != null) { return OverrideFocusedObject; }

            FocusDetails focusDetails;
            if (!TryGetFocusDetails(eventData, out focusDetails)) { return null; }

            IMixedRealityPointer pointer;
            TryGetPointingSource(eventData, out pointer);
            GraphicInputEventData graphicInputEventData = GetSpecificPointerGraphicEventData(pointer);
            Debug.Assert(graphicInputEventData != null);
            return graphicInputEventData.selectedObject;
        }

        /// <summary>
        /// Try to get the focus details based on the specified event data.
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="focusDetails"></param>
        /// <returns>True, if event data pointer input source is registered.</returns>
        public bool TryGetFocusDetails(BaseInputEventData eventData, out FocusDetails focusDetails)
        {
            foreach (var pointerData in pointers)
            {
                if (pointerData.Pointer.InputSourceParent.SourceId == eventData.SourceId)
                {
                    focusDetails = pointerData.Details;
                    return true;
                }
            }

            focusDetails = default(FocusDetails);
            return false;
        }

        /// <summary>
        /// Try to get the registered pointer source that raised the event.
        /// </summary>
        /// <param name="eventData"></param>
        /// <param name="pointer"></param>
        /// <returns>True, if event datas pointer input source is registered.</returns>
        public bool TryGetPointingSource(BaseInputEventData eventData, out IMixedRealityPointer pointer)
        {
            foreach (var pointerData in pointers)
            {
                if (pointerData.Pointer.InputSourceParent.SourceId == eventData.SourceId)
                {
                    pointer = pointerData.Pointer;
                    return true;
                }
            }

            pointer = null;
            return false;
        }

        #endregion Focus Details by EventData

        #region Focus Details by IMixedRealityPointer

        /// <summary>
        /// Gets the currently focused object for the pointing source.
        /// <para><remarks>If the pointing source is not registered, then the Gaze's Focused <see cref="GameObject"/> is returned.</remarks></para>
        /// </summary>
        /// <param name="pointingSource"></param>
        /// <returns>Currently Focused Object.</returns>
        public GameObject GetFocusedObject(IMixedRealityPointer pointingSource)
        {
            if (OverrideFocusedObject != null) { return OverrideFocusedObject; }

            FocusDetails focusDetails;
            if (!TryGetFocusDetails(pointingSource, out focusDetails)) { return null; }

            GraphicInputEventData graphicInputEventData = GetSpecificPointerGraphicEventData(pointingSource);
            Debug.Assert(graphicInputEventData != null);
            graphicInputEventData.selectedObject = focusDetails.Object;

            return focusDetails.Object;
        }

        /// <summary>
        /// Gets the currently focused object for the pointing source.
        /// </summary>
        /// <param name="pointer"></param>
        /// <param name="focusDetails"></param>
        public bool TryGetFocusDetails(IMixedRealityPointer pointer, out FocusDetails focusDetails)
        {
            foreach (var pointerData in pointers)
            {
                if (pointerData.Pointer.PointerId == pointer.PointerId)
                {
                    focusDetails = pointerData.Details;
                    return true;
                }
            }

            focusDetails = default(FocusDetails);
            return false;
        }

        /// <summary>
        /// Get the Graphic Event Data for the specified pointing source.
        /// </summary>
        /// <param name="pointer"></param>
        /// <returns></returns>
        public GraphicInputEventData GetSpecificPointerGraphicEventData(IMixedRealityPointer pointer)
        {
            return GetPointerData(pointer)?.GraphicEventData;
        }

        #endregion Focus Details by IMixedRealityPointer

        #region Utilities

        /// <summary>
        /// Generate a new unique pointer id.
        /// </summary>
        /// <returns></returns>
        public uint GenerateNewPointerId()
        {
            var newId = (uint)UnityEngine.Random.Range(1, int.MaxValue);

            foreach (var pointerData in pointers)
            {
                if (pointerData.Pointer.PointerId == newId)
                {
                    return GenerateNewPointerId();
                }
            }

            return newId;
        }

        /// <summary>
        /// Utility for creating the UIRaycastCamera.
        /// </summary>
        /// <returns>The UIRaycastCamera</returns>
        private void CreateUiRaycastCamera()
        {
            var cameraObject = new GameObject { name = "UIRaycastCamera" };
            cameraObject.transform.parent = transform;
            cameraObject.transform.localPosition = Vector3.zero;
            cameraObject.transform.localRotation = Quaternion.identity;
            uiRaycastCamera = cameraObject.AddComponent<Camera>();
            uiRaycastCamera.enabled = false;
            uiRaycastCamera.clearFlags = CameraClearFlags.Depth;
            uiRaycastCamera.cullingMask = CameraCache.Main.cullingMask;
            uiRaycastCamera.orthographic = true;
            uiRaycastCamera.orthographicSize = 0.5f;
            uiRaycastCamera.nearClipPlane = 0.1f;
            uiRaycastCamera.farClipPlane = 1000f;
            uiRaycastCamera.rect = new Rect(0, 0, 1, 1);
            uiRaycastCamera.depth = 0;
            uiRaycastCamera.renderingPath = RenderingPath.UsePlayerSettings;
            uiRaycastCamera.targetTexture = null;
            uiRaycastCamera.useOcclusionCulling = false;
            uiRaycastCamera.allowHDR = false;
            uiRaycastCamera.allowMSAA = false;
            uiRaycastCamera.allowDynamicResolution = false;
            uiRaycastCamera.targetDisplay = 1;
            uiRaycastCamera.stereoTargetEye = StereoTargetEyeMask.Both;
        }

        /// <summary>
        /// Helper for assigning world space canvases event cameras.
        /// <remarks>Warning! Very expensive. Use sparingly at runtime.</remarks>
        /// </summary>
        public void UpdateCanvasEventSystems()
        {
            Debug.Assert(UIRaycastCamera != null, "You must assign a UIRaycastCamera on the FocusProvider before updating your canvases.");

            // This will also find disabled GameObjects in the scene.
            // Warning! this look up is very expensive!
            var sceneCanvases = Resources.FindObjectsOfTypeAll<Canvas>();

            for (var i = 0; i < sceneCanvases.Length; i++)
            {
                if (sceneCanvases[i].isRootCanvas && sceneCanvases[i].renderMode == RenderMode.WorldSpace)
                {
                    sceneCanvases[i].worldCamera = UIRaycastCamera;
                }
            }
        }

        /// <summary>
        /// Checks if the pointer is registered with the Focus Manager.
        /// </summary>
        /// <param name="pointer"></param>
        /// <returns>True, if registered, otherwise false.</returns>
        public bool IsPointerRegistered(IMixedRealityPointer pointer)
        {
            Debug.Assert(pointer.PointerId != 0, $"{pointer} does not have a valid pointer id!");
            return GetPointerData(pointer) != null;
        }

        /// <summary>
        /// Registers the pointer with the Focus Manager.
        /// </summary>
        /// <param name="pointer"></param>
        /// <returns>True, if the pointer was registered, false if the pointer was previously registered.</returns>
        public bool RegisterPointer(IMixedRealityPointer pointer)
        {
            Debug.Assert(pointer.PointerId != 0, $"{pointer} does not have a valid pointer id!");

            if (IsPointerRegistered(pointer)) { return false; }

            pointers.Add(new PointerData(pointer));
            return true;
        }

        /// <summary>
        /// Unregisters the pointer with the Focus Manager.
        /// </summary>
        /// <param name="pointer"></param>
        /// <returns>True, if the pointer was unregistered, false if the pointer was not registered.</returns>
        public bool UnregisterPointer(IMixedRealityPointer pointer)
        {
            Debug.Assert(pointer.PointerId != 0, $"{pointer} does not have a valid pointer id!");

            PointerData pointerData = GetPointerData(pointer);
            if (pointerData == null) { return false; }

            // Raise focus events if needed.
            if (pointerData.CurrentPointerTarget != null)
            {
                GameObject unfocusedObject = pointerData.CurrentPointerTarget;
                bool objectIsStillFocusedByOtherPointer = false;

                foreach (var otherPointer in pointers)
                {
                    if (otherPointer.CurrentPointerTarget == unfocusedObject)
                    {
                        objectIsStillFocusedByOtherPointer = true;
                        break;
                    }
                }

                if (!objectIsStillFocusedByOtherPointer)
                {
                    InputSystem.RaiseFocusExit(pointer, unfocusedObject);
                }

                InputSystem.RaisePreFocusChanged(pointer, unfocusedObject, null);
            }

            pointers.Remove(pointerData);
            return true;
        }

        /// <summary>
        /// Returns the registered PointerData for the provided pointing input source.
        /// </summary>
        /// <param name="pointer"></param>
        /// <returns>Pointer Data if the pointing source is registered.</returns>
        private PointerData GetPointerData(IMixedRealityPointer pointer)
        {
            foreach (var pointerData in pointers)
            {
                if (pointerData.Pointer.PointerId == pointer.PointerId)
                {
                    return pointerData;
                }
            }

            return null;
        }

        private void UpdatePointers()
        {
            int pointerCount = 0;

            foreach (var pointer in pointers)
            {
                UpdatePointer(pointer);

                if (debugDrawPointingRays)
                {
                    Color rayColor;

                    if ((debugDrawPointingRayColors != null) && (debugDrawPointingRayColors.Length > 0))
                    {
                        rayColor = debugDrawPointingRayColors[pointerCount++ % debugDrawPointingRayColors.Length];
                    }
                    else
                    {
                        rayColor = Color.green;
                    }

                    Debug.DrawRay(pointer.StartPoint, (pointer.Details.Point - pointer.StartPoint), rayColor);
                }
            }
        }

        private void UpdatePointer(PointerData pointer)
        {
            // Call the pointer's OnPreRaycast function
            // This will give it a chance to prepare itself for raycasts
            // eg, by building its Rays array
            pointer.Pointer.OnPreRaycast();

            // If pointer interaction isn't enabled, clear its result object and return
            if (!pointer.Pointer.InteractionEnabled)
            {
                // Don't clear the previous focused object since we still want to trigger FocusExit events
                pointer.ResetFocusedObjects(false);
            }
            else
            {
                // If the pointer is locked
                // Keep the focus objects the same
                // This will ensure that we execute events on those objects
                // even if the pointer isn't pointing at them
                if (!pointer.Pointer.FocusLocked)
                {
                    // Otherwise, continue
                    var prioritizedLayerMasks = (pointer.Pointer.PrioritizedLayerMasksOverride ?? pointingRaycastLayerMasks);

                    // Perform raycast to determine focused object
                    RaycastPhysics(pointer, prioritizedLayerMasks);

                    // If we have a unity event system, perform graphics raycasts as well to support Unity UI interactions
                    if (EventSystem.current != null)
                    {
                        // NOTE: We need to do this AFTER RaycastPhysics so we use the current hit point to perform the correct 2D UI Raycast.
                        RaycastGraphics(pointer, prioritizedLayerMasks);
                    }

                    // Set the pointer's result last
                    pointer.Pointer.Result = pointer as IPointerResult;
                }
            }

            // Call the pointer's OnPostRaycast function
            // This will give it a chance to respond to raycast results
            // eg by updating its appearance
            pointer.Pointer.OnPostRaycast();
        }

        /// <summary>
        /// Perform a Unity physics Raycast to determine which scene objects with a collider is currently being gazed at, if any.
        /// </summary>
        /// <param name="pointer"></param>
        /// <param name="prioritizedLayerMasks"></param>
        private void RaycastPhysics(PointerData pointer, LayerMask[] prioritizedLayerMasks)
        {
            bool isHit = false;
            int rayStepIndex = 0;
            RayStep rayStep = default(RayStep);
            RaycastHit physicsHit = default(RaycastHit);

            Debug.Assert(pointer.Pointer.Rays != null, "No valid rays for pointer");
            Debug.Assert(pointer.Pointer.Rays.Length > 0, "No valid rays for pointer");

            // Check raycast for each step in the pointing source
            for (int i = 0; i < pointer.Pointer.Rays.Length; i++)
            {
                if (RaycastPhysicsStep(pointer.Pointer.Rays[i], prioritizedLayerMasks, out physicsHit))
                {
                    // Set the pointer source's origin ray to this step
                    isHit = true;
                    rayStep = pointer.Pointer.Rays[i];
                    rayStepIndex = i;
                    // No need to continue once we've hit something
                    break;
                }
            }

            if (isHit)
            {
                pointer.UpdateHit(physicsHit, rayStep, rayStepIndex);
            }
            else
            {
                pointer.UpdateHit();
            }
        }

        /// <summary>
        /// Raycasts each physics <see cref="RayStep"/>
        /// </summary>
        /// <param name="step"></param>
        /// <param name="prioritizedLayerMasks"></param>
        /// <param name="physicsHit"></param>
        /// <returns></returns>
        private bool RaycastPhysicsStep(RayStep step, LayerMask[] prioritizedLayerMasks, out RaycastHit physicsHit)
        {
            return prioritizedLayerMasks.Length == 1
                // If there is only one priority, don't prioritize
                ? Physics.Raycast(step.Origin, step.Direction, out physicsHit, step.Length, prioritizedLayerMasks[0])
                // Raycast across all layers and prioritize
                : TryGetPrioritizedHit(Physics.RaycastAll(step.Origin, step.Direction, step.Length, Physics.AllLayers), prioritizedLayerMasks, out physicsHit);
        }

        /// <summary>
        /// Tries to ge the prioritized raycast hit based on the prioritized layer masks.
        /// <para><remarks>Sorts all hit objects first by layerMask, then by distance.</remarks></para>
        /// </summary>
        /// <param name="hits"></param>
        /// <param name="priorityLayers"></param>
        /// <param name="raycastHit"></param>
        /// <returns>The minimum distance hit within the first layer that has hits</returns>
        private static bool TryGetPrioritizedHit(RaycastHit[] hits, LayerMask[] priorityLayers, out RaycastHit raycastHit)
        {
            raycastHit = default(RaycastHit);

            if (hits.Length == 0)
            {
                return false;
            }

            for (int layerMaskIdx = 0; layerMaskIdx < priorityLayers.Length; layerMaskIdx++)
            {
                RaycastHit? minHit = null;

                for (int hitIdx = 0; hitIdx < hits.Length; hitIdx++)
                {
                    RaycastHit hit = hits[hitIdx];
                    if (hit.transform.gameObject.layer.IsInLayerMask(priorityLayers[layerMaskIdx]) &&
                        (minHit == null || hit.distance < minHit.Value.distance))
                    {
                        minHit = hit;
                    }
                }

                if (minHit != null)
                {
                    raycastHit = minHit.Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Perform a Unity Graphics Raycast to determine which uGUI element is currently being gazed at, if any.
        /// </summary>
        /// <param name="pointer"></param>
        /// <param name="prioritizedLayerMasks"></param>
        private void RaycastGraphics(PointerData pointer, LayerMask[] prioritizedLayerMasks)
        {
            Debug.Assert(pointer.Details.Point != Vector3.zero, "No pointer source end point found to raycast against!");
            Debug.Assert(UIRaycastCamera != null, "You must assign a UIRaycastCamera on the FocusProvider before you can process uGUI raycasting.");

            RaycastResult raycastResult = default(RaycastResult);
            bool overridePhysicsRaycast = false;
            RayStep rayStep = default(RayStep);
            int rayStepIndex = 0;

            Debug.Assert(pointer.Pointer.Rays != null, "No valid rays for pointer");
            Debug.Assert(pointer.Pointer.Rays.Length > 0, "No valid rays for pointer");

            // Cast rays for every step until we score a hit
            for (int i = 0; i < pointer.Pointer.Rays.Length; i++)
            {
                if (RaycastGraphicsStep(pointer, pointer.Pointer.Rays[i], prioritizedLayerMasks, out overridePhysicsRaycast, out raycastResult))
                {
                    rayStepIndex = i;
                    rayStep = pointer.Pointer.Rays[i];
                    break;
                }
            }

            // Check if we need to overwrite the physics raycast info
            if ((pointer.CurrentPointerTarget == null || overridePhysicsRaycast) && raycastResult.isValid &&
                 raycastResult.module != null && raycastResult.module.eventCamera == UIRaycastCamera)
            {
                newUiRaycastPosition.x = raycastResult.screenPosition.x;
                newUiRaycastPosition.y = raycastResult.screenPosition.y;
                newUiRaycastPosition.z = raycastResult.distance;

                Vector3 worldPos = UIRaycastCamera.ScreenToWorldPoint(newUiRaycastPosition);

                var hitInfo = new RaycastHit
                {
                    point = worldPos,
                    normal = -raycastResult.gameObject.transform.forward
                };

                pointer.UpdateHit(raycastResult, hitInfo, rayStep, rayStepIndex);
            }
        }

        private bool RaycastGraphicsStep(PointerData pointer, RayStep step, LayerMask[] prioritizedLayerMasks, out bool overridePhysicsRaycast, out RaycastResult uiRaycastResult)
        {
            // Move the uiRaycast camera to the current pointer's position.
            UIRaycastCamera.transform.position = step.Origin;
            UIRaycastCamera.transform.forward = step.Direction;

            // We always raycast from the center of the camera.
            pointer.GraphicEventData.position = new Vector2(UIRaycastCamera.pixelWidth * 0.5f, UIRaycastCamera.pixelHeight * 0.5f);

            // Graphics raycast
            uiRaycastResult = EventSystem.current.Raycast(pointer.GraphicEventData, prioritizedLayerMasks);
            pointer.GraphicEventData.pointerCurrentRaycast = uiRaycastResult;

            overridePhysicsRaycast = false;

            // If we have a raycast result, check if we need to overwrite the physics raycast info
            if (uiRaycastResult.gameObject != null)
            {
                if (pointer.CurrentPointerTarget != null)
                {
                    // Check layer prioritization
                    if (prioritizedLayerMasks.Length > 1)
                    {
                        // Get the index in the prioritized layer masks
                        int uiLayerIndex = uiRaycastResult.gameObject.layer.FindLayerListIndex(prioritizedLayerMasks);
                        int threeDLayerIndex = pointer.Details.LastRaycastHit.collider.gameObject.layer.FindLayerListIndex(prioritizedLayerMasks);

                        if (threeDLayerIndex > uiLayerIndex)
                        {
                            overridePhysicsRaycast = true;
                        }
                        else if (threeDLayerIndex == uiLayerIndex)
                        {
                            if (pointer.Details.LastRaycastHit.distance > uiRaycastResult.distance)
                            {
                                overridePhysicsRaycast = true;
                            }
                        }
                    }
                    else
                    {
                        if (pointer.Details.LastRaycastHit.distance > uiRaycastResult.distance)
                        {
                            overridePhysicsRaycast = true;
                        }
                    }
                }
                // If we've hit something, no need to go further
                return true;
            }
            // If we haven't hit something, keep going
            return false;
        }

        /// <summary>
        /// Raises the Focus Events to the Input Manger if needed.
        /// </summary>
        private void UpdateFocusedObjects()
        {
            Debug.Assert(pendingPointerSpecificFocusChange.Count == 0);
            Debug.Assert(pendingOverallFocusExitSet.Count == 0);
            Debug.Assert(pendingOverallFocusEnterSet.Count == 0);

            // NOTE: We compute the set of events to send before sending the first event
            //       just in case someone responds to the event by adding/removing a
            //       pointer which would change the structures we're iterating over.

            foreach (var pointer in pointers)
            {
                if (pointer.PreviousPointerTarget != pointer.CurrentPointerTarget)
                {
                    pendingPointerSpecificFocusChange.Add(pointer);

                    // Initially, we assume all pointer-specific focus changes will
                    // also result in an overall focus change...

                    if (pointer.PreviousPointerTarget != null)
                    {
                        pendingOverallFocusExitSet.Add(pointer.PreviousPointerTarget);
                    }

                    if (pointer.CurrentPointerTarget != null)
                    {
                        pendingOverallFocusEnterSet.Add(pointer.CurrentPointerTarget);
                    }
                }
            }

            // ... but now we trim out objects whose overall focus was maintained the same by a different pointer:

            foreach (var pointer in pointers)
            {
                pendingOverallFocusExitSet.Remove(pointer.CurrentPointerTarget);
                pendingOverallFocusEnterSet.Remove(pointer.PreviousPointerTarget);
            }

            // Now we raise the events:
            for (int iChange = 0; iChange < pendingPointerSpecificFocusChange.Count; iChange++)
            {
                PointerData change = pendingPointerSpecificFocusChange[iChange];
                GameObject pendingUnfocusObject = change.PreviousPointerTarget;
                GameObject pendingFocusObject = change.CurrentPointerTarget;

                InputSystem.RaisePreFocusChanged(change.Pointer, pendingUnfocusObject, pendingFocusObject);

                if (pendingOverallFocusExitSet.Contains(pendingUnfocusObject))
                {
                    InputSystem.RaiseFocusExit(change.Pointer, pendingUnfocusObject);
                    pendingOverallFocusExitSet.Remove(pendingUnfocusObject);
                }

                if (pendingOverallFocusEnterSet.Contains(pendingFocusObject))
                {
                    InputSystem.RaiseFocusEnter(change.Pointer, pendingFocusObject);
                    pendingOverallFocusEnterSet.Remove(pendingFocusObject);
                }

                InputSystem.RaiseFocusChanged(change.Pointer, pendingUnfocusObject, pendingFocusObject);
            }

            Debug.Assert(pendingOverallFocusExitSet.Count == 0);
            Debug.Assert(pendingOverallFocusEnterSet.Count == 0);
            pendingPointerSpecificFocusChange.Clear();
        }

        #endregion Accessors

        #region ISourceState Implementation

        public void OnSourceDetected(SourceStateEventData eventData)
        {
            // If our input source does not have any pointers, then skip.
            if (eventData.InputSource.Pointers == null) { return; }

            foreach (var sourcePointer in eventData.InputSource.Pointers)
            {
                RegisterPointer(sourcePointer);

                // Special Registration for Gaze
                if (eventData.InputSource.SourceId == InputSystem.GazeProvider.GazeInputSource.SourceId)
                {
                    Debug.Assert(gazeManagerPointingData == null, "Gaze Manager Pointer Data was already registered!");

                    if (gazeManagerPointingData == null)
                    {
                        gazeManagerPointingData = new PointerData(sourcePointer);
                    }

                    Debug.Assert(gazeManagerPointingData != null);
                }
            }
        }

        public void OnSourceLost(SourceStateEventData eventData)
        {
            // If the input source does not have pointers, then skip.
            if (eventData.InputSource.Pointers == null) { return; }

            foreach (var sourcePointer in eventData.InputSource.Pointers)
            {
                // Special unregistration for Gaze
                if (eventData.InputSource.SourceId == InputSystem.GazeProvider.GazeInputSource.SourceId)
                {
                    Debug.Assert(gazeManagerPointingData != null);

                    // If the source lost is the gaze input source, then reset it.
                    if (sourcePointer.PointerId == gazeManagerPointingData.Pointer.PointerId)
                    {
                        gazeManagerPointingData.ResetFocusedObjects();
                        gazeManagerPointingData = null;
                    }
                }

                UnregisterPointer(sourcePointer);
            }
        }

        #endregion ISourceState Implementation
    }
}