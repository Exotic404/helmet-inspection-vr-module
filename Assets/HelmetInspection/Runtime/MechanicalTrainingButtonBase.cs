using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HelmetInspection
{
    [RequireComponent(typeof(XRSimpleInteractable))]
    [RequireComponent(typeof(BoxCollider))]
    public abstract class MechanicalTrainingButtonBase : MonoBehaviour
    {
        [SerializeField] protected TrainingSessionController session;
        [SerializeField] Transform buttonCap;
        [SerializeField] Renderer capRenderer;
        [SerializeField] Color idleColor = new Color(0.12f, 0.72f, 0.36f, 1f);
        [SerializeField] Color hoverColor = new Color(0.25f, 0.95f, 0.55f, 1f);
        [SerializeField] Color pressedColor = new Color(0.7f, 1f, 0.8f, 1f);
        [SerializeField, Min(0f)] float pressTravel = 0.006f;
        [SerializeField, Min(0.05f)] float pressDuration = 0.13f;

        XRSimpleInteractable m_Interactable;
        Vector3 m_RaisedPosition;
        Coroutine m_Animation;
        AudioSource m_AudioSource;
        AudioClip m_Chime;
        MaterialPropertyBlock m_Block;
        bool m_Hovered;

        protected virtual void Awake()
        {
            AlignInteractionColliderToVisibleCap();
            m_Interactable = GetComponent<XRSimpleInteractable>();
            m_Interactable.hoverEntered.AddListener(OnHoverEntered);
            m_Interactable.hoverExited.AddListener(OnHoverExited);
            m_Interactable.selectEntered.AddListener(OnSelected);
            if (buttonCap != null)
                m_RaisedPosition = buttonCap.localPosition;
            m_AudioSource = gameObject.AddComponent<AudioSource>();
            m_AudioSource.spatialBlend = 1f;
            m_AudioSource.playOnAwake = false;
            m_Chime = BuildChime();
            m_Block = new MaterialPropertyBlock();
            ApplyVisual(idleColor, 0.45f);
        }

        protected virtual void OnDestroy()
        {
            if (m_Interactable == null)
                return;
            m_Interactable.hoverEntered.RemoveListener(OnHoverEntered);
            m_Interactable.hoverExited.RemoveListener(OnHoverExited);
            m_Interactable.selectEntered.RemoveListener(OnSelected);
        }

        void OnHoverEntered(HoverEnterEventArgs _) { m_Hovered = true; ApplyVisual(hoverColor, 0.9f); }
        void OnHoverExited(HoverExitEventArgs _) { m_Hovered = false; ApplyVisual(idleColor, 0.45f); }

        void OnSelected(SelectEnterEventArgs args)
        {
            if (m_Animation != null)
                StopCoroutine(m_Animation);
            m_Animation = StartCoroutine(AnimatePress());
            m_AudioSource.PlayOneShot(m_Chime, 0.55f);
            if (args.interactorObject is XRBaseInputInteractor inputInteractor)
                inputInteractor.SendHapticImpulse(0.28f, 0.07f);
            Activate();
        }

        IEnumerator AnimatePress()
        {
            if (buttonCap == null)
                yield break;
            var pressed = m_RaisedPosition + Vector3.forward * pressTravel;
            var half = pressDuration * 0.5f;
            for (var elapsed = 0f; elapsed < half; elapsed += Time.unscaledDeltaTime)
            {
                buttonCap.localPosition = Vector3.Lerp(m_RaisedPosition, pressed, elapsed / half);
                ApplyVisual(pressedColor, 1.8f);
                yield return null;
            }
            for (var elapsed = 0f; elapsed < half; elapsed += Time.unscaledDeltaTime)
            {
                buttonCap.localPosition = Vector3.Lerp(pressed, m_RaisedPosition, elapsed / half);
                yield return null;
            }
            buttonCap.localPosition = m_RaisedPosition;
            ApplyVisual(m_Hovered ? hoverColor : idleColor, m_Hovered ? 0.9f : 0.45f);
            m_Animation = null;
        }

        void ApplyVisual(Color color, float emission)
        {
            if (capRenderer == null)
                return;
            capRenderer.GetPropertyBlock(m_Block);
            m_Block.SetColor("_BaseColor", color);
            m_Block.SetColor("_EmissionColor", color * emission);
            capRenderer.SetPropertyBlock(m_Block);
        }

        protected abstract void Activate();

        /// <summary>
        /// Keeps the interaction volume on the visible button even when a designer moves
        /// the cap independently of its original root transform in the scene.
        /// </summary>
        public void AlignInteractionColliderToVisibleCap()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null || capRenderer == null)
                return;

            var bounds = capRenderer.bounds;
            var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var x = 0; x < 2; ++x)
            for (var y = 0; y < 2; ++y)
            for (var z = 0; z < 2; ++z)
            {
                var worldCorner = new Vector3(
                    x == 0 ? bounds.min.x : bounds.max.x,
                    y == 0 ? bounds.min.y : bounds.max.y,
                    z == 0 ? bounds.min.z : bounds.max.z);
                var localCorner = transform.InverseTransformPoint(worldCorner);
                minimum = Vector3.Min(minimum, localCorner);
                maximum = Vector3.Max(maximum, localCorner);
            }

            box.center = (minimum + maximum) * 0.5f;
            box.size = maximum - minimum + new Vector3(0.012f, 0.012f, 0.012f);
            box.isTrigger = false;
        }

        static AudioClip BuildChime()
        {
            const int rate = 24000;
            const float duration = 0.12f;
            var samples = Mathf.CeilToInt(rate * duration);
            var data = new float[samples];
            for (var i = 0; i < samples; ++i)
            {
                var t = i / (float)rate;
                data[i] = Mathf.Sin(2f * Mathf.PI * 620f * t) * Mathf.Exp(-24f * t) * 0.35f;
            }
            var clip = AudioClip.Create("Mechanical Button Chime", samples, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

#if UNITY_EDITOR
        public void SetEditorReferences(TrainingSessionController owner, Transform cap, Renderer renderer,
            Color idle, Color hover, Color pressed)
        {
            session = owner;
            buttonCap = cap;
            capRenderer = renderer;
            idleColor = idle;
            hoverColor = hover;
            pressedColor = pressed;
        }
#endif
    }
}
