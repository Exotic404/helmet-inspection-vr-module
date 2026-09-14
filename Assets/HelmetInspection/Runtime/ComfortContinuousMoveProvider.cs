using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;

namespace HelmetInspection
{
    /// <summary>
    /// Head-relative XRI movement with a short acceleration/deceleration ramp.
    /// Collision and gravity remain owned by XRI's CharacterController pipeline.
    /// </summary>
    public sealed class ComfortContinuousMoveProvider : ContinuousMoveProvider
    {
        [SerializeField, Min(0.02f)] float accelerationTime = 0.12f;
        [SerializeField, Min(0.02f)] float decelerationTime = 0.10f;

        Vector2 m_SmoothedInput;
        Vector2 m_SmoothVelocity;

        public float CurrentInputMagnitude { get; private set; }

        protected override Vector3 ComputeDesiredMove(Vector2 input)
        {
            var smoothTime = input.sqrMagnitude > m_SmoothedInput.sqrMagnitude
                ? accelerationTime
                : decelerationTime;
            m_SmoothedInput = Vector2.SmoothDamp(m_SmoothedInput, input, ref m_SmoothVelocity,
                smoothTime, Mathf.Infinity, Time.deltaTime);
            if (m_SmoothedInput.sqrMagnitude < 0.0001f && input.sqrMagnitude < 0.0001f)
                m_SmoothedInput = Vector2.zero;
            CurrentInputMagnitude = Mathf.Clamp01(m_SmoothedInput.magnitude);
            return base.ComputeDesiredMove(m_SmoothedInput);
        }
    }
}
