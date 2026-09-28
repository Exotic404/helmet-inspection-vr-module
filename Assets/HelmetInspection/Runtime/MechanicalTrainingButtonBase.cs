using System.Collections;
using UnityEngine;
using UnityEngine.XR;
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
        [SerializeField, Min(0.1f)] float activationCooldown = 0.25f;
        [SerializeField, Range(0f, 1f)] float hoverTintStrength = 0.16f;
        [SerializeField, Range(0f, 1f)] float pressedTintStrength = 0.28f;
        [Header("Interaction sound")]
        [Tooltip("Optional replacement for the generated mechanical click and confirmation tone.")]
        [SerializeField] AudioClip interactionClip;
        [SerializeField, Range(0f, 1f)] float interactionVolume = 0.8f;
        [Tooltip("Partly spatial sound stays audible while still locating the control in the room.")]
        [SerializeField, Range(0f, 1f)] float soundSpatialBlend = 0.55f;
        [SerializeField, Min(0.1f)] float soundMinDistance = 1.5f;
        [SerializeField, Min(1f)] float soundMaxDistance = 9f;

        XRSimpleInteractable m_Interactable;
        Vector3 m_RaisedPosition;
        Coroutine m_Animation;
        AudioSource m_AudioSource;
        AudioClip m_GeneratedClip;
        AudioClip m_MechanicalClip;
        MaterialPropertyBlock m_Block;
        bool m_Hovered;
        bool m_Pressed;
        bool m_ListenersAttached;
        bool m_WarnedMissingSession;
        float m_LastActivationTime = float.NegativeInfinity;

        protected virtual bool CanActivate => session != null && session.isActiveAndEnabled;
        protected virtual bool UsesRestartFeedback => false;

        protected virtual void Awake()
        {
            AlignInteractionColliderToVisibleCap();
            m_Interactable = GetComponent<XRSimpleInteractable>();
            if (buttonCap != null)
                m_RaisedPosition = buttonCap.localPosition;
            m_AudioSource = gameObject.AddComponent<AudioSource>();
            m_AudioSource.playOnAwake = false;
            m_AudioSource.spatialBlend = soundSpatialBlend;
            m_AudioSource.minDistance = Mathf.Max(0.1f, soundMinDistance);
            m_AudioSource.maxDistance = Mathf.Max(m_AudioSource.minDistance + 0.1f, soundMaxDistance);
            m_AudioSource.rolloffMode = AudioRolloffMode.Linear;
            m_AudioSource.dopplerLevel = 0f;
            m_AudioSource.spread = 70f;
            m_AudioSource.priority = 64;
            if (interactionClip == null)
                m_GeneratedClip = BuildInteractionClip(UsesRestartFeedback);
            m_MechanicalClip = BuildInteractionClip(false, false);
            m_Block = new MaterialPropertyBlock();
            ApplyRestingVisual();
        }

        protected virtual void OnEnable()
        {
            if (m_Interactable == null || m_ListenersAttached)
                return;
            m_Interactable.hoverEntered.AddListener(OnHoverEntered);
            m_Interactable.hoverExited.AddListener(OnHoverExited);
            m_Interactable.selectEntered.AddListener(OnSelected);
            m_ListenersAttached = true;
            m_Hovered = m_Interactable.isHovered;
            ApplyRestingVisual();
        }

        protected virtual void OnDisable()
        {
            RemoveListeners();
            if (m_Animation != null)
                StopCoroutine(m_Animation);
            m_Animation = null;
            m_Hovered = false;
            m_Pressed = false;
            if (buttonCap != null)
                buttonCap.localPosition = m_RaisedPosition;
            ApplyRestingVisual();
            if (m_AudioSource != null)
                m_AudioSource.Stop();
        }

        void RemoveListeners()
        {
            if (m_Interactable == null || !m_ListenersAttached)
                return;
            m_Interactable.hoverEntered.RemoveListener(OnHoverEntered);
            m_Interactable.hoverExited.RemoveListener(OnHoverExited);
            m_Interactable.selectEntered.RemoveListener(OnSelected);
            m_ListenersAttached = false;
        }

        protected virtual void OnDestroy()
        {
            RemoveListeners();
            // Destroy only the assets/components this button created, never a supplied clip.
            if (m_GeneratedClip != null)
                Destroy(m_GeneratedClip);
            if (m_MechanicalClip != null)
                Destroy(m_MechanicalClip);
            if (m_AudioSource != null)
                Destroy(m_AudioSource);
        }

        void OnHoverEntered(HoverEnterEventArgs _)
        {
            m_Hovered = true;
            if (!m_Pressed)
                ApplyRestingVisual();
        }

        void OnHoverExited(HoverExitEventArgs _)
        {
            // Another hand can still be pointing at the same control.
            m_Hovered = m_Interactable != null && m_Interactable.isHovered;
            if (!m_Pressed)
                ApplyRestingVisual();
        }

        void OnSelected(SelectEnterEventArgs args)
        {
            if (!isActiveAndEnabled || Time.unscaledTime - m_LastActivationTime < Mathf.Max(0.1f, activationCooldown))
                return;
            var accepted = CanActivate;
            if (!accepted)
            {
                if (session == null && !m_WarnedMissingSession)
                {
                    Debug.LogWarning($"{name}: session control has no TrainingSessionController reference.", this);
                    m_WarnedMissingSession = true;
                }
            }

            // Accept the session operation before presenting success feedback. The cooldown
            // also prevents two hands or a near/far handover from resetting twice.
            m_LastActivationTime = Time.unscaledTime;
            if (accepted)
                Activate();
            if (!isActiveAndEnabled)
                return;
            if (m_Animation != null)
                StopCoroutine(m_Animation);
            m_Pressed = true;
            m_Animation = StartCoroutine(AnimatePress());
            // A START press during an active session still feels like a physical button,
            // but never plays the success tones for an operation that did not take place.
            var clip = accepted ? (interactionClip != null ? interactionClip : m_GeneratedClip) : m_MechanicalClip;
            if (m_AudioSource != null && clip != null)
                m_AudioSource.PlayOneShot(clip, interactionVolume);
            SendPressHaptics(args.interactorObject, accepted ? 0.32f : 0.12f);
        }

        IEnumerator AnimatePress()
        {
            var pressed = m_RaisedPosition + Vector3.forward * pressTravel;
            var half = Mathf.Max(0.05f, pressDuration) * 0.5f;
            ApplyVisual(Color.Lerp(idleColor, pressedColor, pressedTintStrength));
            for (var elapsed = 0f; elapsed < half; elapsed += Time.unscaledDeltaTime)
            {
                if (buttonCap != null)
                    buttonCap.localPosition = Vector3.Lerp(m_RaisedPosition, pressed, Mathf.SmoothStep(0f, 1f, elapsed / half));
                yield return null;
            }
            for (var elapsed = 0f; elapsed < half; elapsed += Time.unscaledDeltaTime)
            {
                if (buttonCap != null)
                    buttonCap.localPosition = Vector3.Lerp(pressed, m_RaisedPosition, Mathf.SmoothStep(0f, 1f, elapsed / half));
                yield return null;
            }
            if (buttonCap != null)
                buttonCap.localPosition = m_RaisedPosition;
            m_Pressed = false;
            ApplyRestingVisual();
            m_Animation = null;
        }

        void ApplyRestingVisual() => ApplyVisual(m_Hovered ? Color.Lerp(idleColor, hoverColor, hoverTintStrength) : idleColor);

        void ApplyVisual(Color color)
        {
            if (capRenderer == null || m_Block == null)
                return;
            capRenderer.GetPropertyBlock(m_Block);
            m_Block.SetColor("_BaseColor", color);
            // The mushroom cap is painted plastic; small separate pilot lights supply the glow.
            m_Block.SetColor("_EmissionColor", Color.black);
            capRenderer.SetPropertyBlock(m_Block);
        }

        static void SendPressHaptics(IXRSelectInteractor interactor, float amplitude)
        {
            if (interactor is XRBaseInputInteractor inputInteractor && inputInteractor.SendHapticImpulse(amplitude, 0.07f))
                return;

            // XRI 3 near/far interactors can exist without a bound HapticImpulsePlayer.
            // Fall back only to the controller that actually activated this button.
            if (interactor == null || interactor.handedness == InteractorHandedness.None)
                return;
            var node = interactor.handedness == InteractorHandedness.Left ? XRNode.LeftHand : XRNode.RightHand;
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetHapticCapabilities(out var capabilities) && capabilities.supportsImpulse)
                device.SendHapticImpulse(0u, amplitude, 0.07f);
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

            // Transform the actual mesh bounds, not its world-axis-aligned bounding box.
            // This stays aligned when the cap/model or its parent has been rotated/scaled.
            var meshFilter = capRenderer.GetComponent<MeshFilter>();
            var bounds = meshFilter != null && meshFilter.sharedMesh != null
                ? meshFilter.sharedMesh.bounds
                : capRenderer.localBounds;
            var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var x = 0; x < 2; ++x)
            for (var y = 0; y < 2; ++y)
            for (var z = 0; z < 2; ++z)
            {
                var capCorner = new Vector3(
                    x == 0 ? bounds.min.x : bounds.max.x,
                    y == 0 ? bounds.min.y : bounds.max.y,
                    z == 0 ? bounds.min.z : bounds.max.z);
                var localCorner = transform.InverseTransformPoint(capRenderer.transform.TransformPoint(capCorner));
                minimum = Vector3.Min(minimum, localCorner);
                maximum = Vector3.Max(maximum, localCorner);
            }

            box.center = (minimum + maximum) * 0.5f;
            var size = maximum - minimum;
            var scale = new Vector3(
                Mathf.Max(0.0001f, transform.TransformVector(Vector3.right).magnitude),
                Mathf.Max(0.0001f, transform.TransformVector(Vector3.up).magnitude),
                Mathf.Max(0.0001f, transform.TransformVector(Vector3.forward).magnitude));
            // Six millimetres of padding per side and a usable 13 cm face, in world metres.
            box.size = new Vector3(
                Mathf.Max(size.x + 0.012f / scale.x, 0.13f / scale.x),
                Mathf.Max(size.y + 0.012f / scale.y, 0.13f / scale.y),
                Mathf.Max(size.z + 0.012f / scale.z, 0.04f / scale.z));
            box.isTrigger = false;
        }

        static AudioClip BuildInteractionClip(bool restart, bool includeConfirmation = true)
        {
            const int rate = 24000;
            var duration = includeConfirmation ? 0.27f : 0.09f;
            var samples = Mathf.CeilToInt(rate * duration);
            var data = new float[samples];
            var peak = 0f;
            uint noiseState = 0x2F6E2B1u;
            for (var i = 0; i < samples; ++i)
            {
                var t = i / (float)rate;
                // Deterministic noise avoids changing Unity's global Random state.
                noiseState ^= noiseState << 13;
                noiseState ^= noiseState >> 17;
                noiseState ^= noiseState << 5;
                var noise = (noiseState & 0xFFFFu) / 32767.5f - 1f;
                var attack = Mathf.Min(1f, t / 0.0015f);
                var click = attack * (noise * 0.43f * Mathf.Exp(-190f * t)
                    + Mathf.Sin(2f * Mathf.PI * 210f * t) * 0.26f * Mathf.Exp(-75f * t));
                var latchTime = t - 0.026f;
                if (latchTime >= 0f)
                    click += noise * 0.13f * Mathf.Exp(-220f * latchTime) * Mathf.Min(1f, latchTime / 0.001f);

                // Two restrained notes distinguish START (rising) from RESTART (falling).
                var firstFrequency = restart ? 740f : 620f;
                var secondFrequency = restart ? 520f : 930f;
                var tone = ConfirmationNote(t - 0.045f, firstFrequency)
                    + ConfirmationNote(t - 0.13f, secondFrequency);
                data[i] = click + (includeConfirmation ? tone : 0f);
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            if (peak > 0f)
                for (var i = 0; i < samples; ++i)
                    data[i] *= 0.85f / peak;
            var clipName = includeConfirmation
                ? (restart ? "Session Restart - Click and Confirmation" : "Session Start - Click and Confirmation")
                : "Session Control - Mechanical Click Only";
            var clip = AudioClip.Create(clipName,
                samples, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static float ConfirmationNote(float time, float frequency)
        {
            const float duration = 0.125f;
            if (time < 0f || time >= duration)
                return 0f;
            var attack = Mathf.Clamp01(time / 0.008f);
            var release = Mathf.Clamp01((duration - time) / 0.03f);
            return Mathf.Sin(2f * Mathf.PI * frequency * time) * attack * release * Mathf.Exp(-13f * time) * 0.15f;
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
