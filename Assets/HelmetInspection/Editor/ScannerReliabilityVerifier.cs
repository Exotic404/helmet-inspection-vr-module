using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HelmetInspection.Editor
{
    /// <summary>Exercises the actual target resolver against all authored defects.
    /// Never saves the deliberately moved helmets or altered training state.</summary>
    public static class ScannerReliabilityVerifier
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const string RuntimeKey = "HelmetInspection.ScannerReliability.RuntimePending";
        static int s_Cases;

        [MenuItem("Helmet Inspection/Verify Scanner Reliability")]
        public static void Verify()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Run the geometry verifier outside Play Mode.");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            try
            {
                s_Cases = 0;
                var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>();
                var session = UnityEngine.Object.FindFirstObjectByType<TrainingSessionController>();
                var hotspots = FindHotspots();
                Require(scanner != null && session != null && hotspots.Length == 10,
                    "Expected scanner, session, and ten authored hotspots.");
                Require(hotspots.Count(item => item.IsHole) == 6 &&
                        hotspots.Select(item => item.DefectIndex).SequenceEqual(Enumerable.Range(0, 10)),
                    "Expected six holes, four surface defects, and ten unique indices.");
                var dataBefore = EditorJsonUtility.ToJson(session.DefectSet);
                var helmet = hotspots[0].GetComponentInParent<HelmetOutOfBoundsRecovery>().transform;
                Require(hotspots.All(item => item.transform.IsChildOf(helmet)), "Targets must move with A2.");
                var startPosition = helmet.position;
                var startRotation = helmet.rotation;
                var startScale = helmet.localScale;
                var rotations = new[] { Quaternion.identity, Quaternion.Euler(0f, 103f, 0f),
                    Quaternion.Euler(63f, -38f, 29f), Quaternion.Euler(180f, 47f, 11f) };
                var shifts = new[] { Vector3.zero, new Vector3(0.6f, 0.2f, -0.4f),
                    new Vector3(-0.7f, 0.7f, 0.3f), new Vector3(0.2f, 0.4f, -0.6f) };
                foreach (var hotspot in hotspots)
                    hotspot.SetInspectionEnabled(true);
                foreach (var scale in new[] { 0.85f, 1f, 1.2f })
                for (var pose = 0; pose < rotations.Length; ++pose)
                {
                    helmet.SetPositionAndRotation(startPosition + shifts[pose], rotations[pose] * startRotation);
                    helmet.localScale = startScale * scale;
                    Physics.SyncTransforms();
                    foreach (var target in hotspots)
                    {
                        foreach (var distance in new[] { 0.012f, 0.045f, 0.15f, 0.35f, 0.60f })
                        foreach (var angle in new[] { -35f, 0f, 35f })
                        foreach (var miss in new[] { -0.003f, 0f, 0.003f })
                        {
                            var outward = Quaternion.AngleAxis(angle, target.transform.up) * target.MarkerNormal;
                            var origin = target.MeasuredCenter + outward * distance;
                            var aim = target.MeasuredCenter + target.transform.right * miss;
                            var direction = (aim - origin).normalized;
                            Require(scanner.TryGetScanTarget(origin, direction, out var acquired) && acquired == target,
                                $"{target.name}, scale {scale}, pose {pose}, range {distance}, angle {angle}, " +
                                $"offset {miss}: selected {(acquired != null ? acquired.name : "nothing")}.");
                        }
                        var wrongSide = target.MeasuredCenter - target.MarkerNormal * 0.25f;
                        Require(!scanner.CanAcquireByBeam(target, wrongSide, target.MarkerNormal, out _, out _),
                            $"{target.name} accepted aim through its back side.");
                        if (target.IsHole)
                            Require(!scanner.CanAcquireByBeam(target,
                                    target.MarkerCenter + target.MarkerNormal * (scanner.HoleBeamRange + 0.1f),
                                    -target.MarkerNormal, out _, out _),
                                $"{target.name} exceeded its close inspection range.");
                    }
                }
                helmet.SetPositionAndRotation(startPosition, startRotation);
                helmet.localScale = startScale;
                foreach (var target in hotspots)
                {
                    Require(!target.TryInspect() && session.FoundCount == 0,
                        "A finding registered before BEGIN.");
                    var origin = target.MeasuredCenter + target.MarkerNormal * 0.3f;
                    SetField(target, "m_Found", true);
                    Require(!scanner.TryGetScanTarget(origin, -target.MarkerNormal, out var found) || found != target,
                        $"Found target {target.name} still competes for acquisition.");
                    Require(!target.TryInspect(), "A found hotspot permitted a duplicate inspection.");
                    target.ResetState();
                    target.SetInspectionEnabled(false);
                    Require(!scanner.TryGetScanTarget(origin, -target.MarkerNormal, out var disabled) || disabled != target,
                        $"Disabled target {target.name} still competes for acquisition.");
                    target.SetInspectionEnabled(true);
                }
                VerifyHandRouting(scanner);
                Require(EditorJsonUtility.ToJson(session.DefectSet) == dataBefore,
                    "The measured defect data changed during verification.");
                Debug.Log($"[ScannerReliability] PASS: {s_Cases} assertions; all ten compete independently " +
                          "across four poses, three scales, five ranges, three angles, and 3 mm aim offsets; " +
                          "close-hole blind zone, back sides, range limits, found/disabled gates, and both hands.");
            }
            finally
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
        }

        static void VerifyHandRouting(InspectionScanner scanner)
        {
            var grab = scanner.GetComponent<XRGrabInteractable>();
            var original = grab.interactorsSelecting.ToArray();
            var inputs = UnityEngine.Object.FindObjectsByType<XRBaseInputInteractor>(FindObjectsInactive.Include);
            var left = inputs.FirstOrDefault(item => item.handedness == InteractorHandedness.Left);
            var right = inputs.FirstOrDefault(item => item.handedness == InteractorHandedness.Right);
            var socket = scanner.HomeMount.GetComponent<XRSocketInteractor>();
            Require(left != null && right != null && socket != null, "Hand interactors or scanner socket missing.");
            // Inject only the selector-list input queried by TryGetControllingHand.
            // No XRI selection callbacks or manager state are modified in Edit Mode.
            try
            {
                grab.interactorsSelecting.Clear();
                Require(!scanner.TryGetControllingHand(out _), "A dropped scanner still reads a hand trigger.");
                grab.interactorsSelecting.Add(socket);
                Require(scanner.TryGetControllingHand(out var docked) && docked == XRNode.LeftHand,
                    "The off-hand scanner socket lost its left trigger.");
                grab.interactorsSelecting.Add(right);
                Require(scanner.TryGetControllingHand(out var swapped) && swapped == XRNode.RightHand,
                    "A right-hand grab must override the off-hand socket trigger.");
                grab.interactorsSelecting.Clear();
                grab.interactorsSelecting.Add(left);
                Require(scanner.TryGetControllingHand(out var heldLeft) && heldLeft == XRNode.LeftHand,
                    "Left-hand scanning lost its trigger route.");
                grab.interactorsSelecting.Clear();
                grab.interactorsSelecting.Add(right);
                Require(scanner.TryGetControllingHand(out var heldRight) && heldRight == XRNode.RightHand,
                    "Right-hand scanning still routes to the left controller.");
            }
            finally
            {
                grab.interactorsSelecting.Clear();
                grab.interactorsSelecting.AddRange(original);
            }
        }

        // Optional batch entry point: omit -quit. It enters Play Mode, runs the real
        // session/trigger lifecycle, exits Play Mode, then exits Unity with a result.
        public static void VerifyRuntime()
        {
            Verify();
            SessionState.SetBool(RuntimeKey, true);
            EditorApplication.update -= RunRuntimeWhenReady;
            EditorApplication.update += RunRuntimeWhenReady;
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void ResumeRuntimeVerification()
        {
            if (SessionState.GetBool(RuntimeKey, false))
            {
                EditorApplication.update -= RunRuntimeWhenReady;
                EditorApplication.update += RunRuntimeWhenReady;
            }
        }

        static void RunRuntimeWhenReady()
        {
            if (!EditorApplication.isPlaying || Time.timeSinceLevelLoad < 0.3f)
                return;
            EditorApplication.update -= RunRuntimeWhenReady;
            var exitCode = 0;
            try
            {
                RunRuntimeChecks();
                Debug.Log("[ScannerReliabilityRuntime] PASS: ten real trigger confirmations, one finding per " +
                          "squeeze, held-trigger retry, release/repress, focus recovery, and reset/restart.");
            }
            catch (Exception error)
            {
                exitCode = 1;
                Debug.LogException(error);
            }
            finally
            {
                SessionState.SetBool(RuntimeKey, false);
                if (Application.isBatchMode)
                    EditorApplication.Exit(exitCode);
                else
                    EditorApplication.ExitPlaymode();
            }
        }

        static void RunRuntimeChecks()
        {
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>();
            var session = UnityEngine.Object.FindFirstObjectByType<TrainingSessionController>();
            var hotspots = FindHotspots();
            var origin = (Transform)typeof(InspectionScanner).GetField("beamOrigin", PrivateInstance).GetValue(scanner);
            var scannerEnabled = scanner.enabled;
            scanner.enabled = false; // The test supplies deterministic trigger samples in one frame.
            try
            {
                session.ResetTraining();
                Aim(hotspots[0]);
                Press(true);
                Require(session.FoundCount == 0, "Trigger registered before BEGIN.");
                Press(false);
                session.BeginTraining();
                // Reproduce a squeeze just before aiming: no hit must not consume the press.
                origin.SetPositionAndRotation(new Vector3(20f, 20f, 20f), Quaternion.identity);
                Press(true);
                Require(session.FoundCount == 0, "An empty-space squeeze registered a finding.");
                SetField(scanner, "m_NextScanTime", 0f);
                Aim(hotspots[0]);
                Press(true);
                Require(session.FoundCount == 1 && hotspots[0].IsFound, "Held-trigger retry did not find D01.");
                Aim(hotspots[1]);
                SetField(scanner, "m_NextScanTime", 0f);
                Press(true);
                Require(session.FoundCount == 1 && !hotspots[1].IsFound,
                    "One held squeeze registered multiple independent defects.");
                for (var i = 1; i < hotspots.Length; ++i)
                {
                    Press(false);
                    Aim(hotspots[i]);
                    Press(true);
                    Require(session.FoundCount == i + 1 && hotspots[i].IsFound,
                        $"A real session trigger did not register {hotspots[i].name}.");
                    Require(!hotspots[i].TryInspect(), "The same finding registered twice.");
                }
                session.ResetTraining();
                Require(session.FoundCount == 0 && !session.IsStarted &&
                        hotspots.All(item => !item.IsFound && !item.CanInspect),
                    "RESTART did not clear all ten findings and close the inspection gate.");
                session.BeginTraining();
                Aim(hotspots[9]);
                Press(true);
                Require(session.FoundCount == 1 && hotspots[9].IsFound,
                    "The scanner could not confirm a finding after RESTART.");
                typeof(InspectionScanner).GetMethod("OnApplicationFocus", PrivateInstance)
                    .Invoke(scanner, new object[] { false });
                Aim(hotspots[8]);
                Press(true);
                Require(session.FoundCount == 2 && hotspots[8].IsFound,
                    "A missed trigger release on focus loss left the scanner latched.");
            }
            finally
            {
                session.ResetTraining();
                scanner.enabled = scannerEnabled;
            }

            void Aim(DefectHotspot target) => origin.SetPositionAndRotation(
                target.MeasuredCenter + target.MarkerNormal * 0.035f,
                Quaternion.LookRotation(-target.MarkerNormal, target.transform.up));
            void Press(bool pressed) => typeof(InspectionScanner).GetMethod("ProcessTrigger", PrivateInstance)
                .Invoke(scanner, new object[] { pressed, XRNode.RightHand });
        }

        static DefectHotspot[] FindHotspots() => UnityEngine.Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include)
            .OrderBy(item => item.DefectIndex).ToArray();
        static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
        static void Require(bool condition, string message)
        {
            ++s_Cases;
            if (!condition)
                throw new InvalidOperationException("[ScannerReliability] " + message);
        }
    }
}
