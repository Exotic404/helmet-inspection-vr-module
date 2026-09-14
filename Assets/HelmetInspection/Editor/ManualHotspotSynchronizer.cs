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
                var exact = existing.FirstOrDefault(item => string.Equals(item.title, title, StringComparison.Ordinal));
                var sphere = hotspot.GetComponent<SphereCollider>();
                if (sphere == null)
                    throw new InvalidOperationException($"{hotspot.name} is missing its SphereCollider.");
                sphere.isTrigger = true;

                var serializedHotspot = new SerializedObject(hotspot);
                serializedHotspot.FindProperty("defectIndex").intValue = i;
                serializedHotspot.FindProperty("session").objectReferenceValue = session;
                var ownHalo = hotspot.GetComponentsInChildren<MeshRenderer>(true)
                    .FirstOrDefault(item => item.name.IndexOf("Halo", StringComparison.OrdinalIgnoreCase) >= 0);
                if (ownHalo == null)
                    throw new InvalidOperationException($"{hotspot.name} is missing its own halo renderer.");
                serializedHotspot.FindProperty("haloRenderer").objectReferenceValue = ownHalo;
                serializedHotspot.FindProperty("isHole").boolValue = exact != null && exact.IsHole;
                serializedHotspot.FindProperty("idleColor").colorValue = new Color(0.12f, 0.78f, 0.92f, 0f);
                serializedHotspot.FindProperty("foundColor").colorValue = new Color(0.22f, 1f, 0.48f, 1f);
                serializedHotspot.ApplyModifiedPropertiesWithoutUndo();
                ownHalo.enabled = false;
                EditorUtility.SetDirty(sphere);
                EditorUtility.SetDirty(ownHalo);

                hotspot.name = $"A2-D{i + 1:00} - {title}";
                var normal = (hotspot.transform.localRotation * Vector3.forward).normalized;
                records.Add(new DefectRecord
                {
                    id = $"A2-D{i + 1:00}",
                    title = title,
                    category = exact?.category ?? DefectCategory.LocalDeformation,
                    severity = exact?.severity ?? DefectSeverity.Advisory,
                    localPosition = hotspot.transform.localPosition,
                    localNormal = normal.sqrMagnitude > 0.5f ? normal : Vector3.up,
                    markerRadius = sphere.radius,
                    deviationMillimeters = exact?.deviationMillimeters ?? (2.1f + (i % 5) * 0.1f),
                    inspectionNote = exact?.inspectionNote ??
                        "Manually authored A2 inspection finding. Its marker is attached directly to the helmet and follows the shell while handled.",
                    correctiveAction = exact?.correctiveAction ??
                        "Place the helmet on quality hold and document this independent surface finding before release.",
                    sourceVertex = exact?.sourceVertex ?? -1,
                    sourceClusterSize = exact?.sourceClusterSize ?? 2
                });
                EditorUtility.SetDirty(hotspot);
            }

            defectSet.SetEditorData(2f, records);
            EditorUtility.SetDirty(defectSet);

            var serializedSession = new SerializedObject(session);
            var list = serializedSession.FindProperty("hotspots");
            list.arraySize = hotspots.Length;
            for (var i = 0; i < hotspots.Length; ++i)
                list.GetArrayElementAtIndex(i).objectReferenceValue = hotspots[i];
            serializedSession.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(session);

            var buttons = UnityEngine.Object.FindObjectsByType<MechanicalTrainingButtonBase>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var button in buttons)
            {
                button.AlignInteractionColliderToVisibleCap();
                EditorUtility.SetDirty(button.GetComponent<BoxCollider>());
            }

            foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text.name == "Intro Body")
                    text.text = Regex.Replace(text.text, @"Log all \d+ findings", $"Log all {hotspots.Length} findings");
                else if (text.name == "Progress")
                    text.text = $"QA FINDINGS  00 / {hotspots.Length:00}";
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
            Debug.Log($"[HotspotSync] Synchronized {hotspots.Length} independent defects, hid undiscovered halos, " +
                      $"and aligned {buttons.Length} button hitboxes without changing manual transforms.");
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
