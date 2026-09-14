using Unity.XR.CoreUtils;
using UnityEngine;

namespace HelmetInspection
{
    /// <summary>
    /// Removes real-world HMD translation while preserving tracked HMD rotation.
    /// The correction is applied to the shared camera/controller offset so the hands
    /// remain correctly positioned relative to the user's head. Virtual locomotion
    /// and snap turning still move the XR Origin normally.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    public sealed class XRPhysicalTranslationLock : MonoBehaviour
    {
        [SerializeField] XROrigin origin;
        [SerializeField] Vector3 anchoredHeadLocalPosition = new Vector3(0f, 1.68f, 0f);

        Transform m_CameraOffset;
        Transform m_TrackedCamera;

        public Vector3 AnchoredHeadLocalPosition => anchoredHeadLocalPosition;

        void Awake() => ResolveReferences();

        void OnEnable()
        {
            ResolveReferences();
            Application.onBeforeRender += ApplyTranslationLock;
        }

        void OnDisable() => Application.onBeforeRender -= ApplyTranslationLock;

        void LateUpdate() => ApplyTranslationLock();

        public void ApplyNow() => ApplyTranslationLock();

        void ResolveReferences()
        {
            if (origin == null)
                origin = GetComponent<XROrigin>();
            m_CameraOffset = origin != null && origin.CameraFloorOffsetObject != null
                ? origin.CameraFloorOffsetObject.transform
                : null;
            m_TrackedCamera = origin != null && origin.Camera != null
                ? origin.Camera.transform
                : null;
        }

        void ApplyTranslationLock()
        {
            if (m_CameraOffset == null || m_TrackedCamera == null || m_CameraOffset.parent == null)
            {
                ResolveReferences();
                if (m_CameraOffset == null || m_TrackedCamera == null || m_CameraOffset.parent == null)
                    return;
            }

            // Input System writes the tracked camera pose in Update and again before
            // rendering. Counter the positional part at their common parent each time.
            var headInOrigin = m_CameraOffset.parent.InverseTransformPoint(m_TrackedCamera.position);
            var correction = anchoredHeadLocalPosition - headInOrigin;
            if (correction.sqrMagnitude > 0.00000001f)
                m_CameraOffset.localPosition += correction;
        }

#if UNITY_EDITOR
        public void SetEditorReferences(XROrigin xrOrigin, Vector3 localHeadPosition)
        {
            origin = xrOrigin;
            anchoredHeadLocalPosition = localHeadPosition;
            ResolveReferences();
        }
#endif
    }
}
