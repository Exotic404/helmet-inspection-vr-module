using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Preserves manually positioned hotspot transforms while rebuilding their data/index wiring.
    /// This deliberately does not invoke the procedural scene generator.
    /// </summary>
    public static class ManualHotspotSynchronizer
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const string DefectSetPath = "Assets/HelmetInspection/Data/DefectSet_A2.asset";

        [MenuItem("Helmet Inspection/Synchronize Manually Authored Hotspots")]
        public static void Synchronize()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var session = UnityEngine.Object.FindFirstObjectByType<TrainingSessionController>(FindObjectsInactive.Include);
            var defectSet = AssetDatabase.LoadAssetAtPath<DefectSet>(DefectSetPath);
            if (session == null || defectSet == null)
                throw new InvalidOperationException("Training session or DefectSet_A2 is missing.");

            var hotspots = UnityEngine.Object.FindObjectsByType<DefectHotspot>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(item => NumberFromName(item.name))
                .ThenBy(item => item.name, StringComparer.Ordinal)
                .ToArray();
            if (hotspots.Length == 0)
                throw new InvalidOperationException("No authored defect hotspots were found.");
            if (hotspots.Select(item => NumberFromName(item.name)).Distinct().Count() != hotspots.Length)
                throw new InvalidOperationException("Every hotspot must have a unique A2-Dxx number in its GameObject name.");

            var existing = defectSet.Defects.ToArray();
            var records = new List<DefectRecord>(hotspots.Length);
            for (var i = 0; i < hotspots.Length; ++i)
            {
                var hotspot = hotspots[i];
                var title = TitleFromName(hotspot.name);
                var id = $"A2-D{NumberFromName(hotspot.name):00}";
                var exact = existing.FirstOrDefault(item => string.Equals(item.id, id, StringComparison.Ordinal));
                var sphere = hotspot.GetComponent<SphereCollider>();
                if (sphere == null)
                    throw new InvalidOperationException($"{hotspot.name} is missing its SphereCollider.");
                sphere.isTrigger = true;

                var serializedHotspot = new SerializedObject(hotspot);
                serializedHotspot.FindProperty("defectIndex").intValue = i;
                serializedHotspot.FindProperty("session").objectReferenceValue = session;
                // Duplicated hotspots retain their authored decal, hole type and accessibility
                // settings. A title is not an identity (two holes can have the same title).
                var ownHalo = serializedHotspot.FindProperty("haloRenderer").objectReferenceValue as Renderer;
                if (ownHalo == null || !ownHalo.transform.IsChildOf(hotspot.transform))
                    ownHalo = hotspot.GetComponentsInChildren<MeshRenderer>(true)
                        .FirstOrDefault(item => item.name.IndexOf("Halo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                item.name.IndexOf("Rim", StringComparison.OrdinalIgnoreCase) >= 0);
                if (ownHalo == null)
                    throw new InvalidOperationException($"{hotspot.name} is missing its own halo renderer.");
                serializedHotspot.FindProperty("haloRenderer").objectReferenceValue = ownHalo;
                var isHole = serializedHotspot.FindProperty("isHole").boolValue;
                serializedHotspot.ApplyModifiedPropertiesWithoutUndo();
                // A cloned hole moved to the opposite side can retain its old inward
                // direction. Only repair unmeasured manual additions with invalid facing;
                // measured defects and deliberately outward authored normals stay intact.
                if (isHole && (exact == null || exact.sourceVertex < 0))
                    RepairInwardManualHoleFacing(hotspot);
                ownHalo.enabled = false;
                EditorUtility.SetDirty(sphere);
                EditorUtility.SetDirty(ownHalo);

                var normal = (hotspot.transform.localRotation * Vector3.forward).normalized;
                records.Add(new DefectRecord
                {
                    id = id,
                    title = title,
                    category = isHole ? DefectCategory.MissingGeometryHole :
                        exact != null && !exact.IsHole ? exact.category : DefectCategory.LocalDeformation,
                    severity = exact?.severity ?? DefectSeverity.Advisory,
                    localPosition = hotspot.transform.localPosition,
                    localNormal = normal.sqrMagnitude > 0.5f ? normal : Vector3.up,
                    markerRadius = sphere.radius,
                    deviationMillimeters = exact?.deviationMillimeters ?? 0f,
                    inspectionNote = exact?.inspectionNote ??
                        "Manually authored A2 inspection finding. Compare this location against the reference helmet; no measured deviation has been assigned.",
                    correctiveAction = exact?.correctiveAction ??
                        "Place the helmet on quality hold and document this independent finding before release.",
                    sourceVertex = exact?.sourceVertex ?? -1,
                    sourceClusterSize = exact?.sourceClusterSize ?? 0
                });
                EditorUtility.SetDirty(hotspot);
            }

            defectSet.SetEditorData(defectSet.CandidateThresholdMillimeters, records);
            EditorUtility.SetDirty(defectSet);

            var serializedSession = new SerializedObject(session);
            var list = serializedSession.FindProperty("hotspots");
            list.arraySize = hotspots.Length;
            for (var i = 0; i < hotspots.Length; ++i)
                list.GetArrayElementAtIndex(i).objectReferenceValue = hotspots[i];
            serializedSession.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(session);

            foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text.name == "Intro Body")
                    text.text = Regex.Replace(text.text, @"Log (?:all \d+|any \d+ of \d+) findings",
                        $"Log any {session.TargetCount} of {hotspots.Length} findings");
                else if (text.name == "Progress")
                    text.text = $"QA FINDINGS  00 / {session.TargetCount:00}";
                EditorUtility.SetDirty(text);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Failed to save the synchronized training scene.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var uniqueIndices = hotspots.Select(item => item.DefectIndex).Distinct().Count();
            if (uniqueIndices != hotspots.Length || defectSet.Defects.Count != hotspots.Length)
                throw new InvalidOperationException("Hotspot synchronization did not produce a one-to-one scene/data mapping.");
            Debug.Log($"[HotspotSync] Synchronized {hotspots.Length} independent defects without changing " +
                      "authored positions, sizes, button hitboxes, or the environment.");
        }

        static void RepairInwardManualHoleFacing(DefectHotspot hotspot)
        {
            var body = hotspot.GetComponentInParent<Rigidbody>();
            if (body == null)
                throw new InvalidOperationException($"{hotspot.name} is not attached to a helmet rigidbody.");
            var shell = body.GetComponentsInChildren<Collider>(true)
                .Where(item => !item.isTrigger && item.attachedRigidbody == body)
                .OrderByDescending(item => item.bounds.size.sqrMagnitude).FirstOrDefault();
            if (shell == null)
                throw new InvalidOperationException($"{hotspot.name} has no solid helmet collider.");
            var outward = (hotspot.MeasuredCenter - shell.bounds.center).normalized;
            if (outward.sqrMagnitude < 0.5f)
                throw new InvalidOperationException($"{hotspot.name} is at the helmet center, not on its surface.");
            var facing = Vector3.Dot(hotspot.MarkerNormal, outward);
            if (facing > 0.1f)
                return;
            hotspot.transform.rotation = Quaternion.FromToRotation(hotspot.MarkerNormal, outward) * hotspot.transform.rotation;
            EditorUtility.SetDirty(hotspot.transform);
            Debug.Log($"[HotspotSync] Corrected inward scan direction for {hotspot.name} " +
                      $"(outward alignment was {facing:F3}); its position and radius are unchanged.");
        }

        // Batch entry point: omit -quit; the Play Mode verifier exits after its checks.
        public static void SynchronizeAndVerify()
        {
            Synchronize();
            HeldItemLocomotionVerifier.Verify();
        }

        static int NumberFromName(string objectName)
        {
            var match = Regex.Match(objectName ?? string.Empty, @"^A2-D(?<number>\d+)");
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var value))
                throw new InvalidOperationException($"Hotspot '{objectName}' must start with A2-Dxx.");
            return value;
        }

        static string TitleFromName(string objectName)
        {
            var separator = objectName.IndexOf(" - ", StringComparison.Ordinal);
            return separator >= 0 && separator + 3 < objectName.Length
                ? objectName.Substring(separator + 3).Trim()
                : objectName;
        }
    }
}
