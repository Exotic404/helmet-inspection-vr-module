using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace HelmetInspection
{
    [RequireComponent(typeof(Rigidbody), typeof(XRGrabInteractable))]
    public sealed class HelmetOutOfBoundsRecovery : MonoBehaviour
    {
        [SerializeField] Transform home;
        [SerializeField] Vector3 minimumRoomBounds = new Vector3(-2.25f, 0.08f, -2.25f);
        [SerializeField] Vector3 maximumRoomBounds = new Vector3(2.25f, 2.75f, 2.25f);
        [SerializeField, Min(0f)] float recoveryDelay = 0.6f;

        Rigidbody m_Body;
        XRGrabInteractable m_Grab;
        Coroutine m_Recovery;
        bool m_InitialIsKinematic;

        public Transform Home => home;
        public Vector3 MinimumRoomBounds => minimumRoomBounds;
        public Vector3 MaximumRoomBounds => maximumRoomBounds;

        void Awake()
        {
            m_Body = GetComponent<Rigidbody>();
            m_Grab = GetComponent<XRGrabInteractable>();
            m_InitialIsKinematic = m_Body.isKinematic;
        }

        void FixedUpdate()
        {
            var position = transform.position;
            var outside = position.x < minimumRoomBounds.x || position.y < minimumRoomBounds.y ||
                          position.z < minimumRoomBounds.z || position.x > maximumRoomBounds.x ||
                          position.y > maximumRoomBounds.y || position.z > maximumRoomBounds.z;

            // A far-ray target can be pushed beyond a wall by holding the stick.
            // Recover immediately even while selected so no helmet reaches the void.
            if (outside && m_Grab.isSelected)
            {
                ResetToHomeNow();
                return;
            }

            if (outside && m_Recovery == null)
                m_Recovery = StartCoroutine(ReturnHome());
            else if (!outside && m_Recovery != null)
            {
                StopCoroutine(m_Recovery);
                m_Recovery = null;
            }
        }

        IEnumerator ReturnHome()
        {
            yield return new WaitForSeconds(recoveryDelay);
            m_Recovery = null;
            if (home != null)
                ResetToHomeNow();
        }

        public void ResetToHomeNow()
        {
            if (home == null)
                return;
            if (m_Body == null || m_Grab == null)
                Awake();
            if (m_Recovery != null)
            {
                StopCoroutine(m_Recovery);
                m_Recovery = null;
            }

            if (m_Grab.isSelected && m_Grab.interactionManager != null)
                m_Grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)m_Grab);

            m_Body.isKinematic = true;
            transform.SetPositionAndRotation(home.position, home.rotation);
            m_Body.position = home.position;
            m_Body.rotation = home.rotation;
            Physics.SyncTransforms();
            m_Body.isKinematic = m_InitialIsKinematic;
            if (!m_Body.isKinematic)
            {
                m_Body.linearVelocity = Vector3.zero;
                m_Body.angularVelocity = Vector3.zero;
            }
            m_Body.Sleep();
        }

#if UNITY_EDITOR
        public void SetEditorHome(Transform homeTransform) => home = homeTransform;

        public void SetEditorSafetyBounds(Vector3 minimum, Vector3 maximum, float delay)
        {
            minimumRoomBounds = minimum;
            maximumRoomBounds = maximum;
            recoveryDelay = Mathf.Max(0f, delay);
        }
#endif
    }
}
