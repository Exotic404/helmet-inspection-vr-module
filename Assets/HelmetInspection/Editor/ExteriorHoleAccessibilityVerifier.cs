using System;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelmetInspection.Editor
{
    public static class ExteriorHoleAccessibilityVerifier
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";

        public static void Verify()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            var holes = UnityEngine.Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include)
                .Where(item => item.IsHole).OrderBy(item => item.DefectIndex).ToArray();
            if (scanner == null || holes.Length != 6)
                throw new InvalidOperationException("Scanner or complete six-hole set is missing.");

            foreach (var intended in holes)
            {
                var origin = intended.MarkerCenter + intended.MarkerNormal * 0.35f;
                var direction = (intended.MeasuredCenter - origin).normalized;
                var selected = holes
                    .Select(item => new
                    {
                        hotspot = item,
                        result = scanner.CanAcquireByBeam(item, origin, direction,
                            out var along, out var score),
                        along,
                        score
                    })
                    .Where(item => item.result)
                    .OrderBy(item => item.score)
                    .ThenBy(item => item.along)
                    .Select(item => item.hotspot)
                    .FirstOrDefault();
                if (selected != intended)
                    throw new InvalidOperationException($"Aiming at {intended.name} selected " +
                                                        $"{(selected != null ? selected.name : "nothing")}.");

                var wrongSideOrigin = intended.MeasuredCenter - intended.MarkerNormal * 0.25f;
                var wrongSideDirection = (intended.MeasuredCenter - wrongSideOrigin).normalized;
                if (scanner.CanAcquireByBeam(intended, wrongSideOrigin, wrongSideDirection, out _, out _))
                    throw new InvalidOperationException($"{intended.name} can be acquired through the back of the shell.");
            }

            Debug.Log("[HoleAccessibilityVerification] PASS: all six holes independently select from an " +
                      "outside approach aimed at the visible opening, and all reject back-side aim.");
        }
    }
}
