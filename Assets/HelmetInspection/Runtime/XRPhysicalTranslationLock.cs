using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

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
        XRBodyTransformer m_BodyTransformer;

        public Vector3 AnchoredHeadLocalPosition => anchoredHeadLocalPosition;

        void Awake() => ResolveReferences();

        void OnEnable()
        {
            ResolveReferences();
            m_BodyTransformer = GetComponentInChildren<XRBodyTransformer>(true);
            if (m_BodyTransformer != null)
                m_BodyTransformer.beforeApplyTransformations += BeforeLocomotion;
            Application.onBeforeRender += ApplyTranslationLock;
        }

        void OnDisable()
        {
            if (m_BodyTransformer != null)
                m_BodyTransformer.beforeApplyTransformations -= BeforeLocomotion;
            Application.onBeforeRender -= ApplyTranslationLock;
        }

        // XRI derives the collision capsule's center and height from the camera
        // immediately before Move. Correct the latest tracked translation first;
        // a LateUpdate-only lock lets physics use a different head pose to rendering.
        void BeforeLocomotion(XRBodyTransformer _) => ApplyTranslationLock();

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
