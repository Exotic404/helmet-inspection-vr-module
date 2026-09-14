using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Keeps the measured hole records on the mesh, but places their invisible scanner
    /// acquisition points beyond the helmet's solid collision proxy.
    /// </summary>
    public static class ExteriorHoleAccessibilityInstaller
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const float MinimumOffset = 0.025f;
        const float ExteriorClearance = 0.025f;
        const float MaximumSearchOffset = 0.18f;
        const float SearchStep = 0.005f;
        const float ScanPadding = 0.018f;

        [MenuItem("Helmet Inspection/Install Exterior Hole Accessibility")]
        public static void Install()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var hotspots = UnityEngine.Object.FindObjectsByType<DefectHotspot>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var holes = hotspots.Where(item => item.IsHole).OrderBy(item => item.DefectIndex).ToArray();
            if (holes.Length != 6)
                throw new InvalidOperationException($"Expected 6 authored hole defects, found {holes.Length}.");

            foreach (var hotspot in hotspots)
            {
                if (!hotspot.IsHole)
                {
                    hotspot.SetEditorHoleAccessibility(0f, 0f);
                    EditorUtility.SetDirty(hotspot);
                    continue;
                }

                var helmet = hotspot.GetComponentInParent<HelmetOutOfBoundsRecovery>(true);
                if (helmet == null)
                    throw new InvalidOperationException($"{hotspot.name} is not parented to a movable helmet.");
                var solidColliders = helmet.GetComponentsInChildren<Collider>(true)
                    .Where(item => item != null && !item.isTrigger && !item.transform.IsChildOf(hotspot.transform))
                    .ToArray();
                if (solidColliders.Length == 0)
                    throw new InvalidOperationException($"{helmet.name} has no solid collision proxy.");

                var offset = FindExteriorOffset(hotspot, solidColliders);
                hotspot.SetEditorHoleAccessibility(offset, ScanPadding);
                EditorUtility.SetDirty(hotspot);
            }

            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanner == null)
                throw new InvalidOperationException("The inspection scanner is missing.");
            scanner.SetEditorHoleInteraction(0.65f, true);
            EditorUtility.SetDirty(scanner);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Failed to save exterior hole accessibility into the training scene.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            foreach (var hole in holes)
                Debug.Log($"[HoleAccessibility] {hole.name}: measured {hole.MeasuredCenter:F3}, " +
                          $"exterior target {hole.MarkerCenter:F3}, offset {hole.HoleExteriorOffset:0.000} m.");
            Debug.Log("[HoleAccessibility] Installed six exterior-facing hole targets, generous scan padding, " +
                      "short-range beam confirmation, and scanner-only helmet collision exclusions.");
        }

        static float FindExteriorOffset(DefectHotspot hotspot, Collider[] solidColliders)
        {
            for (var offset = MinimumOffset; offset <= MaximumSearchOffset; offset += SearchStep)
            {
                var candidate = hotspot.MeasuredCenter + hotspot.MarkerNormal * offset;
                if (solidColliders.Any(collider => IsInside(collider, candidate)))
                    continue;

                var clearedOffset = offset + ExteriorClearance;
                var clearedCandidate = hotspot.MeasuredCenter + hotspot.MarkerNormal * clearedOffset;
                if (solidColliders.All(collider => !IsInside(collider, clearedCandidate)))
                    return clearedOffset;
            }

            throw new InvalidOperationException($"Could not project {hotspot.name} outside the helmet collider. " +
                                                "Check that its authored local normal points away from the shell.");
        }

        static bool IsInside(Collider collider, Vector3 point) =>
            Vector3.SqrMagnitude(collider.ClosestPoint(point) - point) < 0.00000001f;
    }
}
