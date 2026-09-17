using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace HelmetInspection
{
    /// <summary>
    /// Places the live Quest tracking space at the authored training spawn after tracking
    /// becomes valid. Meta can preserve a Stage-space offset when an app is reopened in a
    /// different physical location; without this correction the room is left behind while
    /// tracked controllers (and the attached scanner) remain visible.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    public sealed class XRTrackingSpaceRecenter : MonoBehaviour
    {
        [SerializeField] XROrigin origin;
        [SerializeField] Vector3 authoredHeadSpawn = new Vector3(0f, 1.68f, -1.8f);
        [SerializeField] Vector3 authoredForward = Vector3.forward;
        [SerializeField, Range(1.45f, 1.9f)] float standingEyeHeight = 1.68f;
        [SerializeField, Min(1)] int trackingSettleFrames = 4;
        [SerializeField, Min(0.5f)] float longSuspendSeconds = 2f;
        [SerializeField, Min(0.25f)] float horizontalOutOfBoundsDistance = 2.25f;

        Coroutine m_RecenterRoutine;
        float m_SuspendedAt = -1f;
        float m_FocusLostAt = -1f;
        float m_LastRecenterAt = -10f;
        bool m_InitialPlacementComplete;
        bool m_PresenceKnown;
        bool m_HeadsetAbsent;
        bool m_HandoverPending;
        bool m_HasLastWornPose;
        bool m_HasReturnPose;
        bool m_ApplicationFocused = true;
        bool m_ApplicationPaused;
        bool m_HeightCalibrationPending = true;
        Transform m_StandingHeightOffset;
        Vector3 m_LastWornPosition;
        Vector3 m_LastWornForward;
        Vector3 m_ReturnPosition;
        Vector3 m_ReturnForward;

        public float StandingEyeHeight => standingEyeHeight;

        void Awake()
        {
            if (origin == null)
                origin = GetComponent<XROrigin>();
        }

        void Start()
        {
            // Let OpenXR publish a real HMD pose before using the camera transform.
            QueueRecenter("launch", true);
        }

        void Update()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid) return;
            if (head.TryGetFeatureValue(CommonUsages.userPresence, out var present))
                ObservePresence(present);
            if (!m_ApplicationFocused || m_ApplicationPaused || m_HeadsetAbsent ||
                !head.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) || !tracked)
                return;
            if ((!m_InitialPlacementComplete || m_HasReturnPose || m_HandoverPending) && m_RecenterRoutine == null)
                QueueRecenter("tracking acquired", true, m_HasReturnPose);
            if (m_InitialPlacementComplete && m_RecenterRoutine == null && !m_HasReturnPose &&
                origin != null && origin.Camera != null)
            {
                m_LastWornPosition = origin.Camera.transform.position;
                m_LastWornForward = origin.Camera.transform.forward;
                m_HasLastWornPose = true;
            }
        }

        void ObservePresence(bool present)
        {
            if (!m_PresenceKnown)
            {
                m_PresenceKnown = true;
                m_HeadsetAbsent = !present;
                return;
            }
            if (!present && !m_HeadsetAbsent)
            {
                RememberStandingPose();
                m_HandoverPending = true;
                m_HeightCalibrationPending = true;
                m_HeadsetAbsent = true;
            }
            else if (present && m_HeadsetAbsent)
            {
                m_HeadsetAbsent = false;
                QueueRecenter("headset handover", true, true);
            }
        }

        void RememberStandingPose()
        {
            if (!m_InitialPlacementComplete || m_HasReturnPose || !m_HasLastWornPose)
                return;
            // Use the last worn sample, not the pose after somebody starts carrying
            // the removed headset. Natural head movement is never cancelled while worn.
            m_ReturnPosition = m_LastWornPosition;
            m_ReturnForward = m_LastWornForward;
            m_HasReturnPose = true;
        }

        void OnApplicationPause(bool paused)
        {
            m_ApplicationPaused = paused;
            if (paused)
            {
                RememberStandingPose();
                m_SuspendedAt = Time.realtimeSinceStartup;
                return;
            }

            var wasLongSuspend = m_SuspendedAt >= 0f &&
                                 Time.realtimeSinceStartup - m_SuspendedAt >= longSuspendSeconds;
            if (wasLongSuspend) m_HeightCalibrationPending = true;
            m_SuspendedAt = -1f;
            var recenter = wasLongSuspend || IsHeadOutsideTrainingRoom();
            QueueRecenter("resume", recenter, true);
            if (!recenter && !m_HeadsetAbsent && !m_HandoverPending && m_RecenterRoutine == null) m_HasReturnPose = false;
        }

        void OnApplicationFocus(bool focused)
        {
            m_ApplicationFocused = focused;
            if (!focused)
            {
                RememberStandingPose();
                m_FocusLostAt = Time.realtimeSinceStartup;
                return;
            }

            var wasLongFocusLoss = m_FocusLostAt >= 0f &&
                                   Time.realtimeSinceStartup - m_FocusLostAt >= longSuspendSeconds;
            m_FocusLostAt = -1f;
            var recenter = wasLongFocusLoss || IsHeadOutsideTrainingRoom();
            QueueRecenter("focus regained", recenter, true);
            if (!recenter && !m_HeadsetAbsent && !m_HandoverPending && m_RecenterRoutine == null) m_HasReturnPose = false;
        }

        public void RecenterNow()
        {
            m_HeightCalibrationPending = true;
            QueueRecenter("manual", true);
        }

        void QueueRecenter(string reason, bool force, bool returnToStandingPosition = false)
        {
            if (!force || !isActiveAndEnabled || !m_ApplicationFocused || m_ApplicationPaused ||
                (!m_HasReturnPose && !m_HandoverPending && Time.realtimeSinceStartup - m_LastRecenterAt < 1f))
                return;
            if (m_RecenterRoutine != null)
                StopCoroutine(m_RecenterRoutine);
            m_RecenterRoutine = StartCoroutine(RecenterWhenTracked(reason, returnToStandingPosition));
        }

        IEnumerator RecenterWhenTracked(string reason, bool returnToStandingPosition)
        {
            if (origin == null || origin.Camera == null)
            {
                Debug.LogError("[XRRecenter] XR Origin or tracked camera is missing.");
                m_RecenterRoutine = null;
                yield break;
            }

            var targetPosition = returnToStandingPosition && m_HasReturnPose ? m_ReturnPosition : authoredHeadSpawn;
            var targetDirection = returnToStandingPosition && m_HasReturnPose ? m_ReturnForward : authoredForward;
            var stableFrames = 0;
            for (var frame = 0; frame < 180 && stableFrames < trackingSettleFrames; ++frame)
            {
                stableFrames = IsHeadTrackedAndWorn() ? stableFrames + 1 : 0;
                yield return null;
            }
            if (stableFrames < trackingSettleFrames || !IsHeadTrackedAndWorn())
            {
                // Never move the world based on an invalid/off-head pose. A later
                // tracked/worn frame retries initial placement or pending handover.
                m_RecenterRoutine = null;
                yield break;
            }
            var before = origin.Camera.transform.position;
            AlignTrackingSpace(targetPosition, targetDirection, m_HeightCalibrationPending || !m_InitialPlacementComplete);
            m_HeightCalibrationPending = false;
            m_InitialPlacementComplete = true;
            m_HasReturnPose = false;
            m_HandoverPending = false;
            m_LastWornPosition = origin.Camera.transform.position;
            m_LastWornForward = origin.Camera.transform.forward;
            m_HasLastWornPose = true;
            m_LastRecenterAt = Time.realtimeSinceStartup;
            m_RecenterRoutine = null;
            Debug.Log($"[XRRecenter] {reason}: head {before:F3} -> {origin.Camera.transform.position:F3}; " +
                      $"origin now {origin.transform.position:F3}. Natural head tracking preserved.");
        }

        bool IsHeadTrackedAndWorn()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return m_ApplicationFocused && !m_ApplicationPaused && head.isValid &&
                   origin != null && ResolveTrackingOriginMode() != TrackingOriginModeFlags.Unknown &&
                   (!head.TryGetFeatureValue(CommonUsages.userPresence, out var present) || present) &&
                   head.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && tracked;
        }

        TrackingOriginModeFlags ResolveTrackingOriginMode()
        {
            // XROrigin can miss a native event when Floor mode was already active
            // before it subscribed. Do not wait forever on an Unknown cached mode.
            var subsystem = InputDevices.GetDeviceAtXRNode(XRNode.Head).subsystem;
            if (subsystem != null && subsystem.running)
            {
                var activeMode = subsystem.GetTrackingOriginMode();
                if (activeMode != TrackingOriginModeFlags.Unknown) return activeMode;
            }
            return origin != null ? origin.CurrentTrackingOriginMode : TrackingOriginModeFlags.Unknown;
        }

        /// <summary>Reanchors the tracking origin once. Optional standing-height calibration
        /// adjusts a shared parent, never the camera/controller tracked local poses.</summary>
        public void AlignTrackingSpace(Vector3 targetStandingPosition, Vector3 forward, bool calibrateStandingHeight = false)
        {
            if (origin == null || origin.Camera == null) return;
            var cameraTransform = origin.Camera.transform;
            var character = origin.GetComponent<CharacterController>();
            var restoreCharacter = character != null && character.enabled;
            if (restoreCharacter)
                character.enabled = false;

            if (calibrateStandingHeight)
                CalibrateStandingHeight();

            var targetForward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            if (targetForward.sqrMagnitude > 0.5f)
                origin.MatchOriginUpCameraForward(Vector3.up, targetForward);

            // Keep the grounded origin's Y unchanged: moving the origin upward itself
            // would be undone by gravity. Height compensation lives in the tracking parent.
            var targetHeadPosition = new Vector3(targetStandingPosition.x, cameraTransform.position.y, targetStandingPosition.z);
            origin.MoveCameraToWorldLocation(targetHeadPosition);
            Physics.SyncTransforms();

            if (restoreCharacter)
                character.enabled = true;
        }

        void CalibrateStandingHeight()
        {
            var floorOffset = origin.CameraFloorOffsetObject != null ? origin.CameraFloorOffsetObject.transform : null;
            if (floorOffset == null || floorOffset == origin.Origin.transform ||
                !origin.Camera.transform.IsChildOf(floorOffset))
            {
                Debug.LogError("[XRRecenter] A shared camera/controller floor offset is required for standing-height calibration.");
                return;
            }
            if (m_StandingHeightOffset == null)
            {
                // XROrigin owns floorOffset.localPosition.y and resets it on native
                // tracking-origin updates. A separate, constant parent survives that
                // reset without interfering with the SDK or cancelling natural leaning.
                m_StandingHeightOffset = new GameObject("Standing Height Calibration").transform;
                m_StandingHeightOffset.SetParent(floorOffset.parent, false);
                floorOffset.SetParent(m_StandingHeightOffset, false);
            }
            // Resolve SDK floor/device offset before sampling, never the stale
            // authored preview height. Later SDK origin updates then stay consistent.
            var offsetPosition = floorOffset.localPosition;
            var trackingMode = ResolveTrackingOriginMode();
            if (trackingMode == TrackingOriginModeFlags.Floor)
                offsetPosition.y = 0f;
            else if (trackingMode == TrackingOriginModeFlags.Device || trackingMode == TrackingOriginModeFlags.Unbounded)
                offsetPosition.y = origin.CameraYOffset;
            floorOffset.localPosition = offsetPosition;
            var heightDelta = standingEyeHeight - origin.CameraInOriginSpaceHeight;
            m_StandingHeightOffset.position += origin.Origin.transform.TransformVector(Vector3.up * heightDelta);
        }

        bool IsHeadOutsideTrainingRoom()
        {
            if (origin == null || origin.Camera == null)
                return true;
            var head = origin.Camera.transform.position;
            var horizontal = new Vector2(head.x - authoredHeadSpawn.x, head.z - authoredHeadSpawn.z);
            return horizontal.magnitude > horizontalOutOfBoundsDistance || head.y < 0.2f || head.y > 2.8f;
        }

#if UNITY_EDITOR
        public void SetEditorReferences(XROrigin xrOrigin, Vector3 headSpawn, Vector3 forward)
        {
            origin = xrOrigin;
            authoredHeadSpawn = headSpawn;
            authoredForward = forward.normalized;
        }
#endif
    }
}
