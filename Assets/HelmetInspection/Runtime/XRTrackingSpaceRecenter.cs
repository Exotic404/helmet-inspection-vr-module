using System.Collections;
using System.Collections.Generic;
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
        [SerializeField, Min(1)] int trackingSettleFrames = 4;
        [SerializeField, Min(0.5f)] float longSuspendSeconds = 2f;
        [SerializeField, Min(0.25f)] float horizontalOutOfBoundsDistance = 2.25f;

        readonly List<XRInputSubsystem> m_InputSubsystems = new List<XRInputSubsystem>();
        Coroutine m_RecenterRoutine;
        float m_SuspendedAt = -1f;
        float m_FocusLostAt = -1f;
        float m_LastRecenterAt = -10f;

        void Awake()
        {
            if (origin == null)
                origin = GetComponent<XROrigin>();
        }

        IEnumerator Start()
        {
            // Let OpenXR publish a real HMD pose before using the camera transform.
            yield return RecenterWhenTracked("launch", true);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                m_SuspendedAt = Time.realtimeSinceStartup;
                return;
            }

            var wasLongSuspend = m_SuspendedAt >= 0f &&
                                 Time.realtimeSinceStartup - m_SuspendedAt >= longSuspendSeconds;
            m_SuspendedAt = -1f;
            QueueRecenter("resume", wasLongSuspend || IsHeadOutsideTrainingRoom());
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                m_FocusLostAt = Time.realtimeSinceStartup;
                return;
            }

            var wasLongFocusLoss = m_FocusLostAt >= 0f &&
                                   Time.realtimeSinceStartup - m_FocusLostAt >= longSuspendSeconds;
            m_FocusLostAt = -1f;
            QueueRecenter("focus regained", wasLongFocusLoss || IsHeadOutsideTrainingRoom());
        }

        public void RecenterNow()
        {
            QueueRecenter("manual", true);
        }

        void QueueRecenter(string reason, bool force)
        {
            if (!force || !isActiveAndEnabled || Time.realtimeSinceStartup - m_LastRecenterAt < 1f)
                return;
            if (m_RecenterRoutine != null)
                StopCoroutine(m_RecenterRoutine);
            m_RecenterRoutine = StartCoroutine(RecenterWhenTracked(reason, true));
        }

        IEnumerator RecenterWhenTracked(string reason, bool force)
        {
            if (origin == null || origin.Camera == null)
            {
                Debug.LogError("[XRRecenter] XR Origin or tracked camera is missing.");
                m_RecenterRoutine = null;
                yield break;
            }

            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            for (var frame = 0; frame < 120; ++frame)
            {
                head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                if (head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) && tracked)
                    break;
                yield return null;
            }
            for (var frame = 0; frame < trackingSettleFrames; ++frame)
                yield return null;

            if (!force && !IsHeadOutsideTrainingRoom())
            {
                m_RecenterRoutine = null;
                yield break;
            }

            // Ask the runtime first. Stage/Floor spaces may reject recentering on Quest,
            // so the XROrigin relocation below is the deterministic fallback.
            m_InputSubsystems.Clear();
            SubsystemManager.GetSubsystems(m_InputSubsystems);
            foreach (var subsystem in m_InputSubsystems)
                if (subsystem.running)
                    subsystem.TryRecenter();
            yield return null;

            var cameraTransform = origin.Camera.transform;
            var before = cameraTransform.position;
            var character = origin.GetComponent<CharacterController>();
            var restoreCharacter = character != null && character.enabled;
            if (restoreCharacter)
                character.enabled = false;

            var targetForward = Vector3.ProjectOnPlane(authoredForward, Vector3.up).normalized;
            if (targetForward.sqrMagnitude > 0.5f)
                origin.MatchOriginUpCameraForward(Vector3.up, targetForward);

            // Preserve the live physical eye height. Only stale Stage-space X/Z and yaw
            // are reset, so standing and seated users keep correct floor relationship.
            var targetHeadPosition = new Vector3(authoredHeadSpawn.x, cameraTransform.position.y, authoredHeadSpawn.z);
            origin.MoveCameraToWorldLocation(targetHeadPosition);
            Physics.SyncTransforms();

            if (restoreCharacter)
                character.enabled = true;
            m_LastRecenterAt = Time.realtimeSinceStartup;
            m_RecenterRoutine = null;
            Debug.Log($"[XRRecenter] {reason}: head {before:F3} -> {cameraTransform.position:F3}; " +
                      $"origin now {origin.transform.position:F3}.");
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
