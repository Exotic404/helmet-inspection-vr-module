using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace HelmetInspection
{
    [RequireComponent(typeof(SphereCollider))]
    [RequireComponent(typeof(XRSimpleInteractable))]
    public sealed class DefectHotspot : MonoBehaviour
    {
        [SerializeField] int defectIndex;
        [SerializeField] TrainingSessionController session;
        [SerializeField] Renderer haloRenderer;
        [SerializeField] bool isHole;
        [SerializeField, Min(0f)] float holeExteriorOffset = 0.04f;
        [SerializeField, Min(0f)] float holeScanPadding = 0.018f;
        [SerializeField, HideInInspector] bool showEditorGizmo;
        [SerializeField] Color idleColor = new Color(0.12f, 0.78f, 0.92f, 0f);
        [SerializeField] Color foundColor = new Color(0.22f, 1f, 0.48f, 0.92f);

        XRSimpleInteractable m_Interactable;
        SphereCollider m_Collider;
        MaterialPropertyBlock m_Block;
        bool m_Found;
        bool m_InspectionEnabled;
        float m_Proximity;

        public int DefectIndex => defectIndex;
        public bool IsFound => m_Found;
        public bool IsHole => isHole;
        public bool CanInspect => isActiveAndEnabled && m_InspectionEnabled && !m_Found;
        public float HoleExteriorOffset => isHole ? holeExteriorOffset : 0f;
        public float HoleScanPadding => isHole ? holeScanPadding : 0f;
        public float InspectionRadius => MarkerRadius + (isHole ? holeScanPadding : 0f);
        public Vector3 MarkerNormal => transform.forward.normalized;
        public float MarkerRadius
        {
            get
            {
                if (m_Collider == null)
                    m_Collider = GetComponent<SphereCollider>();
                if (m_Collider == null)
                    return 0f;
                var scale = transform.lossyScale;
                return m_Collider.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            }
        }
        /// <summary>
        /// The authoritative center on the imported helmet mesh. This remains unchanged
        /// even when a hole receives an easier exterior scan target.
        /// </summary>
        public Vector3 MeasuredCenter
        {
            get
            {
                if (m_Collider == null)
                    m_Collider = GetComponent<SphereCollider>();
                return m_Collider != null ? transform.TransformPoint(m_Collider.center) : transform.position;
            }
        }

        /// <summary>
        /// World-space acquisition point used by the scanner. A hole is absent geometry,
        /// so its target is projected outside the shell along the measured surface normal.
        /// The measured transform and trigger collider stay at the real opening.
        /// </summary>
        public Vector3 MarkerCenter => MeasuredCenter + MarkerNormal * HoleExteriorOffset;

        void Awake()
        {
            m_Interactable = GetComponent<XRSimpleInteractable>();
            m_Collider = GetComponent<SphereCollider>();
            m_Block = new MaterialPropertyBlock();
            m_Interactable.selectEntered.AddListener(OnSelected);
            ApplyColor(idleColor);
            UpdateVisibility();
        }

        void OnDestroy()
        {
            if (m_Interactable != null)
                m_Interactable.selectEntered.RemoveListener(OnSelected);
        }

        void OnSelected(SelectEnterEventArgs _)
        {
            // Hole findings use the scanner's exterior acquisition and contact paths.
            // A bare controller ray must not select missing geometry through the shell.
            if (!isHole)
                TryInspect();
        }

        public bool TryInspect()
        {
            if (!CanInspect)
                return false;
            if (session == null)
                session = FindFirstObjectByType<TrainingSessionController>(FindObjectsInactive.Include);
            return session != null && session.RegisterDefect(defectIndex, this);
        }

        public void MarkFound()
        {
            m_Found = true;
            m_Proximity = 0f;
            ApplyColor(foundColor);
            UpdateVisibility();
            EmitConfirmationBurst();
        }

        public void ResetState()
        {
            m_Found = false;
            m_Proximity = 0f;
            ApplyColor(idleColor);
            UpdateVisibility();
        }

        public void SetInspectionEnabled(bool value)
        {
            if (m_Collider == null)
                m_Collider = GetComponent<SphereCollider>();
            m_InspectionEnabled = value;
            m_Collider.enabled = value;
            if (!value)
                m_Proximity = 0f;
            UpdateVisibility();
        }

        public void SetProximity(float amount)
        {
            m_Proximity = m_InspectionEnabled && !m_Found ? Mathf.Clamp01(amount) : 0f;
            UpdateVisibility();
        }

        void UpdateVisibility()
        {
            if (haloRenderer != null)
                // Defect locations must not be revealed before the trainee finds them.
                // Proximity feedback stays on the scanner light/haptics; the ring appears
                // only after confirmation and is colored green by MarkFound().
                haloRenderer.enabled = m_Found;
        }

        void ApplyColor(Color color)
        {
            if (haloRenderer == null)
                return;
            if (m_Block == null)
                m_Block = new MaterialPropertyBlock();
            haloRenderer.GetPropertyBlock(m_Block);
            m_Block.SetColor("_BaseColor", color);
            m_Block.SetColor("_EmissionColor", color * 2.3f);
            haloRenderer.SetPropertyBlock(m_Block);
        }

        void EmitConfirmationBurst()
        {
            var effect = new GameObject("Confirmed Defect Burst");
            effect.transform.SetPositionAndRotation(transform.position, Quaternion.LookRotation(transform.up));
            var particles = effect.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.loop = false;
            main.duration = 0.35f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.12f, 0.32f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.004f, 0.012f);
            main.startColor = foundColor;
            main.maxParticles = 24;
            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.015f;
            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (particleShader != null)
            {
                var particleMaterial = new Material(particleShader) { name = "Runtime Confirmation Particle" };
                if (particleMaterial.HasProperty("_BaseColor"))
                    particleMaterial.SetColor("_BaseColor", foundColor);
                renderer.material = particleMaterial;
            }
            particles.Play();
            Destroy(effect, 1.2f);
        }

#if UNITY_EDITOR
        public void SetEditorReferences(int index, TrainingSessionController owner, Renderer halo, bool hole = false)
        {
            defectIndex = index;
            session = owner;
            haloRenderer = halo;
            isHole = hole;
            holeExteriorOffset = hole ? Mathf.Max(holeExteriorOffset, 0.04f) : 0f;
            holeScanPadding = hole ? Mathf.Max(holeScanPadding, 0.018f) : 0f;
        }

        public void SetEditorHoleAccessibility(float exteriorOffset, float scanPadding)
        {
            holeExteriorOffset = isHole ? Mathf.Max(0f, exteriorOffset) : 0f;
            holeScanPadding = isHole ? Mathf.Max(0f, scanPadding) : 0f;
        }

        public void SetEditorGizmoVisible(bool value)
        {
            showEditorGizmo = value;
        }

        void OnDrawGizmos()
        {
            if (!showEditorGizmo)
                return;
            var previous = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = isHole ? new Color(1f, 0.35f, 0.12f, 0.9f) : new Color(0.1f, 0.9f, 1f, 0.9f);
            Gizmos.DrawWireSphere(MarkerCenter, InspectionRadius);
            if (isHole)
                Gizmos.DrawLine(MeasuredCenter, MarkerCenter);
            Gizmos.matrix = previous;
        }
#endif
    }
}
