using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    public static class BulgeScanAccessibilityVerifier
    {
        public static void VerifyInPlayMode()
        {
            Require(Application.isPlaying, "This regression test requires an initialized Play Mode scene.");
            var target = BulgeScanAccessibilityRepair.FindTarget();
            var session = Object.FindFirstObjectByType<TrainingSessionController>();
            var scanner = Object.FindFirstObjectByType<InspectionScanner>();
            var body = target.GetComponentInParent<Rigidbody>();
            var beam = (Transform)new SerializedObject(scanner).FindProperty("beamOrigin").objectReferenceValue;
            var beamPosition = beam.localPosition;
            var beamRotation = beam.localRotation;
            var originalRotation = body.rotation;
            var measuredPosition = target.transform.localPosition;
            var measuredRotation = target.transform.localRotation;
            var radius = target.MarkerRadius;
            var checks = 0;
            try
            {
                Require(!target.IsHole && target.DefectIndex == 8, "D09 remains its independent surface defect.");
                foreach (var other in Object.FindObjectsByType<DefectHotspot>())
                    if (other != target)
                        Require(Vector3.Dot(other.ScannerNormal, other.MarkerNormal) > .9999f,
                            $"{other.name} scanner-facing behavior is unchanged.");

                foreach (var rotation in new[] { originalRotation,
                    Quaternion.Euler(37f, 111f, -23f) * originalRotation,
                    Quaternion.Euler(-52f, -68f, 41f) * originalRotation })
                foreach (var range in new[] { .035f, .3f })
                foreach (var approach in Enumerable.Range(0, 10))
                {
                    session.ResetTraining();
                    session.BeginTraining();
                    body.rotation = rotation;
                    body.transform.rotation = rotation;
                    Physics.SyncTransforms();
                    var normal = target.ScannerNormal;
                    var tangent = Vector3.Cross(normal, target.transform.up).normalized;
                    if (tangent.sqrMagnitude < .5f)
                        tangent = Vector3.Cross(normal, target.transform.right).normalized;
                    var bitangent = Vector3.Cross(normal, tangent).normalized;
                    Vector3 outward;
                    if (approach == 0)
                        outward = normal;
                    else if (approach == 1)
                        // Exact reproduction: +X in the authored helmet's coordinate space.
                        outward = target.transform.parent.TransformDirection(Vector3.right).normalized;
                    else
                    {
                        var bearing = (approach - 2) % 4 * Mathf.PI * .5f;
                        var angle = (approach < 6 ? 55f : 75f) * Mathf.Deg2Rad;
                        outward = normal * Mathf.Cos(angle) +
                            (tangent * Mathf.Cos(bearing) + bitangent * Mathf.Sin(bearing)) * Mathf.Sin(angle);
                    }
                    var origin = target.MeasuredCenter + outward * range;
                    Require(scanner.CanAcquireByBeam(target, origin, -outward, out _, out _),
                        $"D09 accepts exterior approach {approach} at {range:F3} m with a rotated helmet.");
                    Require(scanner.TryGetScanTarget(origin, -outward, out var acquired) && acquired == target,
                        "D09 wins deliberate aim with all twelve real hotspots active; a neighbor cannot steal it.");
                    beam.SetPositionAndRotation(origin, Quaternion.LookRotation(-outward, tangent));
                    scanner.Scan();
                    Require(target.IsFound && session.FoundCount == 1,
                        "The scanner confirms the independently counted D09 finding.");
                    ++checks;
                }

                session.ResetTraining();
                session.BeginTraining();
                var front = target.ScannerNormal;
                var center = target.MeasuredCenter;
                Require(!scanner.CanAcquireByBeam(target, center - front * .3f, front, out _, out _),
                    "D09 still rejects scanning through the helmet from the opposite side.");
                Require(!scanner.CanAcquireByBeam(target, center + front * 2.6f, -front, out _, out _),
                    "D09 still respects the original scanner range.");
                var side = Vector3.Cross(front, target.transform.up).normalized;
                Require(!scanner.CanAcquireByBeam(target, center + front * .3f + side * .06f,
                    -front, out _, out _), "D09 does not gain a larger aim radius or accept an off-target beam.");
                Require(target.transform.localPosition == measuredPosition &&
                        target.transform.localRotation == measuredRotation && Mathf.Approximately(radius, target.MarkerRadius),
                    "D09 measured location, halo orientation and size remain unchanged.");
                Debug.Log($"[BulgeScan] PASS: {checks} exterior-angle/range/rotation scan cases, " +
                          "all-neighbor target selection, opposite-side/range/miss rejection and geometry preservation.");
            }
            finally
            {
                session.ResetTraining();
                beam.SetLocalPositionAndRotation(beamPosition, beamRotation);
            }
        }

        static void Require(bool passed, string message)
        {
            if (!passed)
                throw new InvalidOperationException("[BulgeScan] FAIL: " + message);
        }
    }
}
