using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HelmetInspection
{
    [RequireComponent(typeof(XRGrabInteractable))]
    public sealed class InspectionScanner : MonoBehaviour
    {
        [SerializeField] Transform beamOrigin;
        [SerializeField] LineRenderer beam;
        [SerializeField] float scanDistance = 2.5f;
        [SerializeField] XRNode defaultHand = XRNode.LeftHand;
        [SerializeField] LayerMask scanMask = ~0;
        [SerializeField] Renderer emitterRenderer;
        [SerializeField] Light emitterLight;
        [SerializeField, Min(0.05f)] float proximityRange = 0.15f;
        [SerializeField, Min(0.005f)] float minimumAimRadius = 0.032f;
        [SerializeField, Min(0f)] float toolTipRadius = 0.012f;
        [SerializeField, Min(0.1f)] float holeBeamRange = 0.65f;
        [SerializeField] bool ignoreHelmetCollisions = true;
        [SerializeField] Color emitterColor = new Color(0.3f, 0.94f, 1f, 1f);
        [SerializeField] Transform homeMount;

        readonly List<InputDevice> m_Devices = new List<InputDevice>();
        readonly RaycastHit[] m_BeamHits = new RaycastHit[64];
        XRGrabInteractable m_Grab;
        DefectHotspot[] m_Hotspots;
        MaterialPropertyBlock m_EmitterBlock;
        DefectHotspot m_LastNearby;
        bool m_TriggerHeld;
        bool m_ScannedForPress;
        XRNode m_TriggerHand;
        float m_NextScanTime;
        Rigidbody m_Body;

        public Transform HomeMount => homeMount;
        public float HoleBeamRange => holeBeamRange;
        public bool IgnoresHelmetCollisions => ignoreHelmetCollisions;

        void Awake()
        {
            m_Grab = GetComponent<XRGrabInteractable>();
            m_Body = GetComponent<Rigidbody>();
            m_Hotspots = FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            m_EmitterBlock = new MaterialPropertyBlock();
            m_Grab.activated.AddListener(OnActivated);
            ConfigureHelmetCollisionExclusions();
            if (beam != null)
                beam.positionCount = 2;
        }

        void OnDestroy()
        {
            if (m_Grab != null)
                m_Grab.activated.RemoveListener(OnActivated);
        }

        void Update()
        {
            UpdateBeam();
            UpdateProximityFeedback();
            if (TryGetControllingHand(out var hand))
                ProcessTrigger(ReadTrigger(hand), hand);
            else
                ResetTriggerState();
        }

        // The off-hand socket has no activation action of its own. Read its hand's
        // device, or the hand that actually grabbed the scanner after a hand swap.
        public bool TryGetControllingHand(out XRNode hand)
        {
            hand = defaultHand;
            if (m_Grab == null)
                m_Grab = GetComponent<XRGrabInteractable>();
            if (m_Grab == null)
                return false;
            foreach (var interactor in m_Grab.interactorsSelecting)
            {
                if (interactor is not XRBaseInputInteractor)
                    continue;
                if (interactor.handedness == InteractorHandedness.Left)
                    hand = XRNode.LeftHand;
                else if (interactor.handedness == InteractorHandedness.Right)
                    hand = XRNode.RightHand;
                else
                    continue;
                return true;
            }
            foreach (var interactor in m_Grab.interactorsSelecting)
                if (interactor is XRSocketInteractor && homeMount != null &&
                    (interactor.transform == homeMount || homeMount.IsChildOf(interactor.transform)))
                    return true;
            return false;
        }

        bool ReadTrigger(XRNode hand)
        {
            foreach (var interactor in m_Grab.interactorsSelecting)
                if (interactor is XRBaseInputInteractor input &&
                    input.activateInput.ReadIsPerformed())
                    return true;
            InputDevices.GetDevicesAtXRNode(hand, m_Devices);
            foreach (var device in m_Devices)
            {
                if (device.TryGetFeatureValue(CommonUsages.trigger, out var amount))
                {
                    if (amount >= (m_TriggerHeld && m_TriggerHand == hand ? 0.25f : 0.65f))
                        return true;
                }
                else if (device.TryGetFeatureValue(CommonUsages.triggerButton, out var pressed) && pressed)
                    return true;
            }
            return false;
        }

        void ProcessTrigger(bool pressed, XRNode hand)
        {
            if (!pressed || (m_TriggerHeld && hand != m_TriggerHand))
                ResetTriggerState();
            if (!pressed)
                return;
            m_TriggerHeld = true;
            m_TriggerHand = hand;
            // A squeeze just before the beam settles must still work. Retry while
            // held until one finding succeeds, then require release for the next.
            if (!m_ScannedForPress)
                m_ScannedForPress = TryScan();
        }

        void ResetTriggerState()
        {
            m_TriggerHeld = false;
            m_ScannedForPress = false;
            m_NextScanTime = 0f;
        }

        void OnDisable() => ResetTriggerState();
        void OnApplicationFocus(bool hasFocus) { if (!hasFocus) ResetTriggerState(); }
        void OnApplicationPause(bool paused) { if (paused) ResetTriggerState(); }

        void UpdateProximityFeedback()
        {
            if (beamOrigin == null)
                return;

            DefectHotspot nearest = null;
            var nearestDistance = float.PositiveInfinity;
            foreach (var hotspot in m_Hotspots)
            {
                if (hotspot == null || !hotspot.CanInspect)
                    continue;
                var distance = Vector3.Distance(beamOrigin.position, hotspot.MarkerCenter);
                var proximity = 1f - Mathf.InverseLerp(hotspot.InspectionRadius, proximityRange, distance);
                hotspot.SetProximity(proximity);
                if (proximity > 0f && distance < nearestDistance)
                {
                    nearest = hotspot;
                    nearestDistance = distance;
                }
            }

            var warmth = nearest != null
                ? 1f - Mathf.InverseLerp(nearest.InspectionRadius, proximityRange, nearestDistance)
                : 0f;
            if (TryGetScanTarget(beamOrigin.position, beamOrigin.forward, out var aimed))
            {
                nearest = aimed;
                warmth = Mathf.Max(warmth, 0.7f);
            }
            var idlePulse = 0.25f + 0.10f * (Mathf.Sin(Time.unscaledTime * 2.4f) * 0.5f + 0.5f);
            var glow = Mathf.Lerp(idlePulse, 2.2f, warmth);
            if (emitterRenderer != null)
            {
                emitterRenderer.GetPropertyBlock(m_EmitterBlock);
                m_EmitterBlock.SetColor("_BaseColor", emitterColor);
                m_EmitterBlock.SetColor("_EmissionColor", emitterColor * glow);
                emitterRenderer.SetPropertyBlock(m_EmitterBlock);
            }
            if (emitterLight != null)
            {
                emitterLight.enabled = warmth > 0.02f;
                emitterLight.intensity = Mathf.Lerp(0f, 1.35f, warmth);
            }

            if (nearest != null && nearest != m_LastNearby)
                SendProximityTick();
            m_LastNearby = nearest;
        }

        void SendProximityTick()
        {
            foreach (var interactor in m_Grab.interactorsSelecting)
            {
                if (interactor is XRBaseInputInteractor inputInteractor &&
                    inputInteractor.SendHapticImpulse(0.12f, 0.035f))
                    return;
            }
            if (!TryGetControllingHand(out var hand))
                return;
            InputDevices.GetDevicesAtXRNode(hand, m_Devices);
            foreach (var device in m_Devices)
                if (device.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
                    device.SendHapticImpulse(0u, 0.12f, 0.035f);
        }

        void OnActivated(ActivateEventArgs args)
        {
            if (TryGetControllingHand(out var hand) &&
                args.interactorObject is IXRSelectInteractor selector && m_Grab.interactorsSelecting.Contains(selector))
                ProcessTrigger(true, hand);
        }

        void ConfigureHelmetCollisionExclusions()
        {
            if (!ignoreHelmetCollisions)
                return;

            var scannerColliders = GetComponentsInChildren<Collider>(true);
            var helmets = FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var helmet in helmets)
            {
                if (helmet == null)
                    continue;
                var helmetColliders = helmet.GetComponentsInChildren<Collider>(true);
                foreach (var scannerCollider in scannerColliders)
                foreach (var helmetCollider in helmetColliders)
                    if (scannerCollider != null && helmetCollider != null &&
                        !scannerCollider.isTrigger && !helmetCollider.isTrigger)
                        Physics.IgnoreCollision(scannerCollider, helmetCollider, true);
            }
        }

        void UpdateBeam()
        {
            if (beam == null || beamOrigin == null)
                return;
            var end = beamOrigin.position + beamOrigin.forward * scanDistance;
            if (TryGetScanTarget(beamOrigin.position, beamOrigin.forward, out var aimed))
                end = aimed.MeasuredCenter;
            else
            {
                var count = Physics.RaycastNonAlloc(beamOrigin.position, beamOrigin.forward, m_BeamHits,
                    scanDistance, scanMask, QueryTriggerInteraction.Ignore);
                var nearest = scanDistance;
                for (var i = 0; i < count; ++i)
                    if (!m_BeamHits[i].collider.transform.IsChildOf(transform) && m_BeamHits[i].distance < nearest)
                    {
                        nearest = m_BeamHits[i].distance;
                        end = m_BeamHits[i].point;
                    }
            }
            beam.SetPosition(0, beamOrigin.position);
            beam.SetPosition(1, end);
        }

        public void Scan()
        {
            TryScan();
        }

        bool TryScan()
        {
            if (beamOrigin == null || Time.unscaledTime < m_NextScanTime)
                return false;
            m_NextScanTime = Time.unscaledTime + 0.18f;
            if (!TryGetScanTarget(beamOrigin.position, beamOrigin.forward, out var target))
                return false;
            var confirmed = target.TryInspect();
            Debug.Log($"[Scanner] {(target.IsHole ? "Exterior hole" : "Surface")} scan " +
                      $"{(confirmed ? "confirmed" : "rejected")} {target.name}.");
            return confirmed;
        }

        /// <summary>Shared acquisition for beam feedback and confirmation. Uses live
        /// transforms, excludes found/disabled targets, and resolves all ten together.</summary>
        public bool TryGetScanTarget(Vector3 origin, Vector3 direction, out DefectHotspot target)
        {
            target = null;
            if (m_Hotspots == null)
                m_Hotspots = FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            DefectHotspot aimedHotspot = null;
            var nearestAlongBeam = float.PositiveInfinity;
            var bestAimScore = float.PositiveInfinity;
            foreach (var hotspot in m_Hotspots)
            {
                if (hotspot == null || !hotspot.CanInspect)
                    continue;

                if (!CanAcquireByBeam(hotspot, origin, direction, out var alongBeam, out var aimScore))
                    continue;
                if (aimScore < bestAimScore - 0.00001f ||
                    (Mathf.Abs(aimScore - bestAimScore) <= 0.00001f &&
                     (alongBeam < nearestAlongBeam - 0.00001f ||
                      (Mathf.Abs(alongBeam - nearestAlongBeam) <= 0.00001f &&
                       (aimedHotspot == null || hotspot.DefectIndex < aimedHotspot.DefectIndex)))))
                {
                    aimedHotspot = hotspot;
                    nearestAlongBeam = alongBeam;
                    bestAimScore = aimScore;
                }
            }

            if (aimedHotspot != null)
            {
                target = aimedHotspot;
                return true;
            }
            // Contact remains available at hole openings. Give deliberate beam aim
            // priority so touching a neighboring hole cannot steal a dent scan.
            var nearestTip = float.PositiveInfinity;
            foreach (var hotspot in m_Hotspots)
            {
                if (hotspot == null || !hotspot.CanInspect || !hotspot.IsHole ||
                    Vector3.Dot(origin - hotspot.MeasuredCenter, hotspot.MarkerNormal) < -toolTipRadius)
                    continue;
                var distance = Mathf.Min(Vector3.Distance(origin, hotspot.MeasuredCenter),
                    Vector3.Distance(origin, hotspot.MarkerCenter));
                if (distance <= hotspot.InspectionRadius + toolTipRadius && distance < nearestTip)
                {
                    target = hotspot;
                    nearestTip = distance;
                }
            }
            return target != null;
        }

        /// <summary>
        /// Evaluates scanner aim without a physics raycast, allowing an exterior-facing
        /// hole to be confirmed even though the helmet's solid collision proxy blocks rays.
        /// </summary>
        public bool CanAcquireByBeam(DefectHotspot hotspot, Vector3 origin, Vector3 direction,
            out float alongBeam, out float aimScore)
        {
            alongBeam = float.PositiveInfinity;
            aimScore = float.PositiveInfinity;
            if (hotspot == null || direction.sqrMagnitude < 0.5f)
                return false;
            direction.Normalize();

            var maximumRange = hotspot.IsHole ? Mathf.Min(scanDistance, holeBeamRange) : scanDistance;
            if (Vector3.Dot(direction, -hotspot.MarkerNormal) < 0.1f)
                return false;
            var aimRadius = Mathf.Max(minimumAimRadius, hotspot.InspectionRadius);
            var bestScore = float.PositiveInfinity;
            var bestAlong = float.PositiveInfinity;
            // Evaluate the actual opening first. The exterior proxy may be BEHIND
            // the tip when the tool is close; that must not reject a visible hole.
            ScorePoint(hotspot.MeasuredCenter, 0f);
            if (hotspot.IsHole)
                ScorePoint(hotspot.MarkerCenter, 0.12f);
            aimScore = bestScore;
            alongBeam = bestAlong;
            return aimScore <= 1f;

            void ScorePoint(Vector3 point, float exteriorPenalty)
            {
                var along = Vector3.Dot(point - origin, direction);
                if (along < 0f || along > maximumRange)
                    return;
                var score = Vector3.Distance(origin + direction * along, point) / aimRadius + exteriorPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestAlong = along;
                }
            }
        }

        public void ResetPlacement()
        {
            ResetTriggerState();
            if (homeMount == null)
                return;
            if (m_Grab == null)
                m_Grab = GetComponent<XRGrabInteractable>();
            if (m_Body == null)
                m_Body = GetComponent<Rigidbody>();

            if (m_Grab != null && m_Grab.isSelected && m_Grab.interactionManager != null)
                m_Grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)m_Grab);

            if (m_Body != null)
                m_Body.isKinematic = true;
            transform.SetPositionAndRotation(homeMount.position, homeMount.rotation);
            if (m_Body != null)
            {
                m_Body.position = homeMount.position;
                m_Body.rotation = homeMount.rotation;
            }
            Physics.SyncTransforms();
            if (m_Body != null)
            {
                m_Body.isKinematic = false;
                m_Body.linearVelocity = Vector3.zero;
                m_Body.angularVelocity = Vector3.zero;
                m_Body.Sleep();
            }
        }

#if UNITY_EDITOR
        public void SetEditorReferences(Transform origin, LineRenderer line, XRNode hand,
            Renderer emitter, Light proximityLight, Transform resetHome = null)
        {
            beamOrigin = origin;
            beam = line;
            defaultHand = hand;
            emitterRenderer = emitter;
            emitterLight = proximityLight;
            homeMount = resetHome;
        }

        public void SetEditorHome(Transform resetHome) => homeMount = resetHome;

        public void SetEditorHoleInteraction(float maximumBeamRange, bool excludePhysicalCollisions)
        {
            holeBeamRange = Mathf.Max(0.1f, maximumBeamRange);
            ignoreHelmetCollisions = excludePhysicalCollisions;
        }
#endif
    }
}
