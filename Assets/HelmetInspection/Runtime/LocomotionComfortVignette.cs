using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HelmetInspection
{
    /// <summary>Applies mild edge darkening only while smooth locomotion is active.</summary>
    public sealed class LocomotionComfortVignette : MonoBehaviour
    {
        [SerializeField] ComfortContinuousMoveProvider moveProvider;
        [SerializeField] Volume volume;
        [SerializeField, Range(0f, 1f)] float idleIntensity = 0.08f;
        [SerializeField, Range(0f, 1f)] float movingIntensity = 0.28f;
        [SerializeField, Min(0.01f)] float fadeTime = 0.18f;

        Vignette m_Vignette;
        float m_Velocity;

        void Awake()
        {
            if (volume != null && volume.sharedProfile != null)
                volume.sharedProfile.TryGet(out m_Vignette);
        }

        void Update()
        {
            if (m_Vignette == null)
                return;
            var amount = moveProvider != null ? moveProvider.CurrentInputMagnitude : 0f;
            var target = Mathf.Lerp(idleIntensity, movingIntensity, amount);
            m_Vignette.intensity.value = Mathf.SmoothDamp(m_Vignette.intensity.value, target,
                ref m_Velocity, fadeTime, Mathf.Infinity, Time.unscaledDeltaTime);
        }

#if UNITY_EDITOR
        public void SetEditorReferences(ComfortContinuousMoveProvider provider, Volume targetVolume)
        {
            moveProvider = provider;
            volume = targetVolume;
        }
#endif
    }
}
